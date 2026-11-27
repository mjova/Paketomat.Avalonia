using System.Diagnostics;
using System.Text.Json.Nodes;
using TopPos.Agent.Contracts;
using TopPos.Kiosk.Workflow;
using TopPos.E2E.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace TopPos.E2E.Tests.Scenarios
{
	/// <summary>
	/// Preuzimanje na zahtev Agenta (RecipientPickup) u pravom klijentu: podrška šalje telefon, šifru i kod prisustva, a
	/// korisnik na ekranu unosi kod koji mu je operater izdiktirao. Komande šalje test klijent kao LocalSystem kroz psexec
	/// (kao <see cref="RemoteServiceTests"/>), korisnik radi na ekranu kroz FlaUI. Provera: odgovor kioska, rezultat iz
	/// GetCommandResult, prelazi, ekran, pozivi servera i otključavanja u LockerSimulator-u. Bez admin konzole ili psexec-a
	/// testovi se preskaču.
	/// </summary>
	[Collection(E2ECollection.Name)]
	public sealed class AgentRecipientPickupTests(ITestOutputHelper output)
	{
		private const string PresenceCode = "4826";

		/// <summary>
		/// Pogrešan kod, pa tačan: posle pogrešnog poruka sa brojem preostalih pokušaja i rezultat u toku (AwaitingAnswer,
		/// vrata, 1 pogrešan), bez prelaza i otključavanja. Tačan kod otvara vrata paketa, zatvaranje vraća na Start. Server:
		/// RRPP pa potvrda preuzimanja. Rezultat: OpenConfirmed, potvrda Confirmed, 1 pogrešan; kiosk slobodan.
		/// </summary>
		[SkippableFact]
		public Task WrongThenCorrectCode_DoorOpens_PickupConfirmed()
		{
			var pipe = RequireAgent();
			return KioskSession.RunAsync(nameof(WrongThenCorrectCode_DoorOpens_PickupConfirmed), output, WithPipe(pipe), async kiosk => {
				var package = kiosk.Data.RecipientPackage(amount: 0);
				await kiosk.Server.AddPackageAsync(package);
				var serverMark = await kiosk.Server.MarkAsync();
				var unlockMark = await kiosk.Locker.MarkUnlocksAsync();

				var request = await BeginToConfirmationAsync(kiosk, pipe, package);
				var result = await ResultAsync(kiosk, pipe, request);
				Assert.Equal((RecipientPickupPhases.AwaitingAnswer, (string?)null), (result.Phase, result.Outcome));
				Assert.Equal(package.BrojPozicije, result.DoorNumber);
				Assert.Equal(0, result.FailedCodeAttempts);
				await kiosk.Ui.WaitForTextAsync("Preostalo pokušaja: 3");

				var mark = kiosk.Log.Mark();
				await kiosk.Ui.TypeOnKeypadAsync("1111");
				await kiosk.Ui.WaitForTextAsync("Pogrešan kod. Preostalo pokušaja: 2");
				await kiosk.WaitForLogLineAsync($"{request}: pogrešan kod prisustva 1111, pokušaj 1 od {RecipientPickupLimits.MaxCodeAttempts}", mark);
				result = await WaitForResultAsync(kiosk, pipe, request, r => r.FailedCodeAttempts == 1, "1 pogrešan unos");
				Assert.Equal(RecipientPickupPhases.AwaitingAnswer, result.Phase);
				Assert.Empty(kiosk.Log.TransitionsAfter(mark));
				Assert.Empty(await kiosk.Locker.GetUnlocksAsync(unlockMark));

				mark = kiosk.Log.Mark();
				await kiosk.Ui.TypeOnKeypadAsync(PresenceCode);
				var accepted = await kiosk.WaitForStateAsync(WorkflowState.RecipientPackagePickup, mark);
				Assert.Equal((WorkflowState.RecipientRemoteConfirmation, nameof(WorkflowTrigger.RecipientRemoteAccepted)), (accepted.From, accepted.Trigger));
				await kiosk.WaitForDoorOpenAsync(package.BrojPozicije, "RecipientPackagePickupPage");
				var toStart = await kiosk.CloseDoorAsync(package.BrojPozicije, WorkflowState.Start);
				Assert.Equal("RecipientPickupComplete", toStart.Trigger);

				result = await CompletedResultAsync(kiosk, pipe, request);
				Assert.Equal((RecipientPickupOutcomes.OpenConfirmed, PickupConfirmationStates.Confirmed), (result.Outcome, result.Confirmation));
				Assert.Equal(package.BrojPozicije, result.DoorNumber);
				Assert.Equal(1, result.FailedCodeAttempts);

				// Server: RRPP sa šifrom i telefonom iz komande, pa potvrda preuzimanja — tim redom.
				var calls = await kiosk.Server.GetBusinessCallsAsync(serverMark);
				var rrpp = Assert.Single(calls, c => c.Endpoint == "PrimaocPraznjenjePozicije");
				Assert.Equal(package.KodZaOtvaranje.ToString(), rrpp.Query["KodZaOtvaranje"]);
				Assert.Equal(package.TelefonPrimaoca, rrpp.Query["TelefonPrimaoca"]);
				var confirm = Assert.Single(calls, c => c.Endpoint == "PrimaocPraznjenjePozicijePotvrda");
				Assert.True(confirm.Seq > rrpp.Seq, $"Potvrda preuzimanja mora doći posle RRPP-a: {string.Join(", ", calls)}");
				Assert.DoesNotContain(calls, c => c.IsUnexpectedError);
				Assert.True(Assert.Single(await kiosk.Server.GetPackagesAsync()).Preuzet);
				await kiosk.AssertOnlyDoorUnlockedAsync(unlockMark, package.BrojPozicije);
				await WaitForBusyAsync(kiosk, busy: false);
			});
		}

		/// <summary>
		/// Tri pogrešna koda: posle trećeg Start i poruka da se vrata neće otvoriti. Rezultat WrongCode sa 3 pogrešna; nijedna
		/// vrata nisu otključana, potvrda nije poslata, paket na serveru ostaje nepreuzet.
		/// </summary>
		[SkippableFact]
		public Task ThreeWrongCodes_WrongCode_NoDoorOpened()
		{
			var pipe = RequireAgent();
			return KioskSession.RunAsync(nameof(ThreeWrongCodes_WrongCode_NoDoorOpened), output, WithPipe(pipe), async kiosk => {
				var package = kiosk.Data.RecipientPackage(amount: 0);
				await kiosk.Server.AddPackageAsync(package);
				var serverMark = await kiosk.Server.MarkAsync();
				var unlockMark = await kiosk.Locker.MarkUnlocksAsync();

				var request = await BeginToConfirmationAsync(kiosk, pipe, package);
				var mark = kiosk.Log.Mark();
				await kiosk.Ui.TypeOnKeypadAsync("1111");
				await kiosk.Ui.WaitForTextAsync("Pogrešan kod. Preostalo pokušaja: 2");
				await kiosk.Ui.TypeOnKeypadAsync("2222");
				await kiosk.Ui.WaitForTextAsync("Pogrešan kod. Preostalo pokušaja: 1");
				await kiosk.Ui.TypeOnKeypadAsync("3333");
				var toStart = await kiosk.WaitForStateAsync(WorkflowState.Start, mark);
				Assert.Equal((WorkflowState.RecipientRemoteConfirmation, nameof(WorkflowTrigger.RecipientRemoteCodeFailed)), (toStart.From, toStart.Trigger));
				await kiosk.Ui.WaitForTextAsync("Pogrešan kod. Vrata se neće otvoriti; obratite se podršci.");

				var result = await CompletedResultAsync(kiosk, pipe, request);
				Assert.Equal(RecipientPickupOutcomes.WrongCode, result.Outcome);
				Assert.Equal(RecipientPickupLimits.MaxCodeAttempts, result.FailedCodeAttempts);
				Assert.Null(result.Confirmation);
				AssertNothingOpened(await kiosk.Server.GetBusinessCallsAsync(serverMark), await kiosk.Locker.GetUnlocksAsync(unlockMark));
				Assert.False(Assert.Single(await kiosk.Server.GetPackagesAsync()).Preuzet);
				await WaitForBusyAsync(kiosk, busy: false);
			});
		}

		/// <summary>
		/// Odustajanje na ekranu: prvo „Otkaži", pa u novom preuzimanju Home. Oba su Declined, bez otključavanja i bez potvrde
		/// serveru; posle svakog je kiosk slobodan.
		/// </summary>
		[RealMouseFact]	// v4
		public Task CancelThenHome_BothDeclined_NoDoorOpened()
		{
			var pipe = RequireAgent();
			return KioskSession.RunAsync(nameof(CancelThenHome_BothDeclined_NoDoorOpened), output, WithPipe(pipe), async kiosk => {
				var package = kiosk.Data.RecipientPackage(amount: 0);
				await kiosk.Server.AddPackageAsync(package);
				var serverMark = await kiosk.Server.MarkAsync();
				var unlockMark = await kiosk.Locker.MarkUnlocksAsync();

				var request = await BeginToConfirmationAsync(kiosk, pipe, package);
				var mark = kiosk.Log.Mark();
				await kiosk.Ui.TapAsync("RecipientRemoteConfirmationPage.Cancel");
				var toStart = await kiosk.WaitForStateAsync(WorkflowState.Start, mark);
				Assert.Equal(nameof(WorkflowTrigger.RecipientRemoteDeclined), toStart.Trigger);
				Assert.Equal(RecipientPickupOutcomes.Declined, (await CompletedResultAsync(kiosk, pipe, request)).Outcome);
				await WaitForBusyAsync(kiosk, busy: false);

				request = await BeginToConfirmationAsync(kiosk, pipe, package);
				mark = kiosk.Log.Mark();
				await kiosk.Ui.PressWithMouseAsync("Header.Home");
				toStart = await kiosk.WaitForStateAsync(WorkflowState.Start, mark);
				Assert.Equal((WorkflowState.RecipientRemoteConfirmation, "Reset"), (toStart.From, toStart.Trigger));
				Assert.Equal(RecipientPickupOutcomes.Declined, (await CompletedResultAsync(kiosk, pipe, request)).Outcome);

				AssertNothingOpened(await kiosk.Server.GetBusinessCallsAsync(serverMark), await kiosk.Locker.GetUnlocksAsync(unlockMark));
				await WaitForBusyAsync(kiosk, busy: false);
			});
		}

		/// <summary>
		/// Korisnik ne unese kod: posle roka (120 s, opšti rok ekrana se ne koristi) Start, poruka o isteku i ishod NoAnswer;
		/// ništa nije otvoreno. Traje ~2,5 min.
		/// </summary>
		[SkippableFact]
		public Task NoCodeEntered_NoAnswerAfterTimeout()
		{
			var pipe = RequireAgent();
			return KioskSession.RunAsync(nameof(NoCodeEntered_NoAnswerAfterTimeout), output, WithPipe(pipe), async kiosk => {
				var package = kiosk.Data.RecipientPackage(amount: 0);
				await kiosk.Server.AddPackageAsync(package);
				var serverMark = await kiosk.Server.MarkAsync();
				var unlockMark = await kiosk.Locker.MarkUnlocksAsync();

				var request = await BeginToConfirmationAsync(kiosk, pipe, package);
				var mark = kiosk.Log.Mark();
				var waited = Stopwatch.StartNew();
				var toStart = await kiosk.WaitForStateAsync(WorkflowState.Start, mark, TimeSpan.FromSeconds(RecipientPickupLimits.CodeTimeoutSeconds + 30));
				kiosk.Trace($"stranica potvrde napuštena posle {waited.Elapsed.TotalSeconds:0} s");
				Assert.Equal((WorkflowState.RecipientRemoteConfirmation, nameof(WorkflowTrigger.RecipientRemoteNoAnswer)), (toStart.From, toStart.Trigger));
				// Rok teče od ulaska na stranicu, a merenje počinje malo posle njega.
				Assert.True(waited.Elapsed >= TimeSpan.FromSeconds(RecipientPickupLimits.CodeTimeoutSeconds - 10),
							$"stranica potvrde napuštena posle {waited.Elapsed.TotalSeconds:0} s");
				await kiosk.Ui.WaitForTextAsync("Vreme za unos koda je isteklo. Obratite se podršci.");

				Assert.Equal(RecipientPickupOutcomes.NoAnswer, (await CompletedResultAsync(kiosk, pipe, request)).Outcome);
				AssertNothingOpened(await kiosk.Server.GetBusinessCallsAsync(serverMark), await kiosk.Locker.GetUnlocksAsync(unlockMark));
				await WaitForBusyAsync(kiosk, busy: false);
			});
		}

		/// <summary>
		/// Paket sa otkupom: posle provere na serveru Start, bez stranice za kod i bez kartice. Ishod PaymentRequired sa iznosom;
		/// ništa nije otvoreno.
		/// </summary>
		[SkippableFact]
		public Task AmountDue_PaymentRequired_NoConfirmationPage()
		{
			var pipe = RequireAgent();
			return KioskSession.RunAsync(nameof(AmountDue_PaymentRequired_NoConfirmationPage), output, WithPipe(pipe), async kiosk => {
				var package = kiosk.Data.RecipientPackage(amount: 1500m);
				await kiosk.Server.AddPackageAsync(package);
				var serverMark = await kiosk.Server.MarkAsync();
				var unlockMark = await kiosk.Locker.MarkUnlocksAsync();

				var (request, mark) = await StartAsync(kiosk, pipe, package);
				var toStart = await kiosk.WaitForStateAsync(WorkflowState.Start, mark);
				Assert.Equal((WorkflowState.RecipientRemoteValidating, nameof(WorkflowTrigger.RecipientPaymentRequired)), (toStart.From, toStart.Trigger));
				// Poruku „Podrška proverava uplatu." provera prikazuje pre prelaza (2 s u E2E), pa se ovde ne čita: psexec traje bar toliko.

				var result = await CompletedResultAsync(kiosk, pipe, request);
				Assert.Equal(RecipientPickupOutcomes.PaymentRequired, result.Outcome);
				Assert.Equal(package.IznosOtkupa, result.Amount);
				Assert.DoesNotContain(kiosk.Log.TransitionsAfter(mark), t => t.To is WorkflowState.RecipientRemoteConfirmation or WorkflowState.RecipientPayment);
				AssertNothingOpened(await kiosk.Server.GetBusinessCallsAsync(serverMark), await kiosk.Locker.GetUnlocksAsync(unlockMark));
				await WaitForBusyAsync(kiosk, busy: false);
			});
		}

		/// <summary>Pogrešna šifra: server odbija RRPP (400) → Start, ishod ServerRejected sa porukom servera; ništa nije otvoreno.</summary>
		[SkippableFact]
		public Task ServerRejects_ServerRejectedWithMessage()
		{
			var pipe = RequireAgent();
			return KioskSession.RunAsync(nameof(ServerRejects_ServerRejectedWithMessage), output, WithPipe(pipe), async kiosk => {
				var package = kiosk.Data.RecipientPackage(amount: 0);
				await kiosk.Server.AddPackageAsync(package);
				var wrong = package with { KodZaOtvaranje = package.KodZaOtvaranje == 9999999 ? 1000000 : package.KodZaOtvaranje + 1 };
				var serverMark = await kiosk.Server.MarkAsync();
				var unlockMark = await kiosk.Locker.MarkUnlocksAsync();

				var (request, mark) = await StartAsync(kiosk, pipe, wrong);
				var toStart = await kiosk.WaitForStateAsync(WorkflowState.Start, mark);
				Assert.Equal((WorkflowState.RecipientRemoteValidating, nameof(WorkflowTrigger.RecipientValidationFailed)), (toStart.From, toStart.Trigger));

				var result = await CompletedResultAsync(kiosk, pipe, request);
				Assert.Equal(RecipientPickupOutcomes.ServerRejected, result.Outcome);
				Assert.False(string.IsNullOrWhiteSpace(result.ServerMessage), "ServerRejected bez poruke servera");
				var calls = await kiosk.Server.GetBusinessCallsAsync(serverMark);
				Assert.Equal(400, Assert.Single(calls, c => c.Endpoint == "PrimaocPraznjenjePozicije").StatusCode);
				AssertNothingOpened(calls, await kiosk.Locker.GetUnlocksAsync(unlockMark));
				await WaitForBusyAsync(kiosk, busy: false);
			});
		}

		/// <summary>Server odgovara na RRPP posle RequestTimeout-a klijenta (5 s) → Start, ishod ServerUnavailable; ništa nije otvoreno.</summary>
		[SkippableFact]
		public Task ServerTimesOut_ServerUnavailable()
		{
			var pipe = RequireAgent();
			return KioskSession.RunAsync(nameof(ServerTimesOut_ServerUnavailable), output, WithPipe(pipe), async kiosk => {
				var package = kiosk.Data.RecipientPackage(amount: 0);
				await kiosk.Server.AddPackageAsync(package);
				var unlockMark = await kiosk.Locker.MarkUnlocksAsync();
				await kiosk.Server.ArmFaultAsync("PrimaocPraznjenjePozicije", delayMs: 8000);

				var (request, mark) = await StartAsync(kiosk, pipe, package);
				var toStart = await kiosk.WaitForStateAsync(WorkflowState.Start, mark, TimeSpan.FromSeconds(30));
				Assert.Equal((WorkflowState.RecipientRemoteValidating, nameof(WorkflowTrigger.RecipientValidationFailed)), (toStart.From, toStart.Trigger));
				Assert.NotEmpty(kiosk.Log.LinesAfter(mark, "RecipientRequestPackagePickup exception"));

				Assert.Equal(RecipientPickupOutcomes.ServerUnavailable, (await CompletedResultAsync(kiosk, pipe, request)).Outcome);
				Assert.Empty(await kiosk.Locker.GetUnlocksAsync(unlockMark));
				await WaitForBusyAsync(kiosk, busy: false);
			});
		}

		/// <summary>Kurir je prijavljen (skeniranje za ostavljanje): odmah Busy, bez prelaza; kurir se zatim uredno odjavljuje.</summary>
		[SkippableFact]
		public Task CourierLoggedIn_RefusedBusy_CourierContinues()
		{
			var pipe = RequireAgent();
			return KioskSession.RunAsync(nameof(CourierLoggedIn_RefusedBusy_CourierContinues), output, WithPipe(pipe), async kiosk => {
				await kiosk.WaitForLogLineAsync($"Agent pipe: server pokrenut ({pipe}", 0);
				await CourierTests.OpenDepositScanAsync(kiosk);
				var mark = kiosk.Log.Mark();

				var (exitCode, response) = await SendPickupAsync(kiosk, pipe, kiosk.Data.RecipientPackage(), NewId());
				Assert.Equal(5, exitCode);
				Assert.Equal(AgentResultCodes.Busy, response?.ResultCode);
				Assert.Empty(kiosk.Log.TransitionsAfter(mark));
				var status = kiosk.ReadStatus() ?? throw new E2EException("status.lock nije čitljiv.");
				Assert.Equal("Courier", status["Role"]!.GetValue<string>());

				await CourierTests.LogoutFromScanAsync(kiosk);
			});
		}

		/// <summary>
		/// Oba modula isključena (kiosk nije u funkciji): odmah NotOperational, bez prelaza. Moduli ponovo uključeni → Start.
		/// DoorWatcher-ov prolaz je skraćen na 3 s da se promena vidi brzo.
		/// </summary>
		[SkippableFact]
		public Task OutOfOrder_RefusedNotOperational()
		{
			var pipe = RequireAgent();
			var options = new KioskOptions {
				ConfigureClient = c => {
					WithPipe(pipe).ConfigureClient!(c);
					c["Locker"]!["DoorWatcherFrequency"] = 3000;
				},
			};
			return KioskSession.RunAsync(nameof(OutOfOrder_RefusedNotOperational), output, options, async kiosk => {
				await kiosk.WaitForLogLineAsync($"Agent pipe: server pokrenut ({pipe}", 0);
				var mark = kiosk.Log.Mark();
				await kiosk.Locker.SetModuleAsync(1, isOn: false);
				await kiosk.Locker.SetModuleAsync(2, isOn: false);
				await kiosk.WaitForLogLineAsync("Paketomat je u funkciji = [False]", mark, TimeSpan.FromSeconds(60));
				await Poll.UntilAsync(() => kiosk.ReadStatus()?["Operational"]?.GetValue<bool>() == false,
									  TimeSpan.FromSeconds(10), "status.lock Operational=false", kiosk.EnsureClientAlive);

				mark = kiosk.Log.Mark();
				var (exitCode, response) = await SendPickupAsync(kiosk, pipe, kiosk.Data.RecipientPackage(), NewId());
				Assert.Equal(5, exitCode);
				Assert.Equal(AgentResultCodes.NotOperational, response?.ResultCode);
				Assert.Empty(kiosk.Log.TransitionsAfter(mark));

				mark = kiosk.Log.Mark();
				await kiosk.Locker.SetModuleAsync(1, isOn: true);
				await kiosk.Locker.SetModuleAsync(2, isOn: true);
				await kiosk.WaitForLogLineAsync("Paketomat je u funkciji = [True]", mark, TimeSpan.FromSeconds(60));
				await kiosk.WaitForStateAsync(WorkflowState.Start, mark);
			});
		}

		/// <summary>
		/// Pad programa dok stranica čeka kod: posle restarta je preuzimanje zaključeno kao Interrupted, sa dodeljenim vratima;
		/// ništa nije otključano ni potvrđeno, kiosk je slobodan.
		/// </summary>
		[SkippableFact]
		public Task ProcessKilledWhileAwaitingCode_Interrupted_NothingOpened()
		{
			var pipe = RequireAgent();
			return KioskSession.RunAsync(nameof(ProcessKilledWhileAwaitingCode_Interrupted_NothingOpened), output, WithPipe(pipe), async kiosk => {
				var package = kiosk.Data.RecipientPackage(amount: 0);
				await kiosk.Server.AddPackageAsync(package);
				var serverMark = await kiosk.Server.MarkAsync();
				var unlockMark = await kiosk.Locker.MarkUnlocksAsync();

				var request = await BeginToConfirmationAsync(kiosk, pipe, package);
				var mark = kiosk.Log.Mark();
				await kiosk.RestartClientAsync(hard: true);
				await kiosk.WaitForLogLineAsync($"Agent pipe: server pokrenut ({pipe}", mark);
				Assert.NotEmpty(kiosk.Log.LinesAfter(mark, $"{AgentCommands.RecipientPickup} {request} je prekinut restartom programa"));

				var result = await CompletedResultAsync(kiosk, pipe, request);
				Assert.Equal(RecipientPickupOutcomes.Interrupted, result.Outcome);
				Assert.Equal(package.BrojPozicije, result.DoorNumber);
				AssertNothingOpened(await kiosk.Server.GetBusinessCallsAsync(serverMark), await kiosk.Locker.GetUnlocksAsync(unlockMark));
				await WaitForBusyAsync(kiosk, busy: false);
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

		/// <summary>RecipientPickup sa telefonom i šifrom paketa; vraća izlaz test klijenta i odgovor kioska.</summary>
		private static Task<(int ExitCode, AgentResponse? Response)> SendPickupAsync(KioskSession kiosk, string pipe, SimPackage package, string requestId)
			=> LocalSystemAgentClient.RemoteServiceAsync(kiosk, pipe, "--recipient-pickup", package.TelefonPrimaoca, package.KodZaOtvaranje.ToString(),
														 PresenceCode, "--request-id", requestId);

		/// <summary>
		/// RecipientPickup: InProgress, prelaz Start → RecipientRemoteValidating. Vraća requestId i kursor uzet pre slanja
		/// (provera na serveru može završiti pre nego što test klijent upiše odgovor).
		/// </summary>
		private static async Task<(string RequestId, int Mark)> StartAsync(KioskSession kiosk, string pipe, SimPackage package)
		{
			await kiosk.WaitForLogLineAsync($"Agent pipe: server pokrenut ({pipe}", 0);
			var request = NewId();
			var mark = kiosk.Log.Mark();
			var (exitCode, response) = await SendPickupAsync(kiosk, pipe, package, request);
			Assert.True(exitCode == 0, $"RecipientPickup: izlaz test klijenta {exitCode}, odgovor {response?.ResultCode} — {response?.Message}");
			Assert.Equal(AgentResultCodes.InProgress, response?.ResultCode);
			var validating = await kiosk.WaitForStateAsync(WorkflowState.RecipientRemoteValidating, mark);
			Assert.Equal((WorkflowState.Start, nameof(WorkflowTrigger.RecipientRemotePickupStarted)), (validating.From, validating.Trigger));
			return (request, mark);
		}

		/// <summary>Preuzimanje do stranice za kod prisustva: prelaz posle provere na serveru, stranica, kiosk zauzet. Vraća requestId.</summary>
		private static async Task<string> BeginToConfirmationAsync(KioskSession kiosk, string pipe, SimPackage package)
		{
			var (request, mark) = await StartAsync(kiosk, pipe, package);
			var confirmation = await kiosk.WaitForStateAsync(WorkflowState.RecipientRemoteConfirmation, mark);
			Assert.Equal(nameof(WorkflowTrigger.RecipientNoPaymentNeeded), confirmation.Trigger);
			await kiosk.Ui.WaitForPageAsync("RecipientRemoteConfirmationPage");
			await WaitForBusyAsync(kiosk, busy: true);
			return request;
		}

		/// <summary>GetCommandResult za preuzimanje (bez ClientInstanceId, radi i posle restarta kioska).</summary>
		private static async Task<RecipientPickupResult> ResultAsync(KioskSession kiosk, string pipe, string request)
		{
			var (exitCode, response) = await LocalSystemAgentClient.RemoteServiceAsync(kiosk, pipe, "--command-result", request);
			Assert.True(exitCode == 0, $"GetCommandResult: izlaz test klijenta {exitCode}, odgovor {response?.ResultCode} — {response?.Message}");
			return AgentJson.ReadResult<RecipientPickupResult>(response!)
				   ?? throw new E2EException($"GetCommandResult {request}: {response!.ResultCode} bez rezultata — {response.Message}");
		}

		/// <summary>Rezultat koji ispunjava uslov (upis u dnevnik ide posle prelaza, u pozadini).</summary>
		private static Task<RecipientPickupResult> WaitForResultAsync(KioskSession kiosk, string pipe, string request,
																	  Func<RecipientPickupResult, bool> condition, string description)
			=> Poll.UntilAsync(async () => await ResultAsync(kiosk, pipe, request) is { } result && condition(result) ? result : null,
							   TimeSpan.FromSeconds(30), $"rezultat preuzimanja {request}: {description}", kiosk.EnsureClientAlive);

		private static async Task<RecipientPickupResult> CompletedResultAsync(KioskSession kiosk, string pipe, string request)
		{
			var result = await WaitForResultAsync(kiosk, pipe, request, r => r.Phase == RecipientPickupPhases.Completed, "faza Completed");
			kiosk.Trace($"preuzimanje {request}: {result.Outcome}, vrata {result.DoorNumber}, pogrešnih kodova {result.FailedCodeAttempts}, potvrda {result.Confirmation}");
			return result;
		}

		/// <summary>Ništa nije otključano i potvrda preuzimanja nije poslata serveru.</summary>
		private static void AssertNothingOpened(IReadOnlyList<ServerCall> calls, IReadOnlyList<UnlockEntry> unlocks)
		{
			Assert.DoesNotContain(calls, c => c.Endpoint == "PrimaocPraznjenjePozicijePotvrda");
			Assert.Empty(unlocks);
		}

		/// <summary>status.lock sa zadatom zauzetošću (upis ide posle prelaza, pa se čeka).</summary>
		private static Task<JsonNode> WaitForBusyAsync(KioskSession kiosk, bool busy)
			=> Poll.UntilAsync(() => kiosk.ReadStatus() is { } status && status["Busy"]!.GetValue<bool>() == busy ? status : null,
							   TimeSpan.FromSeconds(10), $"status.lock Busy={busy}", kiosk.EnsureClientAlive);
	}
}