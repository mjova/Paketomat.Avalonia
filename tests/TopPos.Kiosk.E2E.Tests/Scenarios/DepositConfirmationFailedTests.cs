using TopPos.Kiosk.Workflow;
using TopPos.E2E.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace TopPos.E2E.Tests.Scenarios
{
	/// <summary>
	/// Potvrda ostavljanja pre nastavka (P5, 2026-10-14): server poziciju zauzima tek potvrdom, pa kurir/pošiljalac ne nastavlja
	/// dok je server ne potvrdi. Kiosk šalje potvrdu dvaput, pa prikazuje ekran sa izborom prema ishodu:
	/// sigurno nije upisano → „Ponovi slanje" / „Prekini i preuzmi paket"; odbijeno → samo „Prekini i preuzmi paket";
	/// ne zna se → „Ponovi slanje" / „Ostavi paket i nastavi". Vrata se otvaraju samo na „Preuzmi"; istek roka na ekranu
	/// ostavlja paket bez potvrde. Potvrda ostavljanja ne ide u red (ServerConfirmations.json). E2E: RequestTimeout 5 s,
	/// OperationTimeout 20 s.
	/// </summary>
	[Collection(E2ECollection.Name)]
	public sealed class DepositConfirmationFailedTests(ITestOutputHelper output)
	{
		private const string Endpoint = "KurirPunjenjePozicijePotvrda";
		internal const string Page = "DepositConfirmationFailedPage";
		private const string Retry = Page + ".Retry";
		internal const string ReturnPackage = Page + ".ReturnPackage";
		private const string LeavePackage = Page + ".LeavePackage";
		private const string SendingMessageLine = "prikazana poruka o slanju";
		internal const string ReopenedLine = "ponovo otvorena; ceka se da se paket preuzme";
		private const string ReturnedLine = "zatvorena posle preuzimanja paketa";
		private const string LeftLine = "ne zna se da li je server upisao ostavljanje; paket ostaje u vratima";
		private const string TimeoutLine = "niko nije izabrao radnju posle neuspele potvrde ostavljanja";
		// dva slanja po RequestTimeout (5 s) + Polly pauze
		private static readonly TimeSpan __failedTimeout = TimeSpan.FromSeconds(30);
		private static readonly KioskOptions __autoConfirm = new() { ConfigureClient = c => c["General"]!["ConfirmPackageDeposition"] = false };

		/// <summary>Prvo slanje ne prođe (404, sigurno nije upisano), drugo prođe: bez ekrana, kurir nastavlja na skeniranje.</summary>
		[SkippableFact]
		public Task CourierAuto_SecondAttemptConfirms_NoScreen()
			=> KioskSession.RunAsync(nameof(CourierAuto_SecondAttemptConfirms_NoScreen), output, __autoConfirm, async kiosk => {
				var (package, door) = await CourierDepositUntilDoorOpenAsync(kiosk);
				await kiosk.Server.ArmFaultAsync(Endpoint, status: 404, count: 1);	// Polly ga ne ponavlja
				var serverMark = await kiosk.Server.MarkAsync();
				var mark = kiosk.Log.Mark();

				var next = await kiosk.CloseDoorAsync(door, WorkflowState.CourierPackageDepositBarcodeScan, __failedTimeout);
				Assert.Equal((WorkflowState.CourierPackageDeposit, "DepositDoorClosedAutoConfirm"), (next.From, next.Trigger));
				Assert.Single(kiosk.Log.TransitionsAfter(mark));
				Assert.Empty(kiosk.Log.LinesAfter(mark, SendingMessageLine));	// oba odgovora brža od 1 s
				Assert.Equal(new[] { 404, 200 }, (await ConfirmCallsAsync(kiosk, serverMark)).Select(c => c.StatusCode));
				await kiosk.Ui.WaitForPageAsync("CourierBarcodeScanPage");
				await CourierTests.LogoutFromScanAsync(kiosk);
				await AssertRegisteredAsync(kiosk, package, door);
			});

		/// <summary>Server kasni 3 s (ispod RequestTimeout-a): poruka o slanju se prikaže, potvrda prođe.</summary>
		[SkippableFact]
		public Task CourierAuto_SlowServer_ShowsSendingMessage()
			=> KioskSession.RunAsync(nameof(CourierAuto_SlowServer_ShowsSendingMessage), output, __autoConfirm, async kiosk => {
				var (package, door) = await CourierDepositUntilDoorOpenAsync(kiosk);
				await kiosk.Server.ArmFaultAsync(Endpoint, delayMs: 3000);
				var mark = kiosk.Log.Mark();

				var next = await kiosk.CloseDoorAsync(door, WorkflowState.CourierPackageDepositBarcodeScan, __failedTimeout);
				Assert.Equal("DepositDoorClosedAutoConfirm", next.Trigger);
				var shown = kiosk.Log.FirstLineAfter(mark, SendingMessageLine);
				Assert.True(shown is { } line && line < next.Line, "Poruka o slanju mora biti prikazana pre nastavka.");
				await kiosk.Ui.WaitForPageAsync("CourierBarcodeScanPage");
				await CourierTests.LogoutFromScanAsync(kiosk);
				await AssertRegisteredAsync(kiosk, package, door);
			});

		/// <summary>Server ne radi (503): ekran „Ponovi" / „Preuzmi", Home ugašen; server proradi → „Ponovi" → skeniranje.</summary>
		[SkippableFact]
		public Task CourierAuto_ServerDown_RetrySucceeds_BackToScan()
			=> KioskSession.RunAsync(nameof(CourierAuto_ServerDown_RetrySucceeds_BackToScan), output, __autoConfirm, async kiosk => {
				var (package, door) = await CourierDepositUntilDoorOpenAsync(kiosk);
				await ArmServerDownAsync(kiosk);
				var mark = kiosk.Log.Mark();

				var failed = await kiosk.CloseDoorAsync(door, WorkflowState.CourierDepositConfirmationFailed, __failedTimeout);
				Assert.Equal("DepositConfirmationFailed", failed.Trigger);
				await AssertButtonsAsync(kiosk, retry: true, returnPackage: true, leave: false);

				await kiosk.Server.ClearFaultsAsync();
				kiosk.Trace("server ponovo odgovara");
				var unlockMark = await kiosk.Locker.MarkUnlocksAsync();
				var retryMark = kiosk.Log.Mark();
				await kiosk.Ui.TapAsync(Retry);
				var next = await kiosk.WaitForStateAsync(WorkflowState.CourierPackageDepositBarcodeScan, retryMark);
				Assert.Equal("DepositConfirmationRetrySucceeded", next.Trigger);
				await kiosk.Ui.WaitForPageAsync("CourierBarcodeScanPage");
				await CourierTests.LogoutFromScanAsync(kiosk);

				Assert.Empty(await kiosk.Locker.GetUnlocksAsync(unlockMark));
				await AssertRegisteredAsync(kiosk, package, door);
				Assert.Empty(kiosk.Log.IgnoredTriggersAfter(mark));
			});

		/// <summary>Server ne radi (503) → „Preuzmi" → ista vrata se otvaraju → kurir zatvori → skeniranje; server paket ne vodi.</summary>
		[SkippableFact]
		public Task CourierAuto_ServerDown_ReturnPackage_BackToScan()
			=> KioskSession.RunAsync(nameof(CourierAuto_ServerDown_ReturnPackage_BackToScan), output, __autoConfirm, async kiosk => {
				var (package, door) = await CourierDepositUntilDoorOpenAsync(kiosk);
				await ArmServerDownAsync(kiosk);
				await kiosk.CloseDoorAsync(door, WorkflowState.CourierDepositConfirmationFailed, __failedTimeout);
				await AssertButtonsAsync(kiosk, retry: true, returnPackage: true, leave: false);

				var next = await ReturnPackageAsync(kiosk, door, WorkflowState.CourierPackageDepositBarcodeScan);
				Assert.Equal("DepositPackageReturned", next.Trigger);
				await kiosk.Ui.WaitForPageAsync("CourierBarcodeScanPage");
				await CourierTests.LogoutFromScanAsync(kiosk);
				await AssertNotRegisteredAsync(kiosk, package);
			});

		/// <summary>Eksplicitna potvrda, server odbije (400): samo „Preuzmi", bez drugog slanja → vrata → skeniranje.</summary>
		[SkippableFact]
		public Task CourierExplicit_Rejected_OnlyReturnPackage_BackToScan()
			=> KioskSession.RunAsync(nameof(CourierExplicit_Rejected_OnlyReturnPackage_BackToScan), output, async kiosk => {
				var (package, door) = await CourierDepositUntilDoorOpenAsync(kiosk);
				await kiosk.CloseDoorAsync(door, WorkflowState.CourierDepositConfirmation);
				await kiosk.Ui.WaitForPageAsync("CourierDepositConfirmationPage");

				await kiosk.Server.ArmFaultAsync(Endpoint, error: "E2E: ostavljanje odbijeno");
				var serverMark = await kiosk.Server.MarkAsync();
				var mark = kiosk.Log.Mark();
				await kiosk.Ui.TapAsync("CourierDepositConfirmationPage.Confirm");
				var failed = await kiosk.WaitForStateAsync(WorkflowState.CourierDepositConfirmationFailed, mark, __failedTimeout);
				Assert.Equal((WorkflowState.CourierDepositConfirmation, "DepositConfirmationFailed"), (failed.From, failed.Trigger));
				await AssertButtonsAsync(kiosk, retry: false, returnPackage: true, leave: false);
				Assert.Equal(new[] { 400 }, (await ConfirmCallsAsync(kiosk, serverMark)).Select(c => c.StatusCode));	// odbijanje se ne ponavlja

				var next = await ReturnPackageAsync(kiosk, door, WorkflowState.CourierPackageDepositBarcodeScan);
				Assert.Equal("DepositPackageReturned", next.Trigger);
				await kiosk.Ui.WaitForPageAsync("CourierBarcodeScanPage");
				await CourierTests.LogoutFromScanAsync(kiosk);
				await AssertNotRegisteredAsync(kiosk, package);
			});

		/// <summary>
		/// Odgovor ne stigne do kioska, a server je potvrdu obradio (502 proxy-ja posle obrade): ne zna se → „Ponovi" /
		/// „Ostavi"; „Ostavi" → paket ostaje u vratima, kurir nastavlja; vrata se ne otvaraju.
		/// </summary>
		[SkippableFact]
		public Task CourierAuto_UnknownOutcome_LeavePackage_BackToScan()
			=> KioskSession.RunAsync(nameof(CourierAuto_UnknownOutcome_LeavePackage_BackToScan), output, __autoConfirm, async kiosk => {
				var (package, door) = await CourierDepositUntilDoorOpenAsync(kiosk);
				await kiosk.Server.ArmFaultAsync(Endpoint, status: 502, count: 0, afterProcessing: true);
				kiosk.Trace($"server obradi {Endpoint}, a proxy vrati 502");
				var unlockMark = await kiosk.Locker.MarkUnlocksAsync();
				await kiosk.CloseDoorAsync(door, WorkflowState.CourierDepositConfirmationFailed, __failedTimeout);
				await AssertButtonsAsync(kiosk, retry: true, returnPackage: false, leave: true);

				var mark = kiosk.Log.Mark();
				await kiosk.Ui.TapAsync(LeavePackage);
				var next = await kiosk.WaitForStateAsync(WorkflowState.CourierPackageDepositBarcodeScan, mark);
				Assert.Equal("DepositLeftUnconfirmed", next.Trigger);
				Assert.NotEmpty(kiosk.Log.LinesAfter(mark, LeftLine));
				await kiosk.Ui.WaitForPageAsync("CourierBarcodeScanPage");
				await CourierTests.LogoutFromScanAsync(kiosk);
				await kiosk.Server.ClearFaultsAsync();

				Assert.Empty(await kiosk.Locker.GetUnlocksAsync(unlockMark));
				await AssertRegisteredAsync(kiosk, package, door);	// server je potvrdu upisao, odgovor se izgubio
			});

		/// <summary>Kurir ode bez izbora: posle OperationTimeout-a Start, vrata ostaju zatvorena, potvrda se ne šalje.</summary>
		[SkippableFact]
		public Task CourierAuto_ScreenTimeout_PackageStays_Start()
			=> KioskSession.RunAsync(nameof(CourierAuto_ScreenTimeout_PackageStays_Start), output, __autoConfirm, async kiosk => {
				var (package, door) = await CourierDepositUntilDoorOpenAsync(kiosk);
				await ArmServerDownAsync(kiosk);
				var unlockMark = await kiosk.Locker.MarkUnlocksAsync();
				await kiosk.CloseDoorAsync(door, WorkflowState.CourierDepositConfirmationFailed, __failedTimeout);
				await kiosk.Ui.WaitForPageAsync(Page);
				var serverMark = await kiosk.Server.MarkAsync();

				var mark = kiosk.Log.Mark();
				var start = await kiosk.WaitForStateAsync(WorkflowState.Start, mark, TimeSpan.FromSeconds(40));
				Assert.Equal((WorkflowState.CourierDepositConfirmationFailed, "DepositConfirmationFailedTimeout"), (start.From, start.Trigger));
				Assert.NotEmpty(kiosk.Log.LinesAfter(mark, TimeoutLine));
				await kiosk.Ui.WaitForPageAsync("StartPage");
				await kiosk.Server.ClearFaultsAsync();

				Assert.Empty(await ConfirmCallsAsync(kiosk, serverMark));	// na ekranu se ništa ne šalje samo od sebe
				Assert.Empty(await kiosk.Locker.GetUnlocksAsync(unlockMark));
				Assert.True((await kiosk.Locker.GetDoorAsync(door)).IsLocked);
				await AssertNotRegisteredAsync(kiosk, package);
			});

		/// <summary>Pošiljalac, server ne radi (503) → „Preuzmi" → vrata → zatvaranje → Start; server paket ne vodi.</summary>
		[PendingFlow("pošiljalac", "F1")]	// v4
		public Task Sender_ServerDown_ReturnPackage_Start()
			=> KioskSession.RunAsync(nameof(Sender_ServerDown_ReturnPackage_Start), output, async kiosk => {
				var (package, door) = await SenderDepositUntilDoorOpenAsync(kiosk);
				await ArmServerDownAsync(kiosk);
				var failed = await kiosk.CloseDoorAsync(door, WorkflowState.SenderDepositConfirmationFailed, __failedTimeout);
				Assert.Equal("SenderConfirmationFailed", failed.Trigger);
				await AssertButtonsAsync(kiosk, retry: true, returnPackage: true, leave: false);

				var next = await ReturnPackageAsync(kiosk, door, WorkflowState.Start);
				Assert.Equal("SenderPackageReturned", next.Trigger);
				await kiosk.Ui.WaitForPageAsync("StartPage");
				await AssertNotRegisteredAsync(kiosk, package);
			});

		/// <summary>Pošiljalac, server ne radi (503) → server proradi → „Ponovi" → Start; server vodi paket u vratima.</summary>
		[PendingFlow("pošiljalac", "F1")]	// v4
		public Task Sender_ServerDown_RetrySucceeds_Start()
			=> KioskSession.RunAsync(nameof(Sender_ServerDown_RetrySucceeds_Start), output, async kiosk => {
				var (package, door) = await SenderDepositUntilDoorOpenAsync(kiosk);
				await ArmServerDownAsync(kiosk);
				await kiosk.CloseDoorAsync(door, WorkflowState.SenderDepositConfirmationFailed, __failedTimeout);
				await AssertButtonsAsync(kiosk, retry: true, returnPackage: true, leave: false);

				await kiosk.Server.ClearFaultsAsync();
				kiosk.Trace("server ponovo odgovara");
				var mark = kiosk.Log.Mark();
				await kiosk.Ui.TapAsync(Retry);
				var next = await kiosk.WaitForStateAsync(WorkflowState.Start, mark);
				Assert.Equal("SenderConfirmationRetrySucceeded", next.Trigger);
				await kiosk.Ui.WaitForPageAsync("StartPage");
				await AssertRegisteredAsync(kiosk, package, door);
			});

		/// <summary>
		/// C17: „Preuzmi", a vrata se ne otvore (blokirana) → dva pokušaja, poruka → prijava greške „N" → kurir nastavlja na
		/// skeniranje; paket ostaje u vratima, server ga ne vodi.
		/// </summary>
		[SkippableFact]
		public Task CourierAuto_ReturnPackage_DoorDoesNotOpen_ReportsDoorError_BackToScan()
			=> KioskSession.RunAsync(nameof(CourierAuto_ReturnPackage_DoorDoesNotOpen_ReportsDoorError_BackToScan), output, __autoConfirm, async kiosk => {
				var (package, door) = await CourierDepositUntilDoorOpenAsync(kiosk);
				await ArmServerDownAsync(kiosk);
				await kiosk.CloseDoorAsync(door, WorkflowState.CourierDepositConfirmationFailed, __failedTimeout);
				await AssertButtonsAsync(kiosk, retry: true, returnPackage: true, leave: false);
				await kiosk.Locker.BlockDoorAsync(door);
				kiosk.Trace($"vrata {door} blokirana");

				var serverMark = await kiosk.Server.MarkAsync();
				var unlockMark = await kiosk.Locker.MarkUnlocksAsync();
				var mark = kiosk.Log.Mark();
				await kiosk.Ui.TapAsync(ReturnPackage);
				var next = await kiosk.WaitForStateAsync(WorkflowState.CourierPackageDepositBarcodeScan, mark, TimeSpan.FromSeconds(30));
				Assert.Equal((WorkflowState.CourierDepositConfirmationFailed, "DepositPackageReturned"), (next.From, next.Trigger));
				Assert.Empty(kiosk.Log.LinesAfter(mark, ReopenedLine));
				await kiosk.Ui.WaitForPageAsync("CourierBarcodeScanPage");

				var unlocks = await kiosk.Locker.GetUnlocksAsync(unlockMark);
				Assert.Equal(2, unlocks.Count);
				Assert.All(unlocks, u => Assert.Equal((door, false), (u.Door, u.Unlocked)));
				await RecipientFailureTests.WaitForDoorErrorAsync(kiosk, serverMark, door, expectedError: "N");
				await CourierTests.LogoutFromScanAsync(kiosk);
				await AssertNotRegisteredAsync(kiosk, package);
			});

		/// <summary>
		/// C17: „Preuzmi" → vrata se otvore, a kurir ih ne zatvori → istek praćenja → prijava greške „O" → Start (Reset);
		/// serveru se ništa ne šalje.
		/// </summary>
		[SkippableFact]
		public Task CourierAuto_ReturnPackage_DoorLeftOpen_ReportsOpenDoor_Start()
			=> KioskSession.RunAsync(nameof(CourierAuto_ReturnPackage_DoorLeftOpen_ReportsOpenDoor_Start), output, __autoConfirm, async kiosk => {
				var (package, door) = await CourierDepositUntilDoorOpenAsync(kiosk);
				await ArmServerDownAsync(kiosk);
				await kiosk.CloseDoorAsync(door, WorkflowState.CourierDepositConfirmationFailed, __failedTimeout);
				await AssertButtonsAsync(kiosk, retry: true, returnPackage: true, leave: false);

				var serverMark = await kiosk.Server.MarkAsync();
				var mark = kiosk.Log.Mark();
				await kiosk.Ui.TapAsync(ReturnPackage);
				await kiosk.WaitForLogLineAsync(ReopenedLine, mark);
				kiosk.Trace($"vrata {door} ostaju otvorena");
				var start = await kiosk.WaitForStateAsync(WorkflowState.Start, mark, TimeSpan.FromSeconds(45));
				Assert.Equal((WorkflowState.CourierDepositConfirmationFailed, "Reset"), (start.From, start.Trigger));
				Assert.Empty(kiosk.Log.LinesAfter(mark, ReturnedLine));
				await kiosk.Ui.WaitForPageAsync("StartPage");
				Assert.False((await kiosk.Locker.GetDoorAsync(door)).IsLocked);

				var calls = await RecipientFailureTests.WaitForDoorErrorAsync(kiosk, serverMark, door, expectedError: "O");
				Assert.DoesNotContain(calls, c => c.Endpoint == Endpoint);
				await AssertNotRegisteredAsync(kiosk, package);
			});

		/// <summary>S06: pošiljalac, server odbije ostavljanje (400): samo „Preuzmi", bez drugog slanja → vrata → Start.</summary>
		[PendingFlow("pošiljalac", "F1")]	// v4
		public Task Sender_Rejected_OnlyReturnPackage_Start()
			=> KioskSession.RunAsync(nameof(Sender_Rejected_OnlyReturnPackage_Start), output, async kiosk => {
				var (package, door) = await SenderDepositUntilDoorOpenAsync(kiosk);
				await kiosk.Server.ArmFaultAsync(Endpoint, error: "E2E: ostavljanje odbijeno");
				var serverMark = await kiosk.Server.MarkAsync();
				var failed = await kiosk.CloseDoorAsync(door, WorkflowState.SenderDepositConfirmationFailed, __failedTimeout);
				Assert.Equal("SenderConfirmationFailed", failed.Trigger);
				await AssertButtonsAsync(kiosk, retry: false, returnPackage: true, leave: false);
				Assert.Equal(new[] { 400 }, (await ConfirmCallsAsync(kiosk, serverMark)).Select(c => c.StatusCode));	// odbijanje se ne ponavlja

				var next = await ReturnPackageAsync(kiosk, door, WorkflowState.Start);
				Assert.Equal("SenderPackageReturned", next.Trigger);
				await kiosk.Ui.WaitForPageAsync("StartPage");
				await AssertNotRegisteredAsync(kiosk, package);
			});

		/// <summary>Prijava kurira, skeniranje novog paketa i otvaranje dodeljenih vrata.</summary>
		internal static async Task<(SimPackage Package, int Door)> CourierDepositUntilDoorOpenAsync(KioskSession kiosk)
		{
			var package = kiosk.Data.DepositPackage(size: "M");
			await kiosk.Server.AddPackageAsync(package);
			var unlockMark = await kiosk.Locker.MarkUnlocksAsync();

			await CourierTests.LoginAsync(kiosk, expect: WorkflowState.CourierDepositPrompt);
			await kiosk.Ui.WaitForPageAsync("CourierDepositPromptPage");
			var mark = kiosk.Log.Mark();
			await kiosk.Ui.TapAsync("CourierDepositPromptPage.Deposit");
			await kiosk.WaitForStateAsync(WorkflowState.CourierPackageDepositBarcodeScan, mark);
			await kiosk.Ui.WaitForPageAsync("CourierBarcodeScanPage");
			await kiosk.ScanAsync(package.Barkod, WorkflowState.CourierPackageDeposit);
			var door = (await kiosk.WaitForUnlockAsync(unlockMark)).Door;
			await kiosk.WaitForDoorOpenAsync(door, "CourierPackageDepositPage");
			return (package, door);
		}

		/// <summary>„Ostavi paket" na Start ekranu, skeniranje i otvaranje dodeljenih vrata.</summary>
		internal static async Task<(SimPackage Package, int Door)> SenderDepositUntilDoorOpenAsync(KioskSession kiosk)
		{
			var package = kiosk.Data.DepositPackage(size: "M");
			await kiosk.Server.AddPackageAsync(package);
			var unlockMark = await kiosk.Locker.MarkUnlocksAsync();

			var mark = kiosk.Log.Mark();
			await kiosk.Ui.TapAsync("StartPage.SenderDepositPackage");
			await kiosk.WaitForStateAsync(WorkflowState.SenderDepositBarcodeScan, mark);
			await kiosk.Ui.WaitForPageAsync("SenderBarcodeScanPage");
			await kiosk.ScanAsync(package.Barkod, WorkflowState.SenderPackageDeposit);
			var door = (await kiosk.WaitForUnlockAsync(unlockMark)).Door;
			await kiosk.WaitForDoorOpenAsync(door, "SenderPackageDepositPage");
			return (package, door);
		}

		/// <summary>Server ne prima potvrdu ostavljanja (503) dok se greška ne obriše (count 0: Polly ponavlja 5xx).</summary>
		internal static async Task ArmServerDownAsync(KioskSession kiosk)
		{
			await kiosk.Server.ArmFaultAsync(Endpoint, status: 503, count: 0);
			kiosk.Trace($"server ne odgovara na {Endpoint} (503)");
		}

		/// <summary>Ekran neuspele potvrde je prikazan, dugmad odgovaraju ishodu, a Home je ugašen.</summary>
		internal static async Task AssertButtonsAsync(KioskSession kiosk, bool retry, bool returnPackage, bool leave)
		{
			await kiosk.Ui.WaitForPageAsync(Page);
			foreach (var (button, visible) in new[] { (Retry, retry), (ReturnPackage, returnPackage), (LeavePackage, leave) }) {
				if (visible) {
					await kiosk.Ui.WaitForAsync(button);
				}
				else {
					await kiosk.Ui.WaitForHiddenAsync(button, TimeSpan.FromSeconds(3));
				}
			}
			Assert.False(await kiosk.Ui.IsEnabledAsync("Header.Home"), "Home mora biti onemogućen dok se ne odluči šta sa paketom");
		}

		/// <summary>„Prekini i preuzmi paket": ista vrata se otključaju (jednom), kurir/pošiljalac ih zatvori.</summary>
		internal static async Task<Transition> ReturnPackageAsync(KioskSession kiosk, int door, WorkflowState expected)
		{
			var unlockMark = await kiosk.Locker.MarkUnlocksAsync();
			var mark = kiosk.Log.Mark();
			await kiosk.Ui.TapAsync(ReturnPackage);
			Assert.Equal(door, (await kiosk.WaitForUnlockAsync(unlockMark)).Door);
			// stranica je već prikazana: spremnost za zatvaranje daje linija loga posle provere otvaranja
			await kiosk.WaitForLogLineAsync(ReopenedLine, mark);
			await kiosk.WaitForDoorOpenAsync(door, Page);
			Assert.Single(await kiosk.Locker.GetUnlocksAsync(unlockMark));
			Assert.Empty(kiosk.Log.TransitionsAfter(mark));
			var next = await kiosk.CloseDoorAsync(door, expected);
			Assert.NotEmpty(kiosk.Log.LinesAfter(mark, ReturnedLine));
			return next;
		}

		private static async Task<List<ServerCall>> ConfirmCallsAsync(KioskSession kiosk, long serverMark)
			=> (await kiosk.Server.GetBusinessCallsAsync(serverMark)).Where(c => c.Endpoint == Endpoint).ToList();

		private async Task AssertRegisteredAsync(KioskSession kiosk, SimPackage package, int door)
		{
			Assert.Equal(door, Assert.Single(await kiosk.Server.GetPackagesAsync(), p => p.Barkod == package.Barkod).BrojPozicije);
			output.WriteLine($"{Endpoint}: server vodi paket u vratima {door}");
		}

		private async Task AssertNotRegisteredAsync(KioskSession kiosk, SimPackage package)
		{
			await kiosk.Server.ClearFaultsAsync();
			await Task.Delay(TimeSpan.FromSeconds(3));	// namerno: dokaz da se potvrda ne šalje naknadno (nema reda)
			Assert.Equal(0, Assert.Single(await kiosk.Server.GetPackagesAsync(), p => p.Barkod == package.Barkod).BrojPozicije);
			output.WriteLine($"{Endpoint}: server paket ne vodi ni u jednim vratima");
		}
	}
}
