using System.Diagnostics;
using System.Text.Json.Nodes;
using TopPos.Client.WPF.Workflow;
using Xunit.Abstractions;

namespace TopPos.E2E.Tests.Infrastructure
{
	/// <summary>
	/// Jedan E2E test = jedna sesija: nova kopija sandbox-a i restart sva tri procesa (ServerSimulator →
	/// LockerSimulator → klijent), na kraju gašenje sva tri i čekanje izlaska pre ponovne upotrebe portova.
	/// Simulatori nose stanje koje reset ne čisti pouzdano (POS podešavanja i kvarovi, blokirana vrata, zahtevi u
	/// toku), pa je restart jedina pouzdana granica izolacije (doc/plan-e2e-ui-testing.md, sekcija 3.7).
	/// </summary>
	public sealed class KioskSession : IAsyncDisposable
	{
		private static readonly TimeSpan __simulatorStartTimeout = TimeSpan.FromSeconds(20);
		private static readonly TimeSpan __clientStartTimeout = TimeSpan.FromSeconds(60);
		private static readonly TimeSpan __exitTimeout = TimeSpan.FromSeconds(15);
		private static readonly TimeSpan __sessionLockTimeout = TimeSpan.FromMinutes(5);

		private static readonly string[] __singleInstanceMutexes = [
			"TopPos.Client.WPF.SingleInstance",
			"TopPos.ServerSimulator.SingleInstance",
			"TopPos.LockerSimulator.SingleInstance",
		];

		private readonly ITestOutputHelper _output;
		// Process objekti iz Start-a, do kraja sesije: preko njih se gasi i oni se Dispose-uju. Neoslobođena ručka drži završen
		// proces u spisku procesa, pa ga je sledeći test video kao zaostali (lažna uzbuna, 2026-10-08).
		private readonly List<(Process Process, string Name)> _processes = [];
		private Process? _client;
		private bool _disposed;
		private FileStream? _sessionLock;

		private KioskSession(E2ESettings settings, string directory, LockerLayout layout, ITestOutputHelper output)
		{
			Settings = settings;
			Directory = directory;
			_output = output;
			Server = new ServerSim(settings.ServerPort);
			Locker = new LockerSim(settings.LockerApiPort);
			Log = new ClientLog(ClientDirectory);
			Data = new TestData(settings, layout);
		}

		public E2ESettings Settings { get; }
		public string Directory { get; }
		public string ServerDirectory => Path.Combine(Directory, "server");
		public string LockerDirectory => Path.Combine(Directory, "locker");
		public string ClientDirectory => Path.Combine(Directory, "client");

		public ServerSim Server { get; }
		public LockerSim Locker { get; }
		public ClientLog Log { get; }
		public KioskUi Ui { get; private set; } = default!;
		public TestData Data { get; }

		/// <summary>
		/// Korak testa u e2e-trace.log u sandbox-u (odmah na disk) i u izlaz testa — izlaz xUnit-a se vidi tek na kraju,
		/// a trace pokazuje dokle je test stigao i kad visi.
		/// </summary>
		public void Trace(string message)
		{
			var line = $"{DateTime.Now:HH:mm:ss.fff} {message}";
			try {
				File.AppendAllText(Path.Combine(Directory, "e2e-trace.log"), line + Environment.NewLine);
				_output.WriteLine(line);
			}
			catch (Exception) {
				// trace ne sme da obori test (npr. izlaz testa posle završetka)
			}
		}

		/// <summary>PID pokrenutog klijenta (posle restarta novog procesa).</summary>
		public int ClientProcessId => (_client ?? throw new E2EException("Klijent nije pokrenut.")).Id;

		/// <summary>Klijent je izašao sam (npr. gašenje na zahtev Agenta).</summary>
		public bool ClientHasExited => _client is { HasExited: true };

		/// <summary>Pada odmah ako je klijent ugašen (izuzetak, Esc...), umesto da čeka timeout.</summary>
		public void EnsureClientAlive()
		{
			if (_client is { HasExited: true }) {
				throw new E2EException($"Klijent je ugašen (exit code {_client.ExitCode}). Poslednje linije loga:{Environment.NewLine}{Log.Tail(20)}");
			}
		}

		/// <summary>
		/// Pokreće sesiju, izvršava scenario i uvek gasi procese; pri padu u izlaz testa ide snimak ekrana i rep loga.
		/// Prvi red scenarija je <see cref="E2ESettings.RequireEnabled"/> (preskakanje bez E2E okruženja).
		/// </summary>
		public static Task RunAsync(string testName, ITestOutputHelper output, Func<KioskSession, Task> scenario)
			=> RunAsync(testName, output, null, scenario);

		/// <inheritdoc cref="RunAsync(string, ITestOutputHelper, Func{KioskSession, Task})"/>
		/// <param name="options">Podešavanje sandbox-a za ovaj test (konfiguracija, fajlovi pre starta); null = zajednički šabloni.</param>
		public static async Task RunAsync(string testName, ITestOutputHelper output, KioskOptions? options, Func<KioskSession, Task> scenario)
		{
			E2ESettings.RequireEnabled();
			ScreenCheck.EnsureClientWindowFits();
			await using var kiosk = await StartAsync(testName, output, options).ConfigureAwait(false);
			try {
				await scenario(kiosk).ConfigureAwait(false);
				await kiosk.AssertOwnershipInvariantsAsync().ConfigureAwait(false);
			}
			catch {
				kiosk.DumpDiagnostics();
				throw;
			}
		}

		/// <summary>
		/// Opšte provere posle svakog scenarija (koordinator, faza 2 kanala ka Agentu): nijedan ulaz nije napustio Start bez
		/// rezervacije, nijedna rezervacija se nije završavala predugo, a kiosk koji je na kraju na Start-u za kratko postaje
		/// slobodan i u status.lock (vlasnik se oslobađa posle čišćenja). Ove greške ne prekidaju tok, pa ih scenario sam ne vidi.
		/// </summary>
		private async Task AssertOwnershipInvariantsAsync()
		{
			var unreserved = Log.LinesAfter(0, "napustila Start bez rezervacije");
			if (unreserved.Count > 0) {
				throw new E2EException($"Ulaz je napustio Start bez rezervacije koordinatora:{Environment.NewLine}{string.Join(Environment.NewLine, unreserved)}");
			}
			var slow = Log.LinesAfter(0, "se završava duže od");
			if (slow.Count > 0) {
				throw new E2EException($"Rezervacija se završavala predugo:{Environment.NewLine}{string.Join(Environment.NewLine, slow)}");
			}
			if (_client is not { HasExited: false }) {
				return;     // scenario je ugasio klijent (gašenje, druga instanca)
			}
			var last = Log.TransitionsAfter(0).LastOrDefault();
			if (last != null && last.To != WorkflowState.Start) {
				return;     // scenario namerno završava usred toka
			}
			await Poll.UntilAsync(() => ReadStatus()?["Busy"]?.GetValue<bool>() == false, TimeSpan.FromSeconds(10),
								"status.lock: Busy=false na Start-u posle kraja scenarija", EnsureClientAlive).ConfigureAwait(false);
			Trace("provera vlasništva: kiosk slobodan na Start-u, nema ulaza bez rezervacije");
		}

		/// <summary>status.lock klijenta, ili null dok ga nema ili je usred zamene (čita se uz FileShare.Delete).</summary>
		public JsonNode? ReadStatus()
		{
			try {
				using var stream = new FileStream(Path.Combine(ClientDirectory, "status.lock"), FileMode.Open, FileAccess.Read,
													FileShare.ReadWrite | FileShare.Delete);
				return JsonNode.Parse(stream);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) {
				return null;
			}
		}

		public static async Task<KioskSession> StartAsync(string testName, ITestOutputHelper output, KioskOptions? options = null)
		{
			var settings = E2ESettings.Current;
			// Brava pre čišćenja: bez nje bi drugo pokretanje (drugi worktree, drugi runner) procese aktivne sesije
			// proglasilo zaostalim i ubilo ih. Drži se do kraja DisposeAsync.
			var sessionLock = await AcquireSessionLockAsync(settings.SandboxRoot, output).ConfigureAwait(false);
			KioskSession session;
			try {
				await KillProcessesUnderAsync(settings.SandboxRoot,
											path => output.WriteLine($"Gasim zaostali proces iz prekinutog pokretanja: {path}")).ConfigureAwait(false);
				await EnsureNothingRunningAsync().ConfigureAwait(false);

				Sandbox.DeleteStaleRuns(settings.SandboxRoot);
				var directory = Path.Combine(settings.SandboxRoot, $"{DateTime.Now:yyyyMMdd-HHmmss}-{testName}");
				session = new KioskSession(settings, directory, options?.Layout ?? LockerLayout.Ncu24L, output) { _sessionLock = sessionLock };
			}
			catch {
				sessionLock.Dispose();
				throw;
			}
			// Od ovde bravu drži sesija: svaki izlaz kroz izuzetak mora proći kroz DisposeAsync.
			try {
				output.WriteLine($"Sandbox: {session.Directory}");
				session.PrepareSandbox(options);
				session.Trace("sandbox spreman");
				await session.StartProcessesAsync(options).ConfigureAwait(false);
			}
			catch (Exception ex) {
				try {
					session.Trace($"start nije uspeo: {ex}");
					session.DumpDiagnostics();
				}
				finally {
					await session.DisposeAsync().ConfigureAwait(false);	// ne baca — originalna greška ostaje vidljiva
				}
				throw;
			}
			return session;
		}

		/// <summary>
		/// Međuprocesna brava za E2E sesiju: lock fajl u sandbox korenu otvoren ekskluzivno. OS ga oslobađa i kad
		/// runner pukne (nema „zaglavljene" brave). Drugo pokretanje čeka (testovi dva runner-a se smenjuju).
		/// </summary>
		private static async Task<FileStream> AcquireSessionLockAsync(string sandboxRoot, ITestOutputHelper output)
		{
			System.IO.Directory.CreateDirectory(sandboxRoot);
			var path = Path.Combine(sandboxRoot, "session.lock");
			bool reported = false;
			try {
				return await Poll.UntilAsync(() => {
									try {
										return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
									}
									catch (IOException) {
										if (!reported) {
											reported = true;
											output.WriteLine($"Čekam da se završi druga E2E sesija ({path})...");
										}
										return null;
									}
								},
								__sessionLockTimeout, "E2E brava sesije").ConfigureAwait(false);
			}
			catch (E2ETimeoutException) {
				throw new E2EException($"Druga E2E sesija drži {path} duže od {__sessionLockTimeout.TotalMinutes:0} min.");
			}
		}

		/// <summary>
		/// Gasi procese klijenta i simulatora čiji je exe ispod <paramref name="directory"/>: pri startu u celom sandbox korenu
		/// (zaostali iz prekinutog pokretanja — prekinut test proces ne stigne do DisposeAsync), na kraju sesije u sandbox-u
		/// testa (ono što gašenje nije ugasilo). Razvojnu instancu van sandbox-a ne dira; nju odbija
		/// <see cref="EnsureNothingRunningAsync"/>.
		/// </summary>
		private static async Task KillProcessesUnderAsync(string directory, Action<string> report)
		{
			var root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
			foreach (var name in new[] { "TopPos.Client.WPF", "TopPos.LockerSimulator", "TopPos.ServerSimulator" }) {
				foreach (var process in Process.GetProcessesByName(name)) {
					using (process) {
						try {
							// Izašao proces ostaje u spisku dok neko drži ručku na njega — nije zaostao i ne prijavljuje se.
							if (!process.HasExited
								&& process.MainModule?.FileName is { } path && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) {
								report($"{path} (pid {process.Id})");
								process.Kill(entireProcessTree: true);
								using var cts = new CancellationTokenSource(__exitTimeout);
								try {
									await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
								}
								catch (OperationCanceledException) {
									report($"{path} (pid {process.Id}) nije izašao za {__exitTimeout.TotalSeconds:0} s posle Kill-a");
								}
							}
						}
						catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) {
							// proces je u međuvremenu izašao ili mu se ne može pristupiti — EnsureNothingRunning odlučuje dalje
						}
					}
				}
			}
		}

		/// <summary>
		/// Single-instance mutex-i ne smeju postojati (inače se novi proces tiho ugasi). Kratko se čeka, jer mutex
		/// upravo ugašenog procesa (zaostali ili prethodni test) ne nestaje istog trenutka.
		/// </summary>
		private static async Task EnsureNothingRunningAsync()
		{
			string? held = null;
			try {
				await Poll.UntilAsync(() => (held = __singleInstanceMutexes.FirstOrDefault(IsMutexHeld)) == null,
										TimeSpan.FromSeconds(5), "oslobađanje single-instance mutex-a").ConfigureAwait(false);
			}
			catch (E2ETimeoutException) {
				throw new E2EException($"Već radi proces sa mutex-om {held} — zatvoriti razvojnu instancu pre E2E testova.");
			}
		}

		private static bool IsMutexHeld(string name)
		{
			if (!Mutex.TryOpenExisting(name, out var mutex)) {
				return false;
			}
			mutex.Dispose();
			return true;
		}

		/// <summary>Vrednosti za {{...}} u šablonima iz Config\.</summary>
		public static Dictionary<string, string> ConfigurationTokens(E2ESettings settings)
			=> new() {
				["ServerPort"] = settings.ServerPort.ToString(),
				["LockerTcpPort"] = settings.LockerTcpPort.ToString(),
				["LockerApiPort"] = settings.LockerApiPort.ToString(),
				["ScannerClientPort"] = settings.ScannerClientPort,
				["ScannerSimulatorPort"] = settings.ScannerSimulatorPort,
				["PosClientPort"] = settings.PosClientPort,
				["PosSimulatorPort"] = settings.PosSimulatorPort,
				["LockerId"] = settings.LockerId,
				["CourierQrCode"] = settings.CourierQrCode,
			};

		private void PrepareSandbox(KioskOptions? options)
		{
			var tokens = ConfigurationTokens(Settings);
			foreach (var (project, directory, template, configure) in new (string, string, string, Action<JsonObject>?)[] {
						("TopPos.ServerSimulator", ServerDirectory, "server.app-configuration.json", options?.ConfigureServer),
						("TopPos.LockerSimulator", LockerDirectory, "locker.app-configuration.json", options?.ConfigureLocker),
						("TopPos.Client.WPF", ClientDirectory, "client.app-configuration.json", options?.ConfigureClient) }) {
				Sandbox.CopyBinaries(Settings.ProjectOutput(project), directory);
				Sandbox.AssertClean(directory);
				Sandbox.WriteConfiguration(template, Path.Combine(directory, "app-configuration.json"), tokens, configure);
			}
			// Posle AssertClean: priprema sme da upiše i runtime fajlove koje provera inače odbija.
			options?.PrepareClientDirectory?.Invoke(ClientDirectory);
		}

		private async Task StartProcessesAsync(KioskOptions? options)
		{
			Start(ServerDirectory, "TopPos.ServerSimulator.exe");
			await Poll.UntilAsync(Server.IsUpAsync, __simulatorStartTimeout, "ServerSimulator /_sim/health").ConfigureAwait(false);
			Trace("ServerSimulator gore");

			Start(LockerDirectory, "TopPos.LockerSimulator.exe");
			await Poll.UntilAsync(Locker.IsUpAsync, __simulatorStartTimeout, "LockerSimulator /_sim/health").ConfigureAwait(false);
			Trace("LockerSimulator gore");

			if (options?.BeforeClientStartAsync is { } beforeClientStart) {
				await beforeClientStart(this).ConfigureAwait(false);
				Trace("priprema pre starta klijenta završena");
			}
			await StartClientAsync(options?.StartsOutOfOrder == true).ConfigureAwait(false);
		}

		/// <summary>
		/// Kraj Windows sesije kao pri odjavi: <c>WM_QUERYENDSESSION</c>, pa <c>WM_ENDSESSION</c> svim top-level prozorima
		/// klijenta (<see cref="WindowsSessionEnd"/>), a odmah posle toga Kill — posle <c>WM_ENDSESSION</c> Windows sme da ugasi
		/// proces u svakom trenutku. Vraća odgovore na <c>WM_QUERYENDSESSION</c>; klijent posle ovoga više ne radi.
		/// </summary>
		public async Task<IReadOnlyList<bool>> EndWindowsSessionAsync()
		{
			var client = _client ?? throw new E2EException("Klijent nije pokrenut.");
			Trace($"kraj Windows sesije (odjava), pid {client.Id}");
			var answers = WindowsSessionEnd.Send(client.Id, __exitTimeout, Trace);
			bool exitedAlone = client.HasExited;
			if (!exitedAlone) {
				client.Kill(entireProcessTree: true);
			}
			try {
				using var cts = new CancellationTokenSource(__exitTimeout);
				await client.WaitForExitAsync(cts.Token).ConfigureAwait(false);
			}
			catch (OperationCanceledException) {
				throw new E2EException($"Klijent (pid {client.Id}) nije izašao za {__exitTimeout.TotalSeconds:0} s posle Kill-a.");
			}
			Trace(exitedAlone ? "klijent je izašao pre Kill-a" : "klijent ugašen Kill-om posle WM_ENDSESSION");
			return answers;
		}

		/// <summary>
		/// Restart samo klijenta u istom sandbox-u — simulatori rade dalje, fajlovi stanja klijenta ostaju.
		/// <paramref name="hard"/> = Kill (glumi pad programa ili nestanak struje); inače uredno gašenje zatvaranjem glavnog
		/// prozora (WM_CLOSE → Application.Shutdown → OnExit). Ne Esc: taster stiže samo prozoru u prvom planu, a Windows
		/// procesu testa ne dozvoljava uvek SetForeground. Na VM-u 20.10.2026 (prvi test pokretanja, bez klika mišem pre toga)
		/// klijent posle Esc-a nije ni ušao u OnExit — verovatno je taster otišao drugom prozoru. Posle restarta <see cref="Ui"/> je nova veza;
		/// log se nastavlja u istom dnevnom fajlu, pa kursori uzeti pre restarta ostaju važeći.
		/// </summary>
		/// <param name="afterExit">Poziva se posle izlaska starog procesa, pre starta novog (npr. čitanje fajlova koje novi menja).</param>
		public async Task RestartClientAsync(bool hard, Action? afterExit = null)
		{
			var old = _client ?? throw new E2EException("Klijent nije pokrenut.");
			Trace($"restart klijenta: {(hard ? "kill" : "uredno gašenje (WM_CLOSE)")}, pid {old.Id}");
			if (hard) {
				old.Kill(entireProcessTree: true);
			}
			else {
				CloseClientWindow();
			}
			try {
				using var cts = new CancellationTokenSource(__exitTimeout);
				await old.WaitForExitAsync(cts.Token).ConfigureAwait(false);
			}
			catch (OperationCanceledException) {
				throw new E2EException($"Klijent (pid {old.Id}) nije izašao za {__exitTimeout.TotalSeconds:0} s posle {(hard ? "Kill-a" : "zatvaranja prozora")}.");
			}
			Ui.Dispose();
			_processes.RemoveAll(p => ReferenceEquals(p.Process, old));
			old.Dispose();
			afterExit?.Invoke();
			await StartClientAsync().ConfigureAwait(false);
		}

		/// <summary>Počinje uredno gašenje (WM_CLOSE glavnom prozoru); ne čeka izlazak procesa.</summary>
		public void CloseClientWindow()
		{
			var client = _client ?? throw new E2EException("Klijent nije pokrenut.");
			client.Refresh();	// MainWindowHandle se kešira; prozor je nastao posle Start-a
			if (!client.CloseMainWindow()) {
				throw new E2EException($"Klijent (pid {client.Id}) nema glavni prozor za zatvaranje.");
			}
		}

		/// <param name="outOfOrder">Kontroler ne radi od starta: StartPage se ne prikazuje (<see cref="KioskOptions.StartsOutOfOrder"/>).</param>
		private async Task StartClientAsync(bool outOfOrder = false)
		{
			int logMark = Log.Mark();
			_client = Start(ClientDirectory, "TopPos.Client.WPF.exe");
			Trace($"klijent pokrenut (pid {_client.Id})");
			Ui = await KioskUi.AttachAsync(_client, __clientStartTimeout, EnsureClientAlive, Trace).ConfigureAwait(false);
			if (outOfOrder) {
				await this.WaitForLogLineAsync("Paketomat je u funkciji = [False]", logMark, __clientStartTimeout).ConfigureAwait(false);
				Trace("klijent spreman (paketomat nije u funkciji od starta)");
				return;
			}
			await Ui.WaitForPageAsync("StartPage", __clientStartTimeout).ConfigureAwait(false);

			// Start ekran se pojavi pre kraja inicijalizacije: klijent pri startu dvaput javlja da je paketomat u funkciji
			// (TrySendInitialLockerStatusToUi, pa SendLockerStatusIfChanged posle prve sinhronizacije sa serverom) i
			// svaki put fire-uje Reset. Pritisak u tom prozoru (~0,5 s) bi Reset vratio na Start — zato se čeka da se smiri.
			await Log.WaitForQuietAsync(TimeSpan.FromSeconds(2), __clientStartTimeout, EnsureClientAlive,
										"Workflow:", "OnLockerStatusChanged").ConfigureAwait(false);
			Trace("klijent spreman (inicijalizacija završena)");
		}

		private Process Start(string directory, string exe)
		{
			var process = Process.Start(new ProcessStartInfo(Path.Combine(directory, exe)) {
				WorkingDirectory = directory,	// klijent i simulatori čitaju konfiguraciju iz tekućeg direktorijuma
				UseShellExecute = false,
			}) ?? throw new E2EException($"Pokretanje {exe} nije uspelo");
			_processes.Add((process, exe));
			return process;
		}

		/// <summary>Čeka prelaz u stanje posle kursora, uz proveru da je klijent živ.</summary>
		public async Task<Transition> WaitForStateAsync(WorkflowState state, int after, TimeSpan? timeout = null)
		{
			var transition = await Log.WaitForTransitionAsync(state, after, timeout ?? KioskUi.DefaultTimeout, EnsureClientAlive).ConfigureAwait(false);
			Trace($"prelaz: {transition}");
			return transition;
		}

		/// <summary>Dijagnostika pri padu: snimak ekrana i rep loga u izlaz testa.</summary>
		public void DumpDiagnostics()
		{
			try {
				if (Ui != null) {
					var screenshot = Ui.TryCaptureScreenshot(Path.Combine(Directory, "failure.png"));
					_output.WriteLine($"Snimak ekrana: {screenshot ?? "(nije uspeo)"}");
				}
				_output.WriteLine("Poslednje linije loga klijenta:");
				_output.WriteLine(Log.Tail(40));
			}
			catch (Exception ex) {
				try {
					_output.WriteLine($"Dijagnostika nije uspela: {ex.Message}");
				}
				catch (Exception) {
					// izlaz testa nije dostupan — dijagnostika ne sme da preskoči čišćenje
				}
			}
		}

		/// <summary>Gasi sva tri procesa i čeka da izađu. Ne baca (poziva se i iz catch-a pri neuspelom startu).</summary>
		public async ValueTask DisposeAsync()
		{
			if (_disposed) {
				return;
			}
			_disposed = true;
			try {
				try {
					Ui?.Dispose();
				}
				catch (Exception ex) {
					Trace($"zatvaranje UIA nije uspelo: {ex.Message}");
				}
				// Obrnutim redom: klijent prvo (da ne prijavljuje greške dok simulatori nestaju).
				foreach (var (process, name) in Enumerable.Reverse(_processes)) {
					int id = process.Id;
					try {
						if (!process.HasExited) {
							process.Kill(entireProcessTree: true);
						}
						using var cts = new CancellationTokenSource(__exitTimeout);
						await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
					}
					catch (Exception ex) {
						Trace($"gašenje {name} (pid {id}) nije uspelo: {ex.GetType().Name}: {ex.Message}");
					}
					finally {
						process.Dispose();
					}
				}
				// Provera: iz ovog sandbox-a ne sme ništa da ostane — inače ga sledeći test zatekne kao zaostalog.
				try {
					await KillProcessesUnderAsync(Directory, path => Trace($"posle gašenja još radi, gasim: {path}")).ConfigureAwait(false);
				}
				catch (Exception ex) {
					Trace($"provera posle gašenja nije uspela: {ex.GetType().Name}: {ex.Message}");
				}
				Trace("procesi ugašeni");
			}
			finally {
				Server.Dispose();
				Locker.Dispose();
				_sessionLock?.Dispose();	// tek posle gašenja procesa: sledeća sesija zatiče slobodne portove i mutex-e
			}
		}
	}
}
