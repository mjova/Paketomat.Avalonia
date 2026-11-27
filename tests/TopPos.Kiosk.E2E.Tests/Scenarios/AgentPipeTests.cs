using System.IO;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text.Json.Nodes;
using TopPos.Agent.Contracts;
using TopPos.Kiosk.Workflow;
using TopPos.E2E.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace TopPos.E2E.Tests.Scenarios
{
	/// <summary>
	/// Kanal ka Agentu u pravom klijentu (doc/AgentCommunication.md, faza 1). Test radi pod istim nalogom kao kiosk, pa ne
	/// može da bude dozvoljeni klijent (LocalSystem) — status i pretplatu pokrivaju unit testovi i ručna provera na VM-u
	/// (psexec -s). Ovde: ožičenje u App (start, odbijanje klijenta, gašenje), zauzeto ime i podrazumevano isključen kanal.
	/// v4: na Linuxu je kanal Unix socket (dozvoljen klijent root, test klijent kroz sudo -n, D4); zauzeto ime = drugi vlasnik
	/// <c>agent.v1.lock</c>, a izjava nosi <c>SocketPath</c>, <c>ProcessBootId</c> i <c>ProcessStartTicks</c>.
	/// </summary>
	[Collection(E2ECollection.Name)]
	public sealed class AgentPipeTests(ITestOutputHelper output)
	{
		private static readonly TimeSpan __readTimeout = TimeSpan.FromSeconds(10);

		/// <summary>
		/// Kanal uključen: server se pokreće; klijent pod nalogom kioska prolazi DACL, ali ga provera identiteta odbija pre
		/// Hello. Uredno gašenje zatvara kanal pre kontrolera i zadržava potvrdu urednog gašenja.
		/// </summary>
		[SkippableFact]
		public Task Enabled_StartsServer_RefusesNonSystemClient_AndStopsOnExit()
		{
			var pipeName = NewPipeName();
			return KioskSession.RunAsync(nameof(Enabled_StartsServer_RefusesNonSystemClient_AndStopsOnExit), output, WithPipe(pipeName), async kiosk => {
				await kiosk.WaitForLogLineAsync($"Agent pipe: server pokrenut ({pipeName}", 0);
				AssertDeclaration(kiosk, pipeName);

				var mark = kiosk.Log.Mark();
				Assert.Null(await HelloReplyAsync(pipeName));
				await kiosk.WaitForLogLineAsync("odbijen klijent", mark);
				kiosk.EnsureClientAlive();

				mark = kiosk.Log.Mark();
				await kiosk.RestartClientAsync(hard: false);
				Assert.NotEmpty(kiosk.Log.LinesAfter(mark, "Agent pipe: server zaustavljen."));
				Assert.NotEmpty(kiosk.Log.LinesAfter(mark, "StatusSemaphore: uredno gašenje zabeleženo"));
				await kiosk.WaitForLogLineAsync($"Agent pipe: server pokrenut ({pipeName}", mark);
			});
		}

		/// <summary>Ime pipe-a je zauzeto pre starta kioska: kanal se ne pokreće, kiosk radi dalje.</summary>
		[SkippableFact]
		public async Task NameTakenBeforeStart_KioskRunsWithoutChannel()
		{
			var pipeName = NewPipeName();
			IDisposable? squatter = null;
			var options = WithPipe(pipeName) with {
				BeforeClientStartAsync = _ => {
					squatter = TakeChannel(pipeName);
					return Task.CompletedTask;
				},
			};
			try {
				await KioskSession.RunAsync(nameof(NameTakenBeforeStart_KioskRunsWithoutChannel), output, options, async kiosk => {
					await kiosk.WaitForLogLineAsync($"Agent pipe: ime {pipeName} je već zauzeto", 0);
					Assert.Empty(kiosk.Log.LinesAfter(0, "Agent pipe: server pokrenut"));
					kiosk.EnsureClientAlive();
					// Izjava je po nameri: ostaje i kad server ne proradi, pa Agent vidi nepoznato stanje, ne Busy iz fajla.
					AssertDeclaration(kiosk, pipeName);
				});
			}
			finally {
				squatter?.Dispose();
			}
		}

		/// <summary>
		/// Kanal uključen, ali konfiguracija nije ispravna: server se ne pokreće, a izjava kanala ostaje (neispravna vrednost
		/// ne sme da vrati Agenta na čitanje Busy iz fajla).
		/// </summary>
		[SkippableFact]
		public Task InvalidConfiguration_KeepsDeclaration()
		{
			var pipeName = NewPipeName();
			var options = new KioskOptions {
				ConfigureClient = c => c["AgentCommunication"] = new JsonObject {
					["Enabled"] = true,
					[AgentChannel.AddressField] = pipeName,		// v4
					["HeartbeatIntervalInMilliseconds"] = 70000,
				},
			};
			return KioskSession.RunAsync(nameof(InvalidConfiguration_KeepsDeclaration), output, options, async kiosk => {
				await kiosk.WaitForLogLineAsync("AgentCommunication: konfiguracija nije ispravna", 0);
				Assert.Empty(kiosk.Log.LinesAfter(0, "Agent pipe: server pokrenut"));
				kiosk.EnsureClientAlive();
				AssertDeclaration(kiosk, pipeName);
			});
		}

		/// <summary>Bez sekcije AgentCommunication (sve postojeće konfiguracije) kanal ne postoji.</summary>
		[SkippableFact]
		public Task WithoutSection_ChannelIsDisabled()
			=> KioskSession.RunAsync(nameof(WithoutSection_ChannelIsDisabled), output, async kiosk => {
				await kiosk.WaitForLogLineAsync("AgentCommunication: kanal ka Agentu je isključen.", 0);
				// v4: na Linuxu server se ne pokreće (socket na podrazumevanoj putanji može ostati posle pada nekog ranijeg kioska).
				if (OperatingSystem.IsWindows()) {
					Assert.DoesNotContain(Directory.GetFiles(@"\\.\pipe\"), p => p.EndsWith(AgentProtocol.DefaultPipeName, StringComparison.OrdinalIgnoreCase));
				}
				else {
					Assert.Empty(kiosk.Log.LinesAfter(0, "Agent pipe: server pokrenut"));
				}
				AssertDeclaration(kiosk, pipeName: null);
			});

		/// <summary>
		/// ShutdownApplication od Agenta (LocalSystem kroz psexec) na slobodnom Start-u: Completed, kraj veze i izlazak procesa.
		/// U logu prihvatanje sa SID-om LocalSystem-a, pa izlazak na zahtev Agenta, zatvaranje kanala i kontrolera i završni
		/// upis; status.lock ima CleanShutdown=true uz vlasnika Agent. Trajanje gašenja (iz loga kioska) je u trace-u.
		/// </summary>
		[SkippableFact]
		public Task ShutdownFromLocalSystem_FreeKiosk_ExitsCleanly()
		{
			E2ESettings.RequireEnabled();
			LocalSystemAgentClient.RequireAvailable();
			var pipeName = NewPipeName();
			return KioskSession.RunAsync(nameof(ShutdownFromLocalSystem_FreeKiosk_ExitsCleanly), output, WithPipe(pipeName), async kiosk => {
				await kiosk.WaitForLogLineAsync($"Agent pipe: server pokrenut ({pipeName}", 0);
				int clientProcessId = kiosk.ClientProcessId;
				var mark = kiosk.Log.Mark();

				// Ishod se proverava preko izlaznog koda i loga kioska: stariji PsExec (v1.98) ne prenosi ispis procesa.
				var (exitCode, _) = await LocalSystemAgentClient.ShutdownAsync(kiosk, pipeName, ShutdownPurposes.Restart);

				Assert.True(exitCode == 0, $"test klijent je završio sa {exitCode} (0 = Completed i proces kioska izašao)");
				Assert.True(kiosk.ClientHasExited, "proces kioska mora da izađe posle Completed");
				// Redosled u logu: prihvatanje → izlazak na zahtev Agenta → kanal zatvoren → kontroler → završni upis.
				int accepted = SingleLine(kiosk, mark, $"→ {AgentResultCodes.Completed}");
				Assert.Contains(AgentCommands.ShutdownApplication, kiosk.Log.LinesAfter(mark, $"→ {AgentResultCodes.Completed}")[0]);
				Assert.Contains(AgentChannel.AllowedClientInLog, kiosk.Log.LinesAfter(mark, $"→ {AgentResultCodes.Completed}")[0]);	// v4
				int exitRequested = SingleLine(kiosk, mark, $"Izlazak iz aplikacije na zahtev Agenta ({ShutdownPurposes.Restart}");
				int pipeStopped = SingleLine(kiosk, mark, "Agent pipe: server zaustavljen.");
				int controllerClosing = SingleLine(kiosk, mark, "Izlazak iz aplikacije: zatvaranje veze sa kontrolerom vrata.");
				int cleanShutdown = SingleLine(kiosk, mark, "StatusSemaphore: uredno gašenje zabeleženo");
				Assert.True(accepted < exitRequested && exitRequested < pipeStopped && pipeStopped < controllerClosing && controllerClosing < cleanShutdown,
							$"redosled u logu: prihvatanje {accepted}, izlazak {exitRequested}, kanal {pipeStopped}, kontroler {controllerClosing}, završni upis {cleanShutdown}");
				// Podatak za Agentov rok posle Completed (30 s): koliko traje gašenje do završnog upisa.
				kiosk.Trace($"gašenje: Completed → završni upis {(kiosk.Log.TimeOf(cleanShutdown) - kiosk.Log.TimeOf(accepted)).TotalMilliseconds:0} ms");

				var status = kiosk.ReadStatus() ?? throw new E2EException("status.lock nije čitljiv.");
				Assert.Equal(clientProcessId, status["ProcessId"]!.GetValue<int>());
				Assert.True(status["CleanShutdown"]!.GetValue<bool>());
				Assert.True(status["Busy"]!.GetValue<bool>());
				Assert.Equal("Agent", status["Role"]!.GetValue<string>());
			});
		}

		/// <summary>
		/// ShutdownApplication od Agenta dok je kurir prijavljen (skeniranje za ostavljanje): odmah Busy, kiosk radi dalje, tok
		/// se ne menja, razlog je u logu; kurir se zatim uredno odjavljuje i kiosk postaje slobodan.
		/// </summary>
		[SkippableFact]
		public Task ShutdownFromLocalSystem_CourierSession_RefusedAndSessionContinues()
		{
			E2ESettings.RequireEnabled();
			LocalSystemAgentClient.RequireAvailable();
			var pipeName = NewPipeName();
			return KioskSession.RunAsync(nameof(ShutdownFromLocalSystem_CourierSession_RefusedAndSessionContinues), output, WithPipe(pipeName), async kiosk => {
				await kiosk.WaitForLogLineAsync($"Agent pipe: server pokrenut ({pipeName}", 0);
				await CourierTests.OpenDepositScanAsync(kiosk);
				var mark = kiosk.Log.Mark();

				var (exitCode, _) = await LocalSystemAgentClient.ShutdownAsync(kiosk, pipeName, ShutdownPurposes.Restart);

				Assert.True(exitCode == 5, $"test klijent je završio sa {exitCode} (5 = komanda odbijena)");
				kiosk.EnsureClientAlive();
				var refused = kiosk.Log.LinesAfter(mark, $"→ {AgentResultCodes.Busy}");
				Assert.Single(refused);
				Assert.Contains(AgentCommands.ShutdownApplication, refused[0]);
				Assert.Contains(nameof(WorkflowState.CourierPackageDepositBarcodeScan), refused[0]);
				Assert.Empty(kiosk.Log.TransitionsAfter(mark));
				Assert.Empty(kiosk.Log.LinesAfter(mark, "Izlazak iz aplikacije"));
				var status = kiosk.ReadStatus() ?? throw new E2EException("status.lock nije čitljiv.");
				Assert.True(status["Busy"]!.GetValue<bool>());
				Assert.Equal("Courier", status["Role"]!.GetValue<string>());

				await CourierTests.LogoutFromScanAsync(kiosk);
			});
		}

		// Tačno jedna linija posle kursora; vraća njen broj.
		private static int SingleLine(KioskSession kiosk, int mark, string text)
		{
			Assert.Single(kiosk.Log.LinesAfter(mark, text));
			return kiosk.Log.FirstLineAfter(mark, text)!.Value;
		}

		/// <summary>
		/// Izjava u status.lock: PID tekućeg klijenta i kanal (null = isključen). v4, Linux (ugovor „Transport na Linuxu“, tačka
		/// 5): i <c>ProcessBootId</c> i <c>ProcessStartTicks</c>, kanal je <c>SocketPath</c>, a isključen kanal izostaje iz izjave.
		/// </summary>
		private static void AssertDeclaration(KioskSession kiosk, string? pipeName)
		{
			var status = kiosk.ReadStatus() ?? throw new E2EException("status.lock nije čitljiv.");
			Assert.Equal(kiosk.ClientProcessId, status["ProcessId"]!.GetValue<int>());
			Assert.NotNull(status["ProcessStartUtc"]);
			if (OperatingSystem.IsLinux()) {
				Assert.False(string.IsNullOrEmpty(status["ProcessBootId"]?.GetValue<string>()));
				Assert.True(status["ProcessStartTicks"]?.GetValue<long>() > 0);
			}
			var channel = status["AgentChannel"];
			if (pipeName == null) {
				Assert.Equal(OperatingSystem.IsWindows(), status.AsObject().ContainsKey("AgentChannel"));
				Assert.Null(channel);
			}
			else {
				Assert.Equal(pipeName, channel?[AgentChannel.AddressField]?.GetValue<string>());
			}
		}

		private static string NewPipeName() => AgentChannel.NewAddress();	// v4: ime pipe-a ili putanja socket-a

		/// <summary>
		/// v4: zauzima kanal pre starta kioska — pipe sa <c>FirstPipeInstance</c> (Windows) ili <c>agent.v1.lock</c> pored
		/// socket-a (Linux; folder <c>0700</c> kao kiosk, .NET drži <c>flock</c> dok je fajl otvoren bez deljenja).
		/// </summary>
		private static IDisposable TakeChannel(string address)
		{
			if (OperatingSystem.IsWindows()) {
				return new NamedPipeServerStream(address, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
												PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance);
			}
			Directory.CreateDirectory(Path.GetDirectoryName(address)!, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
			return new FileStream(Path.ChangeExtension(address, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
		}

		// v4: PipeName (Windows) ili SocketPath (Linux).
		private static KioskOptions WithPipe(string pipeName) => AgentChannel.Options(pipeName);

		// Odgovor kioska na ClientHello, ili null ako je veza zatvorena bez odgovora. v4: na Linuxu Unix socket (kiosk zatvara
		// vezu klijenta koji nije root odmah posle accept-a).
		private static async Task<byte[]?> HelloReplyAsync(string pipeName)
		{
			using var pipe = await ConnectAsync(pipeName);
			try {
				await AgentFrameCodec.WriteFrameAsync(pipe, AgentJson.Serialize(new ClientHello { AgentVersion = "E2E" }), AgentProtocol.MaxMessageBytes, CancellationToken.None);
				using var cts = new CancellationTokenSource(__readTimeout);
				return await AgentFrameCodec.ReadFrameAsync(pipe, AgentProtocol.MaxMessageBytes, cts.Token);
			}
			catch (IOException) {
				// v4: i upis ClientHello sme da pukne — veza je mogla biti zatvorena pre njega (Linux).
				return null;
			}
		}

		private static async Task<Stream> ConnectAsync(string address)
		{
			if (OperatingSystem.IsWindows()) {
				var pipe = new NamedPipeClientStream(".", address, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
				await pipe.ConnectAsync(5000);
				return pipe;
			}
			var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
			using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
			await socket.ConnectAsync(new UnixDomainSocketEndPoint(address), cts.Token);
			return new NetworkStream(socket, ownsSocket: true);
		}
	}
}
