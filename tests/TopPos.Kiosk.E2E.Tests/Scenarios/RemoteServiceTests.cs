using System.IO;
using System.Text.Json.Nodes;
using TopPos.Agent.Contracts;
using TopPos.Kiosk.Workflow;
using TopPos.E2E.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace TopPos.E2E.Tests.Scenarios
{
	/// <summary>
	/// Servisna sesija Agenta (BeginRemoteService, OpenDoors, EndRemoteService, GetCommandResult) u pravom klijentu. Komande
	/// šalje test klijent kao LocalSystem kroz psexec (kao <see cref="AgentPipeTests"/>), a serviser radi na ekranu kroz
	/// FlaUI i skener simulatora. Provera: odgovori kioska, otključavanja u LockerSimulator-u (svaka vrata jednom po
	/// komandi, bez ponavljanja), ekran i log. Bez admin konzole ili psexec-a testovi se preskaču.
	/// </summary>
	[Collection(E2ECollection.Name)]
	public sealed class RemoteServiceTests(ITestOutputHelper output)
	{
		private const string PresenceCode = "246810";
		private static readonly TimeSpan __openingTimeout = TimeSpan.FromSeconds(90);

		/// <summary>
		/// Prijava kodom na tastaturi; pre nje portal ne otvara. Vertikala 1 sa ekrana, pa sva vrata iz portala: svaka vrata
		/// otključana tačno jednom po komandi, ishod OpenConfirmed. Kraj sa ekrana u dva dodira (prvi pokazuje otvorena vrata),
		/// Start i slobodan kiosk; rezultat sesije ima sva otvaranja i nijedna vrata bez „zatvoreno".
		/// </summary>
		[SkippableFact]
		public Task LoginOnKeypad_OpenColumnAndAll_EachDoorUnlockedOnce_EndOnScreen()
		{
			var pipe = RequireAgent();
			return KioskSession.RunAsync(nameof(LoginOnKeypad_OpenColumnAndAll_EachDoorUnlockedOnce_EndOnScreen), output, WithPipe(pipe), async kiosk => {
				var session = await BeginAsync(kiosk, pipe);

				var (exitCode, refused) = await LocalSystemAgentClient.RemoteServiceAsync(kiosk, pipe, "--open-doors", session, "1");
				Assert.Equal(5, exitCode);
				Assert.Equal(AgentResultCodes.TechnicianNotPresent, refused?.ResultCode);
				// servisni ekran zauzima ceo prozor: zaglavlje (Home, jezik) nije prikazano
				await kiosk.Ui.WaitForHiddenAsync("Header.Home");
				await kiosk.Ui.WaitForHiddenAsync("Header.Language");

				await LoginOnKeypadAsync(kiosk, session);

				var unlockMark = await kiosk.Locker.MarkUnlocksAsync();
				var mark = kiosk.Log.Mark();
				await kiosk.Ui.TapAsync("RemoteServicePage.Column.1");
				await kiosk.WaitForLogLineAsync(OutcomeLine(session, 24, DoorOpenSources.Screen, DoorOpenOutcomes.OpenConfirmed), mark, __openingTimeout);
				AssertEachDoorUnlockedOnce(await kiosk.Locker.GetUnlocksAsync(unlockMark), Enumerable.Range(1, 24));

				await kiosk.Ui.WaitForAsync("RemoteServicePage.OpenAll");
				unlockMark = await kiosk.Locker.MarkUnlocksAsync();
				var opened = await OpenFromPortalAsync(kiosk, pipe, session, "all");
				Assert.Equal(Enumerable.Range(1, 48), opened.Openings.Select(o => o.DoorNumber));
				Assert.All(opened.Openings, o => Assert.Equal((DoorOpenSources.Portal, DoorOpenOutcomes.OpenConfirmed), (o.Source, o.Outcome)));
				AssertEachDoorUnlockedOnce(await kiosk.Locker.GetUnlocksAsync(unlockMark), Enumerable.Range(1, 48));

				await kiosk.Locker.CloseAllDoorsAsync();
				await kiosk.Ui.WaitForAsync("RemoteServicePage.End");
				mark = kiosk.Log.Mark();
				await kiosk.Ui.TapAsync("RemoteServicePage.End");
				await kiosk.Ui.WaitForTextAsync("Vrata koja možda nisu zatvorena");
				Assert.Empty(kiosk.Log.TransitionsAfter(mark));
				await kiosk.Ui.TapAsync("RemoteServicePage.End");
				await kiosk.WaitForStateAsync(WorkflowState.Start, mark, TimeSpan.FromSeconds(30));
				await kiosk.Ui.WaitForAsync("Header.Home");     // posle sesije zaglavlje je ponovo prikazano, Home radi

				var result = await SessionResultAsync(kiosk, pipe, session);
				Assert.Equal((RemoteServicePhases.Ended, RemoteServiceEndModes.ScreenConfirmed), (result.Phase, result.EndMode));
				Assert.Equal(24 + 48, result.Openings.Count);
				Assert.Empty(result.ModuleErrors);
				Assert.NotNull(result.DoorsNotClosed);
				Assert.Empty(result.DoorsNotClosed);
				await WaitForBusyAsync(kiosk, busy: false);
			});
		}

		/// <summary>
		/// v4 (odluka 27.11.2026): paketomat van funkcije (oba modula isključena), servisna sesija ima prednost. Dok traje, poruka
		/// o kvaru je sklonjena i kiosk prima unos: serviser se prijavljuje na tastaturi i završava sesiju na ekranu. Posle kraja
		/// poruka o kvaru je ponovo prikazana, a Start ugašen. Moduli ponovo uključeni → kiosk u funkciji.
		/// </summary>
		[SkippableFact]
		public Task OutOfOrder_SessionHasPriority_OutOfOrderMessageBackAfterEnd()
		{
			var pipe = RequireAgent();
			var options = new KioskOptions {
				ConfigureClient = c => {
					WithPipe(pipe).ConfigureClient!(c);
					c["Locker"]!["DoorWatcherFrequency"] = 3000;
				},
			};
			return KioskSession.RunAsync(nameof(OutOfOrder_SessionHasPriority_OutOfOrderMessageBackAfterEnd), output, options, async kiosk => {
				const string outOfOrder = "Paketomat trenutno nije u funkciji";
				await kiosk.WaitForLogLineAsync($"Agent pipe: server pokrenut ({pipe}", 0);
				var mark = kiosk.Log.Mark();
				await kiosk.Locker.SetModuleAsync(1, isOn: false);
				await kiosk.Locker.SetModuleAsync(2, isOn: false);
				await kiosk.WaitForLogLineAsync("Paketomat je u funkciji = [False]", mark, TimeSpan.FromSeconds(60));
				await kiosk.Ui.WaitForTextAsync(outOfOrder);

				var session = await BeginAsync(kiosk, pipe);
				await kiosk.Ui.WaitForHiddenAsync("Message.Text");
				await LoginOnKeypadAsync(kiosk, session);

				mark = kiosk.Log.Mark();
				await kiosk.Ui.TapAsync("RemoteServicePage.End");
				await kiosk.Ui.WaitForTextAsync("Dodirnite „Potvrdi kraj“");
				await kiosk.Ui.TapAsync("RemoteServicePage.End");
				await kiosk.WaitForStateAsync(WorkflowState.Start, mark, TimeSpan.FromSeconds(60));
				await kiosk.Ui.WaitForPageAsync("StartPage");
				await kiosk.Ui.WaitForTextAsync(outOfOrder);
				// Dugme postoji (GetBounds ga čeka i kad je onemogućeno), pa je „onemogućeno“ stvarno stanje, ne odsustvo stranice.
				await kiosk.Ui.GetBoundsAsync("StartPage.RecipientPickUpPackage");
				Assert.False(await kiosk.Ui.IsEnabledAsync("StartPage.RecipientPickUpPackage"), "Start ekran mora biti onemogućen posle sesije");
				Assert.Equal(RemoteServiceEndModes.ScreenConfirmed, (await SessionResultAsync(kiosk, pipe, session)).EndMode);

				mark = kiosk.Log.Mark();
				await kiosk.Locker.SetModuleAsync(1, isOn: true);
				await kiosk.Locker.SetModuleAsync(2, isOn: true);
				await kiosk.WaitForLogLineAsync("Paketomat je u funkciji = [True]", mark, TimeSpan.FromSeconds(60));
				await kiosk.Ui.WaitForHiddenAsync("Message.Text");
				await kiosk.Ui.WaitForAsync("StartPage.RecipientPickUpPackage");
				await WaitForBusyAsync(kiosk, busy: false);
			});
		}

		/// <summary>
		/// Prijava QR-om na skeneru; posle prijave skener ne pokreće tok. Modul 2 isključen: prva vrata modula OutcomeUnknown,
		/// ostala ModuleUnavailable bez slanja, poruka na ekranu, nijedno otključavanje. Modul ponovo uključen: novo otvaranje
		/// ga pokušava i vrata se otvaraju. Kraj iz portala sa otvorenim vratima: ta vrata su u DoorsNotClosed. Posle kraja
		/// kurir se prijavljuje skenerom.
		/// </summary>
		[SkippableFact]
		public Task ModuleOff_FirstDoorUnknownRestSkipped_EndFromPortalListsOpenDoor()
		{
			var pipe = RequireAgent();
			return KioskSession.RunAsync(nameof(ModuleOff_FirstDoorUnknownRestSkipped_EndFromPortalListsOpenDoor), output, WithPipe(pipe), async kiosk => {
				var session = await BeginAsync(kiosk, pipe);
				var mark = kiosk.Log.Mark();
				await kiosk.Locker.ScanBarcodeAsync(PresenceCode);
				await kiosk.WaitForLogLineAsync($"{session}: serviser prijavljen kodom {PresenceCode}", mark);

				mark = kiosk.Log.Mark();
				await kiosk.Locker.ScanBarcodeAsync(kiosk.Data.CourierQrCode);
				await kiosk.WaitForLogLineAsync($"barkod {Flatsoft.Common.LogMask.Credential(kiosk.Data.CourierQrCode)} zanemaren — serviser je već prijavljen", mark);	// maskiran
				Assert.Empty(kiosk.Log.TransitionsAfter(mark));

				await kiosk.Locker.SetModuleAsync(2, isOn: false);
				var unlockMark = await kiosk.Locker.MarkUnlocksAsync();
				var opened = await OpenFromPortalAsync(kiosk, pipe, session, "25,26,27");
				Assert.Equal([(25, DoorOpenOutcomes.OutcomeUnknown), (26, DoorOpenOutcomes.ModuleUnavailable), (27, DoorOpenOutcomes.ModuleUnavailable)],
							 opened.Openings.Select(o => (o.DoorNumber, o.Outcome)));
				Assert.Empty(await kiosk.Locker.GetUnlocksAsync(unlockMark));
				await kiosk.Ui.WaitForTextAsync("Modul ne odgovara: 2");

				await kiosk.Locker.SetModuleAsync(2, isOn: true);
				opened = await OpenFromPortalAsync(kiosk, pipe, session, "25");
				Assert.Equal([(25, DoorOpenOutcomes.OpenConfirmed)], opened.Openings.Select(o => (o.DoorNumber, o.Outcome)));
				AssertEachDoorUnlockedOnce(await kiosk.Locker.GetUnlocksAsync(unlockMark), [25]);

				mark = kiosk.Log.Mark();
				var (exitCode, response) = await LocalSystemAgentClient.RemoteServiceAsync(kiosk, pipe, "--remote-service-end", session, "--request-id", NewId());
				Assert.Equal(0, exitCode);
				Assert.Equal(AgentResultCodes.Completed, response?.ResultCode);
				var result = AgentJson.ReadResult<RemoteServiceResult>(response!)!;
				Assert.Equal((RemoteServicePhases.Ended, RemoteServiceEndModes.PortalConfirmed), (result.Phase, result.EndMode));
				Assert.Equal([25], result.DoorsNotClosed);
				Assert.Equal([2], result.ModuleErrors.Select(e => e.ModuleNumber).Distinct());
				await kiosk.WaitForStateAsync(WorkflowState.Start, mark);
				await WaitForBusyAsync(kiosk, busy: false);

				await kiosk.Locker.CloseDoorAsync(25);
				await CourierTests.OpenDepositScanAsync(kiosk);
				await CourierTests.LogoutFromScanAsync(kiosk);
			});
		}

		/// <summary>
		/// Serial: odgovor na otvaranje vrata 1 se izgubi (vrata su otvorena). Ishod OutcomeUnknown, komanda poslata jednom,
		/// ostala vrata modula 1 se preskaču, vrata na drugom modulu se otvaraju. Između dve komande je razmak magistrale
		/// (2,5 s). Na kraju sa ekrana sva vrata otvorena u simulatoru su u DoorsNotClosed.
		/// </summary>
		[SkippableFact]
		public Task Serial_LostOpenResponse_NotResent_GapBetweenDoors()
		{
			var pipe = RequireAgent();
			E2ESettings.RequireLockerSerialPorts();
			var serial = KioskOptions.SerialController(E2ESettings.Current);
			var options = serial with {
				ConfigureClient = c => {
					serial.ConfigureClient!(c);
					WithPipe(pipe).ConfigureClient!(c);
				},
			};
			return KioskSession.RunAsync(nameof(Serial_LostOpenResponse_NotResent_GapBetweenDoors), output, options, async kiosk => {
				Assert.NotEmpty(kiosk.Log.LinesAfter(0, "LockControllerClientSerial: inicijalizacija zavrsena"));
				var session = await BeginAsync(kiosk, pipe, LockerLayout.Serial);
				await LoginOnKeypadAsync(kiosk, session);

				await kiosk.Locker.ArmLockFaultAsync("DropOpenResponse");
				var unlockMark = await kiosk.Locker.MarkUnlocksAsync();
				var mark = kiosk.Log.Mark();
				var opened = await OpenFromPortalAsync(kiosk, pipe, session, "1,2,9");
				Assert.Equal([(1, DoorOpenOutcomes.OutcomeUnknown), (2, DoorOpenOutcomes.ModuleUnavailable), (9, DoorOpenOutcomes.OpenConfirmed)],
							 opened.Openings.Select(o => (o.DoorNumber, o.Outcome)));
				AssertEachDoorUnlockedOnce(await kiosk.Locker.GetUnlocksAsync(unlockMark), [1, 9]);
				Assert.Single(kiosk.Log.LinesAfter(mark, "OpenDoorOnce: 1 "));
				Assert.Empty(kiosk.Log.LinesAfter(mark, "OpenDoorOnce: 2 "));

				unlockMark = await kiosk.Locker.MarkUnlocksAsync();
				opened = await OpenFromPortalAsync(kiosk, pipe, session, "3,4");
				Assert.All(opened.Openings, o => Assert.Equal(DoorOpenOutcomes.OpenConfirmed, o.Outcome));
				var unlocks = await kiosk.Locker.GetUnlocksAsync(unlockMark);
				AssertEachDoorUnlockedOnce(unlocks, [3, 4]);
				// Razmak se meri od kraja prethodne komande, pa je između dva otključavanja bar 2,5 s (uz milisekunde zaokruživanja).
				var gap = unlocks.Single(u => u.Door == 4).Timestamp - unlocks.Single(u => u.Door == 3).Timestamp;
				kiosk.Trace($"razmak otključavanja vrata 3 → 4: {gap.TotalMilliseconds:0} ms");
				Assert.True(gap >= TimeSpan.FromMilliseconds(2450), $"razmak između komandi na magistrali: {gap.TotalMilliseconds:0} ms");

				var openInSimulator = (await kiosk.Locker.GetDoorsAsync()).Where(d => !d.IsLocked).Select(d => d.Number).Order().ToList();
				Assert.Equal([1, 3, 4, 9], openInSimulator);
				mark = kiosk.Log.Mark();
				await kiosk.Ui.TapAsync("RemoteServicePage.End");
				await kiosk.Ui.WaitForTextAsync("Vrata koja možda nisu zatvorena: 1, 3, 4, 9");
				await kiosk.Ui.TapAsync("RemoteServicePage.End");
				// Čitanje 4 modula sa razmakom traje ~8 s: na ekranu je samo „Proveravam…", poziv na potvrdu je sklonjen.
				await kiosk.Ui.WaitForTextAsync("Proveravam stanje vrata");
				Assert.True(string.IsNullOrEmpty(await kiosk.Ui.GetTextAsync("RemoteServicePage.Notice")),
							"poruka „Dodirnite Potvrdi kraj“ ostala je na ekranu tokom čitanja stanja vrata");
				Assert.False(await kiosk.Ui.IsEnabledAsync("RemoteServicePage.End"));
				await kiosk.WaitForStateAsync(WorkflowState.Start, mark, TimeSpan.FromSeconds(60));

				var result = await SessionResultAsync(kiosk, pipe, session);
				Assert.Equal(RemoteServiceEndModes.ScreenConfirmed, result.EndMode);
				Assert.Equal(openInSimulator, result.DoorsNotClosed);
				Assert.Equal([1], result.ModuleErrors.Select(e => e.ModuleNumber));
				await WaitForBusyAsync(kiosk, busy: false);
			});
		}

		/// <summary>
		/// Tri pogrešna koda (tastatura, skener, tastatura): posle prvog poruka sa brojem preostalih pokušaja, posle trećeg
		/// kraj (AuthFailed), Start i slobodan kiosk; nijedna vrata nisu otključana.
		/// </summary>
		[SkippableFact]
		public Task ThreeWrongCodes_SessionEndsAuthFailed_NoDoorOpened()
		{
			var pipe = RequireAgent();
			return KioskSession.RunAsync(nameof(ThreeWrongCodes_SessionEndsAuthFailed_NoDoorOpened), output, WithPipe(pipe), async kiosk => {
				var unlockMark = await kiosk.Locker.MarkUnlocksAsync();
				var session = await BeginAsync(kiosk, pipe);
				var mark = kiosk.Log.Mark();

				await kiosk.Ui.TypeOnKeypadAsync("1111");
				await kiosk.Ui.WaitForTextAsync("Pogrešan kod. Preostalo pokušaja: 2");
				await kiosk.Locker.ScanBarcodeAsync("2222");
				await kiosk.WaitForLogLineAsync($"{session}: pogrešan kod 2222, pokušaj 2 od 3", mark);
				await kiosk.Ui.TypeOnKeypadAsync("3333");
				await kiosk.WaitForStateAsync(WorkflowState.Start, mark, TimeSpan.FromSeconds(30));

				var result = await SessionResultAsync(kiosk, pipe, session);
				Assert.Equal((RemoteServicePhases.Ended, RemoteServiceEndModes.AuthFailed), (result.Phase, result.EndMode));
				Assert.Equal(3, result.FailedAuthAttempts);
				Assert.Empty(result.Openings);
				Assert.Null(result.AuthenticatedAtUtc);
				Assert.Empty(await kiosk.Locker.GetUnlocksAsync(unlockMark));
				await WaitForBusyAsync(kiosk, busy: false);
			});
		}

		/// <summary>Serviser se ne prijavi: posle roka prijave (120 s) kraj AuthTimeout, Start i slobodan kiosk. Traje ~2,5 min.</summary>
		[SkippableFact]
		public Task NoLogin_SessionEndsAfterAuthTimeout()
		{
			var pipe = RequireAgent();
			return KioskSession.RunAsync(nameof(NoLogin_SessionEndsAfterAuthTimeout), output, WithPipe(pipe), async kiosk => {
				var session = await BeginAsync(kiosk, pipe);
				var mark = kiosk.Log.Mark();

				var timeout = TimeSpan.FromSeconds(RemoteServiceLimits.AuthTimeoutSeconds + 30);
				await kiosk.WaitForLogLineAsync($"{session}: završena ({RemoteServiceEndModes.AuthTimeout})", mark, timeout);
				await kiosk.WaitForStateAsync(WorkflowState.Start, mark);

				var result = await SessionResultAsync(kiosk, pipe, session);
				Assert.Equal(RemoteServiceEndModes.AuthTimeout, result.EndMode);
				Assert.True(result.EndedAtUtc - result.StartedAtUtc >= TimeSpan.FromSeconds(RemoteServiceLimits.AuthTimeoutSeconds),
							$"sesija je završena posle {(result.EndedAtUtc - result.StartedAtUtc)?.TotalSeconds:0} s");
				await WaitForBusyAsync(kiosk, busy: false);
			});
		}

		/// <summary>
		/// Pad programa usred sesije (posle otvaranja iz portala): posle restarta je sesija zaključena kao ProcessRestart, sa
		/// ranijim otvaranjima i vratima bez „zatvoreno"; ništa se ne ponavlja (nijedno otključavanje posle restarta). Kiosk
		/// je slobodan: nova sesija počinje, a kraj iz portala pre prijave je PortalCancelled.
		/// </summary>
		[SkippableFact]
		public Task ProcessKilledDuringSession_ClosedAsProcessRestart_NothingRepeated()
		{
			var pipe = RequireAgent();
			return KioskSession.RunAsync(nameof(ProcessKilledDuringSession_ClosedAsProcessRestart_NothingRepeated), output, WithPipe(pipe), async kiosk => {
				var session = await BeginAsync(kiosk, pipe);
				await LoginOnKeypadAsync(kiosk, session);
				var opened = await OpenFromPortalAsync(kiosk, pipe, session, "1,2");
				Assert.All(opened.Openings, o => Assert.Equal(DoorOpenOutcomes.OpenConfirmed, o.Outcome));

				var unlockMark = await kiosk.Locker.MarkUnlocksAsync();
				var mark = kiosk.Log.Mark();
				await kiosk.RestartClientAsync(hard: true);
				await kiosk.WaitForLogLineAsync($"Agent pipe: server pokrenut ({pipe}", mark);
				Assert.NotEmpty(kiosk.Log.LinesAfter(mark, $"{AgentCommands.BeginRemoteService} {session} je prekinut restartom programa"));

				var result = await SessionResultAsync(kiosk, pipe, session);
				Assert.Equal((RemoteServicePhases.Ended, RemoteServiceEndModes.ProcessRestart), (result.Phase, result.EndMode));
				Assert.Equal([(1, DoorOpenOutcomes.OpenConfirmed), (2, DoorOpenOutcomes.OpenConfirmed)], result.Openings.Select(o => (o.DoorNumber, o.Outcome)));
				Assert.Equal([1, 2], result.DoorsNotClosed);
				Assert.Empty(await kiosk.Locker.GetUnlocksAsync(unlockMark));
				await WaitForBusyAsync(kiosk, busy: false);

				var next = await BeginAsync(kiosk, pipe);
				mark = kiosk.Log.Mark();
				var (exitCode, response) = await LocalSystemAgentClient.RemoteServiceAsync(kiosk, pipe, "--remote-service-end", next, "--request-id", NewId());
				Assert.Equal(0, exitCode);
				Assert.Equal(RemoteServiceEndModes.PortalCancelled, AgentJson.ReadResult<RemoteServiceResult>(response!)!.EndMode);
				await kiosk.WaitForStateAsync(WorkflowState.Start, mark);
				Assert.Empty(await kiosk.Locker.GetUnlocksAsync(unlockMark));
			});
		}

		// ─── Koraci ───

		private static string RequireAgent()
		{
			E2ESettings.RequireEnabled();
			LocalSystemAgentClient.RequireAvailable();
			return AgentChannel.NewAddress();		// v4: ime pipe-a ili putanja socket-a
		}

		// v4: PipeName (Windows) ili SocketPath (Linux).
		private static KioskOptions WithPipe(string pipeName) => AgentChannel.Options(pipeName);

		private static string NewId() => Guid.NewGuid().ToString("N");

		/// <summary>
		/// BeginRemoteService sa rasporedom svih vrata (vertikala = modul): Completed, faza AwaitingTechnician, stranica
		/// servisne sesije, kiosk zauzet sa ulogom Agent. Vraća sessionId.
		/// </summary>
		private static async Task<string> BeginAsync(KioskSession kiosk, string pipe, LockerLayout? layout = null)
		{
			await kiosk.WaitForLogLineAsync($"Agent pipe: server pokrenut ({pipe}", 0);
			var layoutFile = WriteLayout(kiosk, layout ?? LockerLayout.Ncu24L);
			var session = NewId();
			var mark = kiosk.Log.Mark();
			var (exitCode, response) = await LocalSystemAgentClient.RemoteServiceAsync(kiosk, pipe, "--remote-service-begin", PresenceCode,
																					   "--layout", layoutFile, "--request-id", session);
			Assert.True(exitCode == 0, $"BeginRemoteService: izlaz test klijenta {exitCode}, odgovor {response?.ResultCode} — {response?.Message}");
			Assert.Equal(AgentResultCodes.Completed, response?.ResultCode);
			Assert.Equal(RemoteServicePhases.AwaitingTechnician, AgentJson.ReadResult<RemoteServiceResult>(response!)!.Phase);
			await kiosk.WaitForStateAsync(WorkflowState.RemoteService, mark);
			await kiosk.Ui.WaitForPageAsync("RemoteServicePage");
			var status = await WaitForBusyAsync(kiosk, busy: true);
			Assert.Equal("Agent", status["Role"]!.GetValue<string>());
			return session;
		}

		// CSV rasporeda kao iz Technician-a: vertikala po modulu, vrata odozgo nadole.
		private static string WriteLayout(KioskSession kiosk, LockerLayout layout)
		{
			var directory = Path.Combine(kiosk.Directory, "agent");
			Directory.CreateDirectory(directory);
			var file = Path.Combine(directory, "layout.csv");
			var rows = Enumerable.Range(1, layout.DoorCount)
								 .Select(door => $"{door};{BoxSizes.M};{(door - 1) / layout.DoorsPerModule + 1};{(door - 1) % layout.DoorsPerModule + 1}");
			File.WriteAllLines(file, ["DoorNumber;Size;Column;Row", .. rows]);
			return file;
		}

		private static async Task LoginOnKeypadAsync(KioskSession kiosk, string session)
		{
			// Ceo donji red tastature je na ekranu: tasteri su iste veličine, pa odsečen „Potvrdi" ima manju vidljivu visinu.
			var (digit, _) = await kiosk.Ui.GetBoundsAsync("Keypad.1");
			var (confirm, window) = await kiosk.Ui.GetBoundsAsync("Keypad.Confirm");
			kiosk.Trace($"tastatura: taster 1 {digit}, Potvrdi {confirm}, prozor {window}");
			Assert.True(confirm.Height >= digit.Height - 2 && confirm.Bottom <= window.Bottom,
						$"tastatura nije cela na ekranu: Potvrdi {confirm}, taster 1 {digit}, prozor {window}");
			var mark = kiosk.Log.Mark();
			await kiosk.Ui.TypeOnKeypadAsync(PresenceCode);
			await kiosk.WaitForLogLineAsync($"{session}: serviser prijavljen kodom {PresenceCode}", mark);
			await kiosk.Ui.WaitForAsync("RemoteServicePage.OpenAll");
			Assert.Equal($"Provera brava na paketomatu {kiosk.Settings.LockerId}", await kiosk.Ui.GetTextAsync("RemoteServicePage.Title"));
		}

		/// <summary>OpenDoors iz portala (InProgress), pa GetCommandResult dok otvaranje ne završi; vraća konačan rezultat.</summary>
		private static async Task<OpenDoorsResult> OpenFromPortalAsync(KioskSession kiosk, string pipe, string session, string doors)
		{
			var requestId = NewId();
			var (exitCode, response) = await LocalSystemAgentClient.RemoteServiceAsync(kiosk, pipe, "--open-doors", session, doors, "--request-id", requestId);
			Assert.True(exitCode == 0, $"OpenDoors: izlaz test klijenta {exitCode}, odgovor {response?.ResultCode} — {response?.Message}");
			Assert.Equal(AgentResultCodes.InProgress, response?.ResultCode);
			var deadline = DateTime.UtcNow + __openingTimeout;
			while (true) {
				var (_, query) = await LocalSystemAgentClient.RemoteServiceAsync(kiosk, pipe, "--command-result", requestId);
				var result = AgentJson.ReadResult<OpenDoorsResult>(query ?? throw new E2EException("GetCommandResult: odgovor nije stigao."))
							 ?? throw new E2EException($"GetCommandResult {requestId}: {query.ResultCode} bez rezultata — {query.Message}");
				if (result.Finished) {
					kiosk.Trace($"otvaranje {doors}: {string.Join(", ", result.Openings.Select(o => $"{o.DoorNumber} {o.Outcome}"))}");
					return result;
				}
				if (DateTime.UtcNow > deadline) {
					throw new E2EException($"Otvaranje {doors} nije završeno za {__openingTimeout.TotalSeconds:0} s.");
				}
				await Task.Delay(500);
			}
		}

		/// <summary>GetCommandResult za sesiju (bez ClientInstanceId, radi i posle restarta kioska).</summary>
		private static async Task<RemoteServiceResult> SessionResultAsync(KioskSession kiosk, string pipe, string session)
		{
			var (exitCode, response) = await LocalSystemAgentClient.RemoteServiceAsync(kiosk, pipe, "--command-result", session);
			Assert.True(exitCode == 0, $"GetCommandResult: izlaz test klijenta {exitCode}, odgovor {response?.ResultCode} — {response?.Message}");
			var result = AgentJson.ReadResult<RemoteServiceResult>(response!)!;
			Assert.False(result.HasMore);
			return result;
		}

		private static string OutcomeLine(string session, int door, string source, string outcome)
			=> $"RemoteServiceSession {session}: vrata {door} ({source}) → {outcome}";

		private static void AssertEachDoorUnlockedOnce(IReadOnlyList<UnlockEntry> unlocks, IEnumerable<int> doors)
		{
			Assert.All(unlocks, u => Assert.True(u.Unlocked, $"vrata {u.Door} su blokirana u simulatoru"));
			Assert.Equal(doors.Order(), unlocks.Select(u => u.Door).Order());
		}

		/// <summary>status.lock sa zadatom zauzetošću (upis ide posle prelaza, pa se čeka).</summary>
		private static Task<JsonNode> WaitForBusyAsync(KioskSession kiosk, bool busy)
			=> Poll.UntilAsync(() => kiosk.ReadStatus() is { } status && status["Busy"]!.GetValue<bool>() == busy ? status : null,
							   TimeSpan.FromSeconds(10), $"status.lock Busy={busy}", kiosk.EnsureClientAlive);
	}
}
