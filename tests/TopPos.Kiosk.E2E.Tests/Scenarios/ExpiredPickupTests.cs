using TopPos.Kiosk.Workflow;
using TopPos.E2E.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace TopPos.E2E.Tests.Scenarios
{
	/// <summary>
	/// Kurir preuzima istekao paket kad nešto pođe naopako (storyboard C08, C09, C20): pogrešan barkod, vrata koja se ne
	/// otvore ili ostanu otvorena, potvrda pražnjenja koju server ne primi. Potvrda pražnjenja ne ide u red, pa paket koji je
	/// izvađen bez potvrde server i dalje vodi u vratima — test to proverava kao poznato ponašanje. Jedan istekao paket: posle
	/// njega kurir dobija ekran za ostavljanje. E2E: OperationTimeout 20 s, ScanTrackerTimeoutInMilliseconds 60 s.
	/// </summary>
	[Collection(E2ECollection.Name)]
	public sealed class ExpiredPickupTests(ITestOutputHelper output)
	{
		private const string ConfirmEndpoint = "KurirPraznjenjePozicijePotvrda";
		private static readonly TimeSpan __operationTimeout = TimeSpan.FromMilliseconds(20000);	// General.OperationTimeout
		private static readonly TimeSpan __scanTimeout = TimeSpan.FromMilliseconds(60000);		// BarcodeScanner.ScanTrackerTimeoutInMilliseconds

		/// <summary>
		/// C08: dva pogrešna barkoda → posle drugog procedura se prekida bez potvrde → ekran za ostavljanje; server i dalje
		/// vodi paket u vratima.
		/// </summary>
		[SkippableFact]
		public Task WrongBarcodeTwice_ResolvedWithoutConfirmation_PackageStaysOnServer()
			=> KioskSession.RunAsync(nameof(WrongBarcodeTwice_ResolvedWithoutConfirmation_PackageStaysOnServer), output, async kiosk => {
				var (package, serverMark) = await PickUpUntilScanAsync(kiosk);

				await ScanRejectedAsync(kiosk, "E2E-POGRESAN-1");
				var mark = kiosk.Log.Mark();
				var resolved = await kiosk.ScanAsync("E2E-POGRESAN-2", WorkflowState.CourierDepositPrompt);
				Assert.Equal((WorkflowState.CourierPackagePickupBarcodeScan, "PickupBarcodeResolved"), (resolved.From, resolved.Trigger));
				Assert.NotEmpty(kiosk.Log.LinesAfter(mark, "neodgovarajući barkod je skreniran previše puta"));
				await kiosk.Ui.WaitForPageAsync("CourierDepositPromptPage");
				await CancelDepositPromptAsync(kiosk);

				Assert.DoesNotContain(await kiosk.Server.GetBusinessCallsAsync(serverMark), c => c.Endpoint == ConfirmEndpoint);
				AssertStillOnServer(await kiosk.Server.GetPackagesAsync(), package);
			});

		/// <summary>C08: pogrešan barkod jednom (poruka, skeniranje se nastavlja), pa ispravan → potvrda → ekran za ostavljanje.</summary>
		[SkippableFact]
		public Task WrongBarcodeThenCorrect_Confirms()
			=> KioskSession.RunAsync(nameof(WrongBarcodeThenCorrect_Confirms), output, async kiosk => {
				var (package, serverMark) = await PickUpUntilScanAsync(kiosk);

				await ScanRejectedAsync(kiosk, "E2E-POGRESAN-1");
				var resolved = await kiosk.ScanAsync(package.Barkod, WorkflowState.CourierDepositPrompt);
				Assert.Equal("PickupBarcodeResolved", resolved.Trigger);
				await kiosk.Ui.WaitForPageAsync("CourierDepositPromptPage");
				await CancelDepositPromptAsync(kiosk);

				var confirm = Assert.Single(await kiosk.Server.GetBusinessCallsAsync(serverMark), c => c.Endpoint == ConfirmEndpoint);
				Assert.Equal((200, package.Barkod), (confirm.StatusCode, confirm.RequestValue("Barkod")));
				Assert.DoesNotContain(await kiosk.Server.GetPackagesAsync(), p => p.Barkod == package.Barkod);
			});

		/// <summary>
		/// C09: vrata isteklog paketa se ne otvore (blokirana) → jedan pokušaj, poruka → prijava greške „N" → ekran za
		/// ostavljanje (nema više isteklih); potvrde nema, server i dalje vodi paket.
		/// </summary>
		[SkippableFact]
		public Task DoorDoesNotOpen_ReportsDoorError_ToDepositPrompt()
			=> KioskSession.RunAsync(nameof(DoorDoesNotOpen_ReportsDoorError_ToDepositPrompt), output, async kiosk => {
				var package = kiosk.Data.ExpiredPackage();
				await kiosk.Server.AddPackageAsync(package);
				var serverMark = await kiosk.Server.MarkAsync();
				var unlockMark = await kiosk.Locker.MarkUnlocksAsync();
				await kiosk.Locker.BlockDoorAsync(package.BrojPozicije);
				kiosk.Trace($"vrata {package.BrojPozicije} blokirana");

				await CourierTests.LoginAsync(kiosk, expect: WorkflowState.CourierPickupPrompt);
				await kiosk.Ui.WaitForPageAsync("CourierPickupPromptPage");
				var mark = kiosk.Log.Mark();
				await kiosk.Ui.TapAsync("CourierPickupPromptPage.PickUp");
				var next = await kiosk.WaitForStateAsync(WorkflowState.CourierDepositPrompt, mark, TimeSpan.FromSeconds(30));
				Assert.Equal((WorkflowState.CourierPackagePickup, "PickupDoorActionFailed"), (next.From, next.Trigger));
				await kiosk.Ui.WaitForPageAsync("CourierDepositPromptPage");

				var unlock = Assert.Single(await kiosk.Locker.GetUnlocksAsync(unlockMark));
				Assert.Equal((package.BrojPozicije, false), (unlock.Door, unlock.Unlocked));
				var calls = await RecipientFailureTests.WaitForDoorErrorAsync(kiosk, serverMark, package.BrojPozicije, expectedError: "N");
				Assert.DoesNotContain(calls, c => c.Endpoint == ConfirmEndpoint);
				await CancelDepositPromptAsync(kiosk);
				AssertStillOnServer(await kiosk.Server.GetPackagesAsync(), package);
			});

		/// <summary>
		/// C09: kurir ne zatvori vrata → posle OperationTimeout-a poruka „zatvorite vrata" → posle još jednog roka prijava
		/// greške „O" → Start; potvrde nema (barkod nije skeniran), server i dalje vodi paket.
		/// </summary>
		[SkippableFact]
		public Task DoorLeftOpen_ReminderThenTimeout_ReportsOpenDoor_Start()
			=> KioskSession.RunAsync(nameof(DoorLeftOpen_ReminderThenTimeout_ReportsOpenDoor_Start), output, async kiosk => {
				var package = kiosk.Data.ExpiredPackage();
				await kiosk.Server.AddPackageAsync(package);
				var serverMark = await kiosk.Server.MarkAsync();

				await CourierTests.LoginAsync(kiosk, expect: WorkflowState.CourierPickupPrompt);
				await kiosk.Ui.WaitForPageAsync("CourierPickupPromptPage");
				var mark = kiosk.Log.Mark();
				await kiosk.Ui.TapAsync("CourierPickupPromptPage.PickUp");
				await kiosk.WaitForStateAsync(WorkflowState.CourierPackagePickup, mark);
				await kiosk.WaitForDoorOpenAsync(package.BrojPozicije, "CourierPackagePickupPage");
				kiosk.Trace($"vrata {package.BrojPozicije} ostaju otvorena");

				await kiosk.WaitForLogLineAsync("AskToCloseTheDoor: asking to close the door", mark, __operationTimeout + TimeSpan.FromSeconds(15));
				Assert.DoesNotContain(kiosk.Log.TransitionsAfter(mark), t => t.To != WorkflowState.CourierPackagePickup);
				var toStart = await kiosk.WaitForStateAsync(WorkflowState.Start, mark, __operationTimeout + TimeSpan.FromSeconds(15));
				Assert.Equal((WorkflowState.CourierPackagePickup, "Reset"), (toStart.From, toStart.Trigger));
				await kiosk.Ui.WaitForPageAsync("StartPage");

				var calls = await RecipientFailureTests.WaitForDoorErrorAsync(kiosk, serverMark, package.BrojPozicije, expectedError: "O");
				Assert.DoesNotContain(calls, c => c.Endpoint == ConfirmEndpoint);
				AssertStillOnServer(await kiosk.Server.GetPackagesAsync(), package);
			});

		/// <summary>
		/// C09: kurir zatvori vrata tek posle podsetnika „zatvorite vrata" (posle OperationTimeout-a) → skeniranje preuzetog
		/// paketa kao i kad zatvori na vreme → potvrda pražnjenja. Ranije je zatvaranje posle podsetnika vodilo na Start bez
		/// skeniranja: paket izvađen, a server ga je i dalje vodio u vratima.
		/// </summary>
		[SkippableFact]
		public Task DoorClosedAfterReminder_StillScanned_Confirms()
			=> KioskSession.RunAsync(nameof(DoorClosedAfterReminder_StillScanned_Confirms), output, async kiosk => {
				var package = kiosk.Data.ExpiredPackage();
				await kiosk.Server.AddPackageAsync(package);
				var serverMark = await kiosk.Server.MarkAsync();

				await CourierTests.LoginAsync(kiosk, expect: WorkflowState.CourierPickupPrompt);
				await kiosk.Ui.WaitForPageAsync("CourierPickupPromptPage");
				var mark = kiosk.Log.Mark();
				await kiosk.Ui.TapAsync("CourierPickupPromptPage.PickUp");
				await kiosk.WaitForStateAsync(WorkflowState.CourierPackagePickup, mark);
				await kiosk.WaitForDoorOpenAsync(package.BrojPozicije, "CourierPackagePickupPage");
				kiosk.Trace($"vrata {package.BrojPozicije} ostaju otvorena do podsetnika");
				await kiosk.WaitForLogLineAsync("AskToCloseTheDoor: asking to close the door", mark, __operationTimeout + TimeSpan.FromSeconds(15));

				var toScan = await kiosk.CloseDoorAsync(package.BrojPozicije, WorkflowState.CourierPackagePickupBarcodeScan);
				Assert.Equal((WorkflowState.CourierPackagePickup, "PickupDoorClosedAfterOpen"), (toScan.From, toScan.Trigger));
				Assert.DoesNotContain(kiosk.Log.TransitionsAfter(mark), t => t.To == WorkflowState.Start);
				await kiosk.Ui.WaitForPageAsync("CourierBarcodeScanPage");
				await kiosk.WaitForScannerReadyAsync(toScan);

				var resolved = await kiosk.ScanAsync(package.Barkod, WorkflowState.CourierDepositPrompt);
				Assert.Equal("PickupBarcodeResolved", resolved.Trigger);
				await CancelDepositPromptAsync(kiosk);

				var confirm = Assert.Single(await kiosk.Server.GetBusinessCallsAsync(serverMark), c => c.Endpoint == ConfirmEndpoint);
				Assert.Equal((200, package.Barkod), (confirm.StatusCode, confirm.RequestValue("Barkod")));
				Assert.DoesNotContain(await kiosk.Server.GetPackagesAsync(), p => p.Barkod == package.Barkod);
			});

		/// <summary>
		/// C20: server odbije potvrdu pražnjenja (400, jednom) → poruka, skeniranje se nastavlja → isti barkod ponovo →
		/// potvrda prođe → ekran za ostavljanje.
		/// </summary>
		[SkippableFact]
		public Task ConfirmationRejectedOnce_RescanConfirms()
			=> KioskSession.RunAsync(nameof(ConfirmationRejectedOnce_RescanConfirms), output, async kiosk => {
				var (package, serverMark) = await PickUpUntilScanAsync(kiosk);
				await kiosk.Server.ArmFaultAsync(ConfirmEndpoint, error: "E2E: pražnjenje odbijeno");

				await ScanRejectedAsync(kiosk, package.Barkod);
				var resolved = await kiosk.ScanAsync(package.Barkod, WorkflowState.CourierDepositPrompt);
				Assert.Equal("PickupBarcodeResolved", resolved.Trigger);
				await kiosk.Ui.WaitForPageAsync("CourierDepositPromptPage");
				await CancelDepositPromptAsync(kiosk);

				var confirmations = (await kiosk.Server.GetBusinessCallsAsync(serverMark)).Where(c => c.Endpoint == ConfirmEndpoint).ToList();
				Assert.Equal([400, 200], confirmations.Select(c => c.StatusCode).ToList());
				Assert.DoesNotContain(await kiosk.Server.GetPackagesAsync(), p => p.Barkod == package.Barkod);
			});

		/// <summary>
		/// C20: server ne prima potvrdu pražnjenja (503 dok se ne obriše) → poruka posle svakog skeniranja → kurir odustane i
		/// ne skenira više → istek skeniranja (rok se posle neuspelog skeniranja produžava za ceo rok) → Start. Paket je
		/// izvađen, potvrda ne ide u red, pa server i dalje vodi paket u vratima. Dugačak test (~2 min).
		/// </summary>
		[SkippableFact]
		public Task ServerDown_ConfirmationNeverSent_ScanTimesOut_Start()
			=> KioskSession.RunAsync(nameof(ServerDown_ConfirmationNeverSent_ScanTimesOut_Start), output, async kiosk => {
				var (package, serverMark) = await PickUpUntilScanAsync(kiosk);
				await kiosk.Server.ArmFaultAsync(ConfirmEndpoint, status: 503, count: 0);
				kiosk.Trace($"server ne odgovara na {ConfirmEndpoint} (503)");

				var mark = kiosk.Log.Mark();
				await ScanRejectedAsync(kiosk, package.Barkod);
				var toStart = await kiosk.WaitForStateAsync(WorkflowState.Start, mark, 2 * __scanTimeout + TimeSpan.FromSeconds(15));
				Assert.Equal((WorkflowState.CourierPackagePickupBarcodeScan, "PickupBarcodeScanTimeout"), (toStart.From, toStart.Trigger));
				await kiosk.Ui.WaitForPageAsync("StartPage");
				await kiosk.Server.ClearFaultsAsync();

				await Task.Delay(TimeSpan.FromSeconds(3));	// namerno: dokaz da se potvrda ne šalje naknadno (nema reda)
				var confirmations = (await kiosk.Server.GetBusinessCallsAsync(serverMark)).Where(c => c.Endpoint == ConfirmEndpoint).ToList();
				Assert.NotEmpty(confirmations);
				Assert.All(confirmations, c => Assert.Equal(503, c.StatusCode));
				AssertStillOnServer(await kiosk.Server.GetPackagesAsync(), package);
			});

		/// <summary>
		/// Jedan istekao paket: prijava → „Preuzmi" → vrata se otvaraju → zatvaranje → skeniranje preuzetog paketa (skener
		/// spreman). Vraća paket i kursor servera uzet pre prijave.
		/// </summary>
		internal static async Task<(SimPackage Package, long ServerMark)> PickUpUntilScanAsync(KioskSession kiosk)
		{
			var package = kiosk.Data.ExpiredPackage();
			await kiosk.Server.AddPackageAsync(package);
			var serverMark = await kiosk.Server.MarkAsync();

			await CourierTests.LoginAsync(kiosk, expect: WorkflowState.CourierPickupPrompt);
			await kiosk.Ui.WaitForPageAsync("CourierPickupPromptPage");
			var mark = kiosk.Log.Mark();
			await kiosk.Ui.TapAsync("CourierPickupPromptPage.PickUp");
			await kiosk.WaitForStateAsync(WorkflowState.CourierPackagePickup, mark);
			await kiosk.WaitForDoorOpenAsync(package.BrojPozicije, "CourierPackagePickupPage");
			var toScan = await kiosk.CloseDoorAsync(package.BrojPozicije, WorkflowState.CourierPackagePickupBarcodeScan);
			await kiosk.Ui.WaitForPageAsync("CourierBarcodeScanPage");
			await kiosk.WaitForScannerReadyAsync(toScan);
			return (package, serverMark);
		}

		/// <summary>
		/// Skeniranje koje kiosk ne prihvati (pogrešan barkod ili neuspela potvrda) — čeka da skener ponovo prima barkod: tek
		/// posle poruke (CourierMessageDisplayTimeInSeconds) handler vraća „nije prihvaćen".
		/// </summary>
		private static async Task ScanRejectedAsync(KioskSession kiosk, string barcode)
		{
			var mark = kiosk.Log.Mark();
			await kiosk.Locker.ScanBarcodeAsync(barcode);
			kiosk.Trace($"skeniran barkod {barcode} (kiosk ga ne prihvata)");
			await kiosk.WaitForLogLineAsync($"BarcodeScanTracker: barkod '{barcode}' nije prihvaćen", mark, TimeSpan.FromSeconds(30));
			Assert.Empty(kiosk.Log.TransitionsAfter(mark));
		}

		/// <summary>Ekran za ostavljanje → „Odustani" → Start.</summary>
		internal static async Task CancelDepositPromptAsync(KioskSession kiosk)
		{
			var mark = kiosk.Log.Mark();
			await kiosk.Ui.TapAsync("CourierDepositPromptPage.Cancel");
			await kiosk.WaitForStateAsync(WorkflowState.Start, mark);
		}

		/// <summary>Server i dalje vodi istekao paket u istim vratima (pražnjenje nije potvrđeno).</summary>
		internal static void AssertStillOnServer(List<SimPackage> packages, SimPackage package)
		{
			var onServer = Assert.Single(packages, p => p.Barkod == package.Barkod);
			Assert.Equal((package.BrojPozicije, true), (onServer.BrojPozicije, onServer.Istekao));
		}
	}
}
