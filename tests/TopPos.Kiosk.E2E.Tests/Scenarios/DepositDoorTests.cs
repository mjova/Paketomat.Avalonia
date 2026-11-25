using TopPos.Kiosk.Workflow;
using TopPos.E2E.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace TopPos.E2E.Tests.Scenarios
{
	/// <summary>
	/// Ostavljanje (kurir i pošiljalac) kad vrata ne rade ili kad korisnik odustane dok je sanduk otvoren — slučajevi u kojima
	/// paket može fizički ostati u sanduku, a server za njega ne zna (storyboard C13, C16, C19, S04, S05). Server poziciju
	/// zauzima tek potvrdom ostavljanja (KurirPunjenjePozicijePotvrda), pa test proverava da potvrda nije poslata i da server
	/// paket ne vodi ni u jednim vratima. E2E: OperationTimeout 20 s (rok za zatvaranje vrata), ConfirmPackageDeposition=true.
	/// </summary>
	[Collection(E2ECollection.Name)]
	public sealed class DepositDoorTests(ITestOutputHelper output)
	{
		private const string ConfirmEndpoint = "KurirPunjenjePozicijePotvrda";
		private static readonly TimeSpan __operationTimeout = TimeSpan.FromMilliseconds(20000);	// General.OperationTimeout u E2E konfiguraciji

		/// <summary>
		/// C19: vrata za ostavljanje se ne otvore (blokirana) → dva pokušaja, poruka → nazad na skeniranje (kurir ostaje
		/// prijavljen); server dobija prijavu greške „N", potvrde nema.
		/// </summary>
		[SkippableFact]
		public Task Courier_DoorDoesNotOpen_ReportsDoorError_BackToScan()
			=> KioskSession.RunAsync(nameof(Courier_DoorDoesNotOpen_ReportsDoorError_BackToScan), output, async kiosk => {
				var package = kiosk.Data.DepositPackage(size: "M");
				await kiosk.Server.AddPackageAsync(package);
				var serverMark = await kiosk.Server.MarkAsync();
				var unlockMark = await kiosk.Locker.MarkUnlocksAsync();
				await BlockDoorsOfSizeAsync(kiosk, "M");

				await CourierTests.OpenDepositScanAsync(kiosk);
				var mark = kiosk.Log.Mark();
				await kiosk.ScanAsync(package.Barkod, WorkflowState.CourierPackageDeposit);
				var back = await kiosk.WaitForStateAsync(WorkflowState.CourierPackageDepositBarcodeScan, mark, TimeSpan.FromSeconds(30));
				Assert.Equal((WorkflowState.CourierPackageDeposit, "DepositDoorOpenFailed"), (back.From, back.Trigger));
				await kiosk.Ui.WaitForPageAsync("CourierBarcodeScanPage");

				var door = await AssignedDoorAsync(kiosk, serverMark);
				await AssertDoorNeverOpenedAsync(kiosk, unlockMark, door);
				var calls = await RecipientFailureTests.WaitForDoorErrorAsync(kiosk, serverMark, door, expectedError: "N");
				Assert.DoesNotContain(calls, c => c.Endpoint == ConfirmEndpoint);

				await CourierTests.LogoutFromScanAsync(kiosk);
				await AssertNotRegisteredAsync(kiosk, package, serverMark);
			});

		/// <summary>
		/// C19: kurir ne zatvori vrata → praćenje ističe posle OperationTimeout-a → prijava greške „O" → Start (Reset); potvrda
		/// se ne šalje, pa server paket ne vodi iako je možda u sanduku.
		/// </summary>
		[SkippableFact]
		public Task Courier_DoorLeftOpen_ReportsOpenDoor_Start()
			=> KioskSession.RunAsync(nameof(Courier_DoorLeftOpen_ReportsOpenDoor_Start), output, async kiosk => {
				var serverMark = await kiosk.Server.MarkAsync();
				var (package, door) = await DepositConfirmationFailedTests.CourierDepositUntilDoorOpenAsync(kiosk);
				kiosk.Trace($"vrata {door} ostaju otvorena");

				var mark = kiosk.Log.Mark();
				var toStart = await kiosk.WaitForStateAsync(WorkflowState.Start, mark, __operationTimeout + TimeSpan.FromSeconds(25));
				Assert.Equal((WorkflowState.CourierPackageDeposit, "Reset"), (toStart.From, toStart.Trigger));
				await kiosk.Ui.WaitForPageAsync("StartPage");
				Assert.False((await kiosk.Locker.GetDoorAsync(door)).IsLocked);

				var calls = await RecipientFailureTests.WaitForDoorErrorAsync(kiosk, serverMark, door, expectedError: "O");
				Assert.DoesNotContain(calls, c => c.Endpoint == ConfirmEndpoint);
				await AssertNotRegisteredAsync(kiosk, package, serverMark);
			});

		/// <summary>
		/// C13: „Izloguj se" dok je sanduk otvoren → zahtev se pamti (poruka), ništa se ne dešava dok su vrata otvorena →
		/// zatvaranje → odjava (Start) bez potvrde ostavljanja.
		/// </summary>
		[SkippableFact]
		public Task Courier_LogoutWhileDoorOpen_ClosingDoorLogsOut_NoConfirmation()
			=> KioskSession.RunAsync(nameof(Courier_LogoutWhileDoorOpen_ClosingDoorLogsOut_NoConfirmation), output, async kiosk => {
				var serverMark = await kiosk.Server.MarkAsync();
				var (package, door) = await DepositConfirmationFailedTests.CourierDepositUntilDoorOpenAsync(kiosk);

				var mark = kiosk.Log.Mark();
				await kiosk.Ui.TapAsync("CourierPackageDepositPage.Cancel");
				// Zahtev se pamti kad handler preuzme semafor (odmah posle ove linije); zatvaranje pre toga vodi na potvrdu.
				await kiosk.WaitForLogLineAsync("CourierPackageDepositViewModel: executing CancelCommand", mark);
				await Task.Delay(TimeSpan.FromSeconds(1));	// namerno: dokaz da odjava čeka zatvaranje vrata
				Assert.Empty(kiosk.Log.TransitionsAfter(mark));

				var toStart = await kiosk.CloseDoorAsync(door, WorkflowState.Start);
				Assert.Equal((WorkflowState.CourierPackageDeposit, "DepositCancelledLogout"), (toStart.From, toStart.Trigger));
				Assert.DoesNotContain(kiosk.Log.TransitionsAfter(mark), t => t.To == WorkflowState.CourierDepositConfirmation);
				await kiosk.Ui.WaitForPageAsync("StartPage");

				await AssertNotRegisteredAsync(kiosk, package, serverMark);
			});

		/// <summary>
		/// C16: eksplicitna potvrda, prvi izbor „Otkaži" → nazad na skeniranje bez potvrde; ni istek roka ekrana potvrde (koji
		/// inače znači potvrdu) posle toga ne šalje ništa.
		/// </summary>
		[SkippableFact]
		public Task Courier_ExplicitConfirmation_CancelFirst_NoConfirmationEvenAfterTimeout()
			=> KioskSession.RunAsync(nameof(Courier_ExplicitConfirmation_CancelFirst_NoConfirmationEvenAfterTimeout), output, async kiosk => {
				var serverMark = await kiosk.Server.MarkAsync();
				var (package, door) = await DepositConfirmationFailedTests.CourierDepositUntilDoorOpenAsync(kiosk);
				var toConfirmation = await kiosk.CloseDoorAsync(door, WorkflowState.CourierDepositConfirmation);
				Assert.Equal("DepositDoorClosedExplicitConfirm", toConfirmation.Trigger);
				await kiosk.Ui.WaitForPageAsync("CourierDepositConfirmationPage");

				var mark = kiosk.Log.Mark();
				await kiosk.Ui.TapAsync("CourierDepositConfirmationPage.Cancel");
				var back = await kiosk.WaitForStateAsync(WorkflowState.CourierPackageDepositBarcodeScan, mark);
				Assert.Equal((WorkflowState.CourierDepositConfirmation, "DepositConfirmationCancelled"), (back.From, back.Trigger));
				Assert.NotEmpty(kiosk.Log.LinesAfter(mark, "kurir nije potvrdio ostavljanje (Otkazi)"));
				await kiosk.Ui.WaitForPageAsync("CourierBarcodeScanPage");

				await Task.Delay(__operationTimeout + TimeSpan.FromSeconds(3));	// namerno: istek roka potvrde ne sme ništa da pošalje
				Assert.Empty(kiosk.Log.LinesAfter(mark, "isteklo vreme za potvrdu ostavljanja"));
				Assert.DoesNotContain(await kiosk.Server.GetBusinessCallsAsync(serverMark), c => c.Endpoint == ConfirmEndpoint);

				await CourierTests.LogoutFromScanAsync(kiosk);
				await AssertNotRegisteredAsync(kiosk, package, serverMark);
			});

		/// <summary>
		/// S05: vrata pošiljaoca se ne otvore (blokirana) → dva pokušaja, poruka → nazad na skeniranje; prijava greške „N",
		/// potvrde nema.
		/// </summary>
		[PendingFlow("pošiljalac", "F1")]	// v4
		public Task Sender_DoorDoesNotOpen_ReportsDoorError_BackToScan()
			=> KioskSession.RunAsync(nameof(Sender_DoorDoesNotOpen_ReportsDoorError_BackToScan), output, async kiosk => {
				var package = kiosk.Data.DepositPackage(size: "M");
				await kiosk.Server.AddPackageAsync(package);
				var serverMark = await kiosk.Server.MarkAsync();
				var unlockMark = await kiosk.Locker.MarkUnlocksAsync();
				await BlockDoorsOfSizeAsync(kiosk, "M");

				await OpenSenderScanAsync(kiosk);
				var mark = kiosk.Log.Mark();
				await kiosk.ScanAsync(package.Barkod, WorkflowState.SenderPackageDeposit);
				var back = await kiosk.WaitForStateAsync(WorkflowState.SenderDepositBarcodeScan, mark, TimeSpan.FromSeconds(30));
				Assert.Equal((WorkflowState.SenderPackageDeposit, "SenderDoorOpenFailed"), (back.From, back.Trigger));
				await kiosk.Ui.WaitForPageAsync("SenderBarcodeScanPage");

				var door = await AssignedDoorAsync(kiosk, serverMark);
				await AssertDoorNeverOpenedAsync(kiosk, unlockMark, door);
				var calls = await RecipientFailureTests.WaitForDoorErrorAsync(kiosk, serverMark, door, expectedError: "N");
				Assert.DoesNotContain(calls, c => c.Endpoint == ConfirmEndpoint);

				var cancelMark = kiosk.Log.Mark();
				await kiosk.Ui.TapAsync("SenderBarcodeScanPage.Cancel");
				await kiosk.WaitForStateAsync(WorkflowState.Start, cancelMark);
				await AssertNotRegisteredAsync(kiosk, package, serverMark);
			});

		/// <summary>S05: pošiljalac ne zatvori vrata → istek praćenja → prijava greške „O" → Start (Reset), bez potvrde.</summary>
		[PendingFlow("pošiljalac", "F1")]	// v4
		public Task Sender_DoorLeftOpen_ReportsOpenDoor_Start()
			=> KioskSession.RunAsync(nameof(Sender_DoorLeftOpen_ReportsOpenDoor_Start), output, async kiosk => {
				var serverMark = await kiosk.Server.MarkAsync();
				var (package, door) = await SenderDepositUntilDoorOpenAsync(kiosk);
				kiosk.Trace($"vrata {door} ostaju otvorena");

				var mark = kiosk.Log.Mark();
				var toStart = await kiosk.WaitForStateAsync(WorkflowState.Start, mark, __operationTimeout + TimeSpan.FromSeconds(25));
				Assert.Equal((WorkflowState.SenderPackageDeposit, "Reset"), (toStart.From, toStart.Trigger));
				await kiosk.Ui.WaitForPageAsync("StartPage");
				Assert.False((await kiosk.Locker.GetDoorAsync(door)).IsLocked);

				var calls = await RecipientFailureTests.WaitForDoorErrorAsync(kiosk, serverMark, door, expectedError: "O");
				Assert.DoesNotContain(calls, c => c.Endpoint == ConfirmEndpoint);
				await AssertNotRegisteredAsync(kiosk, package, serverMark);
			});

		/// <summary>
		/// S04: „Otkaži" dok je sanduk otvoren → zahtev se pamti, ništa se ne dešava dok su vrata otvorena → zatvaranje →
		/// Start bez potvrde ostavljanja.
		/// </summary>
		[PendingFlow("pošiljalac", "F1")]	// v4
		public Task Sender_CancelWhileDoorOpen_ClosingDoorReturnsToStart_NoConfirmation()
			=> KioskSession.RunAsync(nameof(Sender_CancelWhileDoorOpen_ClosingDoorReturnsToStart_NoConfirmation), output, async kiosk => {
				var serverMark = await kiosk.Server.MarkAsync();
				var (package, door) = await SenderDepositUntilDoorOpenAsync(kiosk);

				var mark = kiosk.Log.Mark();
				await kiosk.Ui.TapAsync("SenderPackageDepositPage.Cancel");
				await kiosk.WaitForLogLineAsync("SenderPackageDepositViewModel: executing CancelCommand", mark);
				await Task.Delay(TimeSpan.FromSeconds(1));	// namerno: dokaz da otkaz čeka zatvaranje vrata
				Assert.Empty(kiosk.Log.TransitionsAfter(mark));

				var toStart = await kiosk.CloseDoorAsync(door, WorkflowState.Start);
				Assert.Equal((WorkflowState.SenderPackageDeposit, "SenderCancelled"), (toStart.From, toStart.Trigger));
				await kiosk.Ui.WaitForPageAsync("StartPage");

				await AssertNotRegisteredAsync(kiosk, package, serverMark);
			});

		/// <summary>„Ostavi paket" na Start ekranu → ekran za skeniranje pošiljaoca.</summary>
		private static async Task OpenSenderScanAsync(KioskSession kiosk)
		{
			var mark = kiosk.Log.Mark();
			await kiosk.Ui.TapAsync("StartPage.SenderDepositPackage");
			await kiosk.WaitForStateAsync(WorkflowState.SenderDepositBarcodeScan, mark);
			await kiosk.Ui.WaitForPageAsync("SenderBarcodeScanPage");
		}

		/// <summary>Pošiljalac: skeniranje novog paketa i otvaranje dodeljenih vrata.</summary>
		private static async Task<(SimPackage Package, int Door)> SenderDepositUntilDoorOpenAsync(KioskSession kiosk)
		{
			var package = kiosk.Data.DepositPackage(size: "M");
			await kiosk.Server.AddPackageAsync(package);
			var unlockMark = await kiosk.Locker.MarkUnlocksAsync();

			await OpenSenderScanAsync(kiosk);
			await kiosk.ScanAsync(package.Barkod, WorkflowState.SenderPackageDeposit);
			var door = (await kiosk.WaitForUnlockAsync(unlockMark)).Door;
			await kiosk.WaitForDoorOpenAsync(door, "SenderPackageDepositPage");
			return (package, door);
		}

		/// <summary>
		/// Server dodeljuje najniža slobodna vrata tražene veličine, a test ih ne zna unapred — blokira sva vrata te veličine.
		/// </summary>
		private static async Task BlockDoorsOfSizeAsync(KioskSession kiosk, string size)
		{
			var doors = (await kiosk.Locker.GetDoorsAsync()).Where(d => TestData.BoxSize(d.Number) == size).Select(d => d.Number).ToList();
			foreach (var door in doors) {
				await kiosk.Locker.BlockDoorAsync(door);
			}
			kiosk.Trace($"blokirana sva vrata veličine {size}: {string.Join(", ", doors)}");
		}

		/// <summary>Vrata iz (jedine) dodele servera posle kursora.</summary>
		private static async Task<int> AssignedDoorAsync(KioskSession kiosk, long serverMark)
		{
			var assignment = Assert.Single(await kiosk.Server.GetBusinessCallsAsync(serverMark), c => c.Endpoint == "KurirPunjenjePozicije");
			return int.Parse(assignment.ResponseValue("BrojPozicije")!);
		}

		/// <summary>Kiosk je dvaput pokušao da otključa dodeljena vrata, nijednom uspešno, i nijedna druga.</summary>
		private static async Task AssertDoorNeverOpenedAsync(KioskSession kiosk, long unlockMark, int door)
		{
			var unlocks = await kiosk.Locker.GetUnlocksAsync(unlockMark);
			Assert.Equal(2, unlocks.Count);
			Assert.All(unlocks, u => Assert.Equal((door, false), (u.Door, u.Unlocked)));
			Assert.True((await kiosk.Locker.GetDoorAsync(door)).IsLocked);
		}

		/// <summary>Potvrda ostavljanja nije poslata ni naknadno (nema reda), pa server paket ne vodi ni u jednim vratima.</summary>
		private async Task AssertNotRegisteredAsync(KioskSession kiosk, SimPackage package, long serverMark)
		{
			await Task.Delay(TimeSpan.FromSeconds(3));	// namerno: dokaz da se potvrda ne šalje naknadno
			Assert.DoesNotContain(await kiosk.Server.GetBusinessCallsAsync(serverMark), c => c.Endpoint == ConfirmEndpoint);
			Assert.Equal(0, Assert.Single(await kiosk.Server.GetPackagesAsync(), p => p.Barkod == package.Barkod).BrojPozicije);
			output.WriteLine($"{ConfirmEndpoint}: nije poslata, server paket ne vodi ni u jednim vratima");
		}
	}
}
