using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using TopPos.Kiosk.Workflow;
using Xunit.Abstractions;

namespace TopPos.E2E.Tests.Infrastructure
{
	/// <summary>
	/// Jedan E2E test = jedna sesija: nova kopija sandbox-a i restart svih procesa (simulatori → klijent), na kraju gašenje
	/// i čekanje izlaska. Simulatori nose stanje koje reset ne čisti pouzdano (POS podešavanja i kvarovi, blokirana vrata,
	/// zahtevi u toku), pa je restart jedina pouzdana granica izolacije (doc/plan-e2e-ui-testing.md u v2, sekcija 3.7).
	/// v4: server i paketomat su jedan proces <c>TopPos.Simulators both</c> na slobodnim portovima (READY red ih javlja), pa
	/// se konfiguracija klijenta piše tek kad simulatori rade; uz <c>--parent-pid</c> simulatori ne nadžive test proces.
	/// </summary>
	public sealed class KioskSession : IAsyncDisposable
	{
		private static readonly TimeSpan __simulatorStartTimeout = TimeSpan.FromSeconds(30);
		private static readonly TimeSpan __clientStartTimeout = TimeSpan.FromSeconds(60);
		private static readonly TimeSpan __exitTimeout = TimeSpan.FromSeconds(15);
		private static readonly TimeSpan __sessionLockTimeout = TimeSpan.FromMinutes(5);

		private const string KioskProcess = "TopPos.Kiosk";
		private const string SimulatorsProcess = "TopPos.Simulators";
		/// <summary>Windows: isti mutex kao v2 klijent (WindowsSingleInstanceGuard.V2MutexName).</summary>
		private const string KioskMutex = "TopPos.Client.WPF.SingleInstance";
		/// <summary>Linux: LinuxSingleInstanceGuard (/run/lock, inače /tmp).</summary>
		private static readonly string __kioskLockFile = Path.Combine(System.IO.Directory.Exists("/run/lock") ? "/run/lock" : "/tmp", "toppos-kiosk.lock");

		private readonly ITestOutputHelper _output;
		// Process objekti iz Start-a, do kraja sesije: preko njih se gasi i oni se Dispose-uju. Neoslobođena ručka drži završen
		// proces u spisku procesa, pa ga je sledeći test video kao zaostali (lažna uzbuna, 2026-10-08).
		private readonly List<(Process Process, string Name)> _processes = [];
		private readonly KioskOptions? _options;
		private Process? _client;
		private Process? _simulators;
		private StreamWriter? _simulatorsLog;
		private bool _disposed;
		private FileStream? _sessionLock;

		private KioskSession(E2ESettings settings, string directory, KioskOptions? options, ITestOutputHelper output)
		{
			Settings = settings;
			Directory = directory;
			_options = options;
			_output = output;
			Log = new ClientLog(ClientDirectory);
			Data = new TestData(settings, options?.Layout ?? LockerLayout.Ncu24L);
		}

		public E2ESettings Settings { get; }
		public string Directory { get; }
		public string SimulatorsDirectory => Path.Combine(Directory, "simulators");
		public string ClientDirectory => Path.Combine(Directory, "client");

		/// <summary>Kontrolni API simulatora servera; postoji kad simulatori jave da su spremni.</summary>
		public ServerSim Server { get; private set; } = default!;
		/// <summary>Kontrolni API simulatora paketomata; postoji kad simulatori jave da su spremni.</summary>
		public LockerSim Locker { get; private set; } = default!;
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
				session = new KioskSession(settings, directory, options, output) { _sessionLock = sessionLock };
			}
			catch {
				sessionLock.Dispose();
				throw;
			}
			// Od ovde bravu drži sesija: svaki izlaz kroz izuzetak mora proći kroz DisposeAsync.
			try {
				output.WriteLine($"Sandbox: {session.Directory}");
				session.PrepareSandbox();
				session.Trace("sandbox spreman");
				await session.StartProcessesAsync().ConfigureAwait(false);
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
		/// Gasi procese klijenta i simulatora čiji je program ispod <paramref name="directory"/>: pri startu u celom sandbox
		/// korenu (zaostali iz prekinutog pokretanja — prekinut test proces ne stigne do DisposeAsync), na kraju sesije u
		/// sandbox-u testa (ono što gašenje nije ugasilo). Razvojnu instancu van sandbox-a ne dira; nju odbija
		/// <see cref="EnsureNothingRunningAsync"/>.
		/// </summary>
		private static async Task KillProcessesUnderAsync(string directory, Action<string> report)
		{
			var root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
			var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
			foreach (var name in new[] { KioskProcess, SimulatorsProcess }) {
				foreach (var process in Process.GetProcessesByName(name)) {
					using (process) {
						try {
							// Izašao proces ostaje u spisku dok neko drži ručku na njega — nije zaostao i ne prijavljuje se.
							if (!process.HasExited
								&& process.MainModule?.FileName is { } path && path.StartsWith(root, comparison)) {
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
		/// Zaštita od druge instance kioska ne sme biti zauzeta (inače se novi proces tiho ugasi). Kratko se čeka, jer
		/// zaštita upravo ugašenog procesa (zaostali ili prethodni test) ne nestaje istog trenutka.
		/// </summary>
		private static async Task EnsureNothingRunningAsync()
		{
			try {
				await Poll.UntilAsync(() => !IsKioskInstanceHeld(), TimeSpan.FromSeconds(5), "oslobađanje zaštite od druge instance kioska").ConfigureAwait(false);
			}
			catch (E2ETimeoutException) {
				throw new E2EException($"Već radi kiosk ({(OperatingSystem.IsWindows() ? KioskMutex : __kioskLockFile)}) — zatvoriti razvojnu instancu pre E2E testova.");
			}
		}

		private static bool IsKioskInstanceHeld()
		{
			if (OperatingSystem.IsWindows()) {
				if (!Mutex.TryOpenExisting(KioskMutex, out var mutex)) {
					return false;
				}
				mutex.Dispose();
				return true;
			}
			try {
				// LinuxSingleInstanceGuard drži fajl otvoren ekskluzivno; fajl bez vlasnika ostaje posle izlaska
				using var probe = new FileStream(__kioskLockFile, FileMode.Open, FileAccess.Read, FileShare.None);
				return false;
			}
			catch (FileNotFoundException) {
				return false;
			}
			catch (IOException) {
				return true;
			}
			catch (UnauthorizedAccessException) {
				return false;	// tuđ fajl bez prava čitanja — kiosk ga otvara sam i javlja grešku
			}
		}

		/// <summary>
		/// Vrednosti za {{...}} u šablonima iz Config\. Portovi servera i NCU24L simulatora poznati su tek iz READY reda
		/// (<see cref="StartProcessesAsync"/>); bez njih (0) šablon klijenta se može upisati samo za proveru rasporeda.
		/// </summary>
		public static Dictionary<string, string> ConfigurationTokens(E2ESettings settings, int serverPort = 0, int lockerTcpPort = 0)
			=> new() {
				["ServerPort"] = serverPort.ToString(),
				["LockerTcpPort"] = lockerTcpPort.ToString(),
				["ScannerClientPort"] = settings.ScannerClientPort,
				["ScannerSimulatorPort"] = settings.ScannerSimulatorPort,
				["PosClientPort"] = settings.PosClientPort,
				["PosSimulatorPort"] = settings.PosSimulatorPort,
				// Linux: simulator pravi pty par i symlink na putanji koju otvara klijent; Windows: com0com, polje prazno
				["ScannerKioskPort"] = settings.UsesVirtualSerialPairs ? settings.ScannerClientPort : "",
				["PosKioskPort"] = settings.UsesVirtualSerialPairs ? settings.PosClientPort : "",
				["LockerKioskPort"] = settings.UsesVirtualSerialPairs ? settings.LockerClientPort : "",
				["LockerId"] = settings.LockerId,
				["CourierQrCode"] = settings.CourierQrCode,
			};

		public string ServerConfigurationPath => Path.Combine(SimulatorsDirectory, "server.app-configuration.json");
		public string LockerConfigurationPath => Path.Combine(SimulatorsDirectory, "locker.app-configuration.json");

		private void PrepareSandbox()
		{
			var tokens = ConfigurationTokens(Settings);
			Sandbox.CopyBinaries(Settings.SimulatorsOutput, SimulatorsDirectory);
			Sandbox.AssertClean(SimulatorsDirectory);
			Sandbox.WriteConfiguration("server.app-configuration.json", ServerConfigurationPath, tokens, _options?.ConfigureServer);
			Sandbox.WriteConfiguration("locker.app-configuration.json", LockerConfigurationPath, tokens, _options?.ConfigureLocker);

			Sandbox.CopyBinaries(Settings.KioskOutput, ClientDirectory);
			Sandbox.AssertClean(ClientDirectory);
			// Konfiguraciju klijenta i pripremu foldera radi StartProcessesAsync (portovi simulatora).
		}

		private async Task StartProcessesAsync()
		{
			var ready = await StartSimulatorsAsync().ConfigureAwait(false);
			int serverPort = ready.GetProperty("server").GetProperty("port").GetInt32();
			var locker = ready.GetProperty("locker");
			int lockerTcpPort = locker.GetProperty("tcpPort").ValueKind == JsonValueKind.Number ? locker.GetProperty("tcpPort").GetInt32() : 0;
			Server = new ServerSim(serverPort);
			Locker = new LockerSim(locker.GetProperty("controlApiPort").GetInt32());
			Trace($"simulatori gore: {ready.GetRawText()}");

			Sandbox.WriteConfiguration("client.app-configuration.json", Path.Combine(ClientDirectory, "app-configuration.json"),
										ConfigurationTokens(Settings, serverPort, lockerTcpPort), _options?.ConfigureClient);
			// Posle AssertClean i upisa konfiguracije: priprema sme da upiše i runtime fajlove koje provera inače odbija.
			_options?.PrepareClientDirectory?.Invoke(ClientDirectory);

			if (_options?.BeforeClientStartAsync is { } beforeClientStart) {
				await beforeClientStart(this).ConfigureAwait(false);
				Trace("priprema pre starta klijenta završena");
			}
			await StartClientAsync(_options?.StartsOutOfOrder == true).ConfigureAwait(false);
		}

		/// <summary>
		/// <c>TopPos.Simulators both</c>; čeka READY red (portovi, serijski portovi klijenta). Izlaz simulatora ide u
		/// simulators.log u sandbox-u (stderr je njihov log).
		/// </summary>
		private async Task<JsonElement> StartSimulatorsAsync()
		{
			var info = new ProcessStartInfo(Executable(SimulatorsDirectory, SimulatorsProcess)) {
				WorkingDirectory = SimulatorsDirectory,
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
			};
			foreach (var arg in new[] { "both", "--server-config", ServerConfigurationPath, "--locker-config", LockerConfigurationPath,
										"--parent-pid", Environment.ProcessId.ToString() }) {
				info.ArgumentList.Add(arg);
			}
			var readyLine = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
			var recent = new Queue<string>();	// za poruku kad start ne uspe (fajl je samo uz to)
			_simulatorsLog = TryOpenLog(Path.Combine(Directory, "simulators.log"));
			// Poziva se sa ThreadPool niti procesa: izuzetak odavde bi oborio testhost (i preskočio gašenje) — ne sme da baci.
			void Append(string? line, bool stdout)
			{
				if (line == null) {
					return;
				}
				if (stdout && line.StartsWith("READY ", StringComparison.Ordinal)) {
					readyLine.TrySetResult(line["READY ".Length..]);
				}
				lock (recent) {
					recent.Enqueue(line);
					if (recent.Count > 200) {
						recent.Dequeue();
					}
					try {
						_simulatorsLog?.WriteLine(line);
					}
					catch (Exception) {
						// pun disk i slično: log simulatora je dijagnostika, ne uslov testa
					}
				}
			}
			var process = new Process { StartInfo = info };
			process.OutputDataReceived += (_, e) => Append(e.Data, stdout: true);
			process.ErrorDataReceived += (_, e) => Append(e.Data, stdout: false);
			if (!process.Start()) {
				throw new E2EException($"Pokretanje {SimulatorsProcess} nije uspelo");
			}
			_simulators = process;
			_processes.Add((process, SimulatorsProcess));
			process.BeginOutputReadLine();
			process.BeginErrorReadLine();

			var exited = process.WaitForExitAsync();
			var done = await Task.WhenAny(readyLine.Task, exited, Task.Delay(__simulatorStartTimeout)).ConfigureAwait(false);
			if (done != readyLine.Task) {
				string log;
				lock (recent) {
					log = string.Join(Environment.NewLine, recent);
				}
				throw new E2EException(done == exited
					? $"Simulatori su izašli pre READY (exit code {process.ExitCode}):{Environment.NewLine}{log}"
					: $"Simulatori nisu javili READY za {__simulatorStartTimeout.TotalSeconds:0} s:{Environment.NewLine}{log}");
			}
			return JsonDocument.Parse(await readyLine.Task.ConfigureAwait(false)).RootElement.Clone();
		}

		/// <summary>simulators.log u sandbox-u; deljen za čitanje dok testovi rade. Null ako ne može da se otvori.</summary>
		private static StreamWriter? TryOpenLog(string path)
		{
			try {
				return new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { AutoFlush = true };
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
				return null;
			}
		}

		/// <summary>
		/// Restart samo klijenta u istom sandbox-u — simulatori rade dalje, fajlovi stanja klijenta ostaju.
		/// <paramref name="hard"/> = Kill (glumi pad programa ili nestanak struje); inače uredno gašenje
		/// (<see cref="CloseClientWindow"/>). Posle restarta <see cref="Ui"/> je nova veza; log se nastavlja u istom dnevnom
		/// fajlu, pa kursori uzeti pre restarta ostaju važeći.
		/// </summary>
		/// <param name="afterExit">Poziva se posle izlaska starog procesa, pre starta novog (npr. čitanje fajlova koje novi menja).</param>
		public async Task RestartClientAsync(bool hard, Action? afterExit = null)
		{
			var old = _client ?? throw new E2EException("Klijent nije pokrenut.");
			Trace($"restart klijenta: {(hard ? "kill" : "uredno gašenje")}, pid {old.Id}");
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
				throw new E2EException($"Klijent (pid {old.Id}) nije izašao za {__exitTimeout.TotalSeconds:0} s posle {(hard ? "Kill-a" : "zahteva za gašenje")}.");
			}
			Ui.Dispose();
			_processes.RemoveAll(p => ReferenceEquals(p.Process, old));
			old.Dispose();
			afterExit?.Invoke();
			await StartClientAsync().ConfigureAwait(false);
		}

		/// <summary>
		/// Počinje uredno gašenje klijenta; ne čeka izlazak procesa. Windows: WM_CLOSE glavnom prozoru (v2; ne Esc — taster
		/// stiže samo prozoru u prvom planu). v4, Linux: SIGTERM, kao systemd.
		/// </summary>
		public void CloseClientWindow()
		{
			var client = _client ?? throw new E2EException("Klijent nije pokrenut.");
			if (!OperatingSystem.IsWindows()) {
				Terminate(client);
				return;
			}
			client.Refresh();	// MainWindowHandle se kešira; prozor je nastao posle Start-a
			if (!client.CloseMainWindow()) {
				throw new E2EException($"Klijent (pid {client.Id}) nema glavni prozor za zatvaranje.");
			}
		}

		/// <param name="outOfOrder">Kontroler ne radi od starta: StartPage se ne prikazuje (<see cref="KioskOptions.StartsOutOfOrder"/>).</param>
		private async Task StartClientAsync(bool outOfOrder = false)
		{
			int logMark = Log.Mark();
#if !WINDOWS
			if (OperatingSystem.IsLinux()) {
				await AtSpiKioskUi.EnableAccessibilityAsync(Trace).ConfigureAwait(false);
			}
#endif
			_client = Process.Start(new ProcessStartInfo(Executable(ClientDirectory, KioskProcess)) {
				WorkingDirectory = ClientDirectory,
				UseShellExecute = false,
			}) ?? throw new E2EException($"Pokretanje {KioskProcess} nije uspelo");
			_processes.Add((_client, KioskProcess));
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

		/// <summary>Program klijenta u sandbox-u (npr. za drugu instancu).</summary>
		public string ClientExecutable => Executable(ClientDirectory, KioskProcess);

		/// <summary>Program iz build izlaza (apphost): <c>X.exe</c> na Windows-u, <c>X</c> na Linuxu.</summary>
		private static string Executable(string directory, string name)
			=> Path.Combine(directory, OperatingSystem.IsWindows() ? name + ".exe" : name);

		/// <summary>Linux: SIGTERM (uredno gašenje, kao systemd).</summary>
		private static void Terminate(Process process)
		{
			if (kill(process.Id, __sigterm) != 0) {
				throw new E2EException($"SIGTERM procesu {process.Id} nije uspeo (errno {Marshal.GetLastPInvokeError()}).");
			}
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

		/// <summary>Gasi sve procese i čeka da izađu. Ne baca (poziva se i iz catch-a pri neuspelom startu).</summary>
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
					Trace($"zatvaranje UI veze nije uspelo: {ex.Message}");
				}
				// Obrnutim redom: klijent prvo (da ne prijavljuje greške dok simulatori nestaju).
				foreach (var (process, name) in Enumerable.Reverse(_processes)) {
					int id = process.Id;
					try {
						if (!process.HasExited) {
							// v4, Linux: simulatori uredno (SIGTERM), da obrišu symlink-ove pty parova; ako ne izađu — Kill
							if (!OperatingSystem.IsWindows() && ReferenceEquals(process, _simulators)) {
								Terminate(process);
								using var graceful = new CancellationTokenSource(TimeSpan.FromSeconds(5));
								try {
									await process.WaitForExitAsync(graceful.Token).ConfigureAwait(false);
								}
								catch (OperationCanceledException) {
									Trace($"{name} (pid {id}) nije izašao posle SIGTERM — Kill");
								}
							}
							if (!process.HasExited) {
								process.Kill(entireProcessTree: true);
							}
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
				Server?.Dispose();
				Locker?.Dispose();
				DisposeSimulatorsLog();
				_sessionLock?.Dispose();	// tek posle gašenja procesa: sledeća sesija zatiče slobodnu zaštitu kioska
			}
		}


		private void DisposeSimulatorsLog()
		{
			// procesi su ugašeni; zakasneli callback izlaza dobija ObjectDisposedException, koji Append guta
			try {
				_simulatorsLog?.Dispose();
			}
			catch (Exception) {
			}
			_simulatorsLog = null;
		}

		private const int __sigterm = 15;

		[DllImport("libc", SetLastError = true)]
		private static extern int kill(int pid, int sig);
	}
}
