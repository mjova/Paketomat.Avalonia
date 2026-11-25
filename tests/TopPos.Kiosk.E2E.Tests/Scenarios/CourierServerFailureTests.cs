using TopPos.Kiosk.Workflow;
using TopPos.E2E.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace TopPos.E2E.Tests.Scenarios
{
	/// <summary>
	/// Kurir kad server ne odgovori kako treba, i ostale grane kurirskog toka (storyboard C01, C03, C04, C05, C12, C21, S03):
	/// prijava i lista isteklih paketa koje ne uspeju, dostupnost sanduka koja se ne dobije, pitanje o preuzimanju isteklih,
	/// odbijena promena veličine, više paketa u jednoj prijavi. E2E: RequestTimeout 5 s (kašnjenje servera 8 s = istek),
	/// OperationTimeout 20 s, poruke greške 2 s.
	/// </summary>
	[Collection(E2ECollection.Name)]
	public sealed class CourierServerFailureTests(ITestOutputHelper output)
	{
		private const string CourierCode = "K001";
		private const int LateResponseMs = 8000;	// > RequestTimeout (5 s)
		private static readonly TimeSpan __operationTimeout = TimeSpan.FromMilliseconds(20000);

		private static readonly KioskOptions __confirmPickup = new() {
			ConfigureClient = c => c["General"]!["ConfirmPackagePickup"] = true,
		};
		private static readonly KioskOptions __boxAvailabilityPage = new() {
			ConfigureClient = c => c["General"]!["EnableLockerBoxAvailabilityPage"] = true,
		};

		/// <summary>C21: server odbije listu isteklih paketa (400) → poruka servera → Start; kurir ne dobija nijedan ekran.</summary>
		[SkippableFact]
		public Task ExpiredListRejected_MessageThenStart()
			=> KioskSession.RunAsync(nameof(ExpiredListRejected_MessageThenStart), output, async kiosk => {
				await kiosk.Server.ArmFaultAsync("KurirPozicijeZaPraznjenje", error: "E2E: lista nije dostupna");
				var mark = await ScanCourierQrAsync(kiosk);
				var toStart = await kiosk.WaitForStateAsync(WorkflowState.Start, mark, TimeSpan.FromSeconds(20));
				Assert.Equal((WorkflowState.CourierFetchingExpiredPackages, "Reset"), (toStart.From, toStart.Trigger));
				Assert.NotEmpty(kiosk.Log.LinesAfter(mark, "GetExpiredPackagesForPickup: [BadRequest] E2E: lista nije dostupna"));
				AssertNoCourierScreen(kiosk, mark);
				await kiosk.Ui.WaitForPageAsync("StartPage");
			});

		/// <summary>C21: lista isteklih paketa ne stigne na vreme (istek) → podrazumevana poruka → Start.</summary>
		[SkippableFact]
		public Task ExpiredListTimesOut_MessageThenStart()
			=> KioskSession.RunAsync(nameof(ExpiredListTimesOut_MessageThenStart), output, async kiosk => {
				await kiosk.Server.ArmFaultAsync("KurirPozicijeZaPraznjenje", delayMs: LateResponseMs);
				var mark = await ScanCourierQrAsync(kiosk);
				var toStart = await kiosk.WaitForStateAsync(WorkflowState.Start, mark, TimeSpan.FromSeconds(30));
				Assert.Equal((WorkflowState.CourierFetchingExpiredPackages, "Reset"), (toStart.From, toStart.Trigger));
				Assert.NotEmpty(kiosk.Log.LinesAfter(mark, "GetExpiredPackagesForPickup exception"));
				AssertNoCourierScreen(kiosk, mark);
				await kiosk.Ui.WaitForPageAsync("StartPage");
			});

		/// <summary>C03: prijava kurira ne stigne na vreme (istek) → poruka → CourierLoginFailed → Start; liste isteklih nema.</summary>
		[SkippableFact]
		public Task LoginTimesOut_FailedThenStart()
			=> KioskSession.RunAsync(nameof(LoginTimesOut_FailedThenStart), output, async kiosk => {
				var serverMark = await kiosk.Server.MarkAsync();
				await kiosk.Server.ArmFaultAsync("KurirAutorizacija", delayMs: LateResponseMs);
				var mark = kiosk.Log.Mark();
				await kiosk.ScanAsync(kiosk.Data.CourierQrCode, WorkflowState.CourierAuthenticating);
				var toStart = await kiosk.WaitForStateAsync(WorkflowState.Start, mark, TimeSpan.FromSeconds(30));
				Assert.Equal((WorkflowState.CourierAuthenticating, "CourierLoginFailed"), (toStart.From, toStart.Trigger));
				Assert.NotEmpty(kiosk.Log.LinesAfter(mark, "CourierLogin exception"));
				await kiosk.Ui.WaitForPageAsync("StartPage");
				Assert.DoesNotContain(await kiosk.Server.GetBusinessCallsAsync(serverMark), c => c.Endpoint == "KurirPozicijeZaPraznjenje");
			});

		/// <summary>
		/// C03: Home dok server proverava kurira (odgovor kasni 3 s) → kiosk čeka odgovor, pa Reset → Start; zakasnela uspešna
		/// prijava je ignorisana i lista isteklih se ne traži.
		/// </summary>
		[RealMouseFact]	// v4
		public Task HomeWhileAuthenticating_LateSuccessIgnored()
			=> KioskSession.RunAsync(nameof(HomeWhileAuthenticating_LateSuccessIgnored), output, async kiosk => {
				var serverMark = await kiosk.Server.MarkAsync();
				var homeButton = await kiosk.Ui.GetClickablePointAsync("Header.Home");	// unapred: posle skeniranja klik bez UIA poziva
				var serverDelay = TimeSpan.FromSeconds(3);
				await kiosk.Server.ArmFaultAsync("KurirAutorizacija", delayMs: (int)serverDelay.TotalMilliseconds);
				var scanned = await kiosk.ScanAsync(kiosk.Data.CourierQrCode, WorkflowState.CourierAuthenticating);

				var homeMark = kiosk.Log.Mark();
				await kiosk.Ui.ClickAtAsync(homeButton, "Header.Home");
				var toStart = await kiosk.WaitForStateAsync(WorkflowState.Start, homeMark, TimeSpan.FromSeconds(10));
				Assert.Equal((WorkflowState.CourierAuthenticating, "Reset"), (toStart.From, toStart.Trigger));
				kiosk.AssertResetWaitedForServer(scanned, homeMark, toStart, serverDelay);
				await kiosk.Ui.WaitForPageAsync("StartPage");
				await kiosk.AssertIgnoredTriggersAsync(homeMark, ("CourierLoginSuccessful", WorkflowState.Start));

				var calls = await kiosk.Server.GetBusinessCallsAsync(serverMark);
				Assert.Equal(200, Assert.Single(calls, c => c.Endpoint == "KurirAutorizacija").StatusCode);
				Assert.DoesNotContain(calls, c => c.Endpoint == "KurirPozicijeZaPraznjenje");
			});

		/// <summary>
		/// C04: dostupnost sanduka se ne dobije (400) → poruka na ekranu, kurir ostaje na ekranu dostupnosti → „Dalje" →
		/// lista isteklih → ekran za ostavljanje.
		/// </summary>
		[SkippableFact]
		public Task BoxAvailabilityFetchFails_CompleteStillContinues()
			=> KioskSession.RunAsync(nameof(BoxAvailabilityFetchFails_CompleteStillContinues), output, __boxAvailabilityPage, async kiosk => {
				await kiosk.Server.ArmFaultAsync("StatusPaketomata", error: "E2E: dostupnost nije dostupna");
				var mark = kiosk.Log.Mark();
				await CourierTests.LoginAsync(kiosk, expect: WorkflowState.CourierLockerBoxAvailability);
				await kiosk.Ui.WaitForPageAsync("CourierLockerBoxAvailabilityPage");
				await kiosk.WaitForLogLineAsync("odgovor na zahtev za dobijanje dostupnosti kutija nije dobijen", mark);
				Assert.DoesNotContain(kiosk.Log.TransitionsAfter(mark), t => t.From == WorkflowState.CourierLockerBoxAvailability);

				var completeMark = kiosk.Log.Mark();
				await kiosk.Ui.TapAsync("CourierLockerBoxAvailabilityPage.Complete");
				var prompt = await kiosk.WaitForStateAsync(WorkflowState.CourierDepositPrompt, completeMark);
				Assert.Equal("NoExpiredPackages", prompt.Trigger);
				Assert.Contains(kiosk.Log.TransitionsAfter(completeMark), t => t.Trigger == "BoxAvailabilityCompleted");
				await kiosk.Ui.WaitForPageAsync("CourierDepositPromptPage");
				await ExpiredPickupTests.CancelDepositPromptAsync(kiosk);
			});

		/// <summary>C05: pitanje o preuzimanju isteklih (ConfirmPackagePickup) → „Da" → lista isteklih → ekran za preuzimanje.</summary>
		[SkippableFact]
		public Task PickupConfirmation_Yes_FetchesExpiredPackages()
			=> KioskSession.RunAsync(nameof(PickupConfirmation_Yes_FetchesExpiredPackages), output, __confirmPickup, async kiosk => {
				var package = kiosk.Data.ExpiredPackage();
				await kiosk.Server.AddPackageAsync(package);
				var serverMark = await kiosk.Server.MarkAsync();
				await CourierTests.LoginAsync(kiosk, expect: WorkflowState.CourierPickupConfirmation);
				await kiosk.Ui.WaitForPageAsync("CourierPickupConfirmationPage");
				Assert.DoesNotContain(await kiosk.Server.GetBusinessCallsAsync(serverMark), c => c.Endpoint == "KurirPozicijeZaPraznjenje");

				var mark = kiosk.Log.Mark();
				await kiosk.Ui.TapAsync("CourierPickupConfirmationPage.Confirm");
				var prompt = await kiosk.WaitForStateAsync(WorkflowState.CourierPickupPrompt, mark);
				Assert.Equal("HasExpiredPackages", prompt.Trigger);
				Assert.Contains(kiosk.Log.TransitionsAfter(mark), t => t.Trigger == "PickupConfirmed");
				await kiosk.Ui.WaitForPageAsync("CourierPickupPromptPage");
				Assert.Single(await kiosk.Server.GetBusinessCallsAsync(serverMark), c => c.Endpoint == "KurirPozicijeZaPraznjenje");

				var cancelMark = kiosk.Log.Mark();
				await kiosk.Ui.TapAsync("CourierPickupPromptPage.Cancel");
				await kiosk.WaitForStateAsync(WorkflowState.Start, cancelMark);
				ExpiredPickupTests.AssertStillOnServer(await kiosk.Server.GetPackagesAsync(), package);
			});

		/// <summary>C05: „Ne" → ekran za ostavljanje bez traženja liste isteklih; istekao paket ostaje u vratima.</summary>
		[SkippableFact]
		public Task PickupConfirmation_No_GoesToDepositWithoutFetching()
			=> KioskSession.RunAsync(nameof(PickupConfirmation_No_GoesToDepositWithoutFetching), output, __confirmPickup, async kiosk => {
				var package = kiosk.Data.ExpiredPackage();
				await kiosk.Server.AddPackageAsync(package);
				var serverMark = await kiosk.Server.MarkAsync();
				var unlockMark = await kiosk.Locker.MarkUnlocksAsync();
				await CourierTests.LoginAsync(kiosk, expect: WorkflowState.CourierPickupConfirmation);
				await kiosk.Ui.WaitForPageAsync("CourierPickupConfirmationPage");

				var mark = kiosk.Log.Mark();
				await kiosk.Ui.TapAsync("CourierPickupConfirmationPage.Cancel");
				var prompt = await kiosk.WaitForStateAsync(WorkflowState.CourierDepositPrompt, mark);
				Assert.Equal((WorkflowState.CourierPickupConfirmation, "PickupConfirmationCancelled"), (prompt.From, prompt.Trigger));
				await kiosk.Ui.WaitForPageAsync("CourierDepositPromptPage");
				await ExpiredPickupTests.CancelDepositPromptAsync(kiosk);

				Assert.DoesNotContain(await kiosk.Server.GetBusinessCallsAsync(serverMark), c => c.Endpoint == "KurirPozicijeZaPraznjenje");
				Assert.Empty(await kiosk.Locker.GetUnlocksAsync(unlockMark));
				ExpiredPickupTests.AssertStillOnServer(await kiosk.Server.GetPackagesAsync(), package);
			});

		/// <summary>C05: na pitanju o preuzimanju isteklih kurir ne izabere ništa → posle OperationTimeout-a Reset → Start.</summary>
		[SkippableFact]
		public Task PickupConfirmation_NoActivity_TimesOutToStart()
			=> KioskSession.RunAsync(nameof(PickupConfirmation_NoActivity_TimesOutToStart), output, __confirmPickup, async kiosk => {
				var mark = kiosk.Log.Mark();
				await CourierTests.LoginAsync(kiosk, expect: WorkflowState.CourierPickupConfirmation);
				var entered = kiosk.Log.TransitionsAfter(mark).Last(t => t.To == WorkflowState.CourierPickupConfirmation);
				await kiosk.Ui.WaitForPageAsync("CourierPickupConfirmationPage");

				var toStart = await kiosk.WaitForStateAsync(WorkflowState.Start, entered.Line, __operationTimeout + TimeSpan.FromSeconds(10));
				Assert.Equal((WorkflowState.CourierPickupConfirmation, "Reset"), (toStart.From, toStart.Trigger));
				var elapsed = kiosk.Log.TimeOf(toStart.Line) - kiosk.Log.TimeOf(entered.Line);
				Assert.InRange(elapsed, __operationTimeout - TimeSpan.FromSeconds(1), __operationTimeout + TimeSpan.FromSeconds(2));
				await kiosk.Ui.WaitForPageAsync("StartPage");
			});

		/// <summary>
		/// C12: server odbije promenu veličine (400) → poruka → nazad na skeniranje → isti barkod dobija ista vrata (veličina
		/// nije promenjena) → zatvaranje → potvrda za ta vrata.
		/// </summary>
		[SkippableFact]
		public Task Courier_ChangeBoxRejected_RescanGetsSameDoor()
			=> KioskSession.RunAsync(nameof(Courier_ChangeBoxRejected_RescanGetsSameDoor), output, async kiosk => {
				var package = kiosk.Data.DepositPackage(size: "S");
				await kiosk.Server.AddPackageAsync(package);
				var serverMark = await kiosk.Server.MarkAsync();
				var unlockMark = await kiosk.Locker.MarkUnlocksAsync();

				await CourierTests.OpenDepositScanAsync(kiosk);
				await kiosk.ScanAsync(package.Barkod, WorkflowState.CourierPackageDeposit);
				var first = (await kiosk.WaitForUnlockAsync(unlockMark)).Door;
				await kiosk.WaitForDoorOpenAsync(first, "CourierPackageDepositPage");
				await kiosk.Server.ArmFaultAsync("KurirPromenaVelicinePosiljke", error: "E2E: veće sanduče nije dostupno");

				var mark = kiosk.Log.Mark();
				await kiosk.Ui.TapAsync("CourierPackageDepositPage.ChangeBoxSize");
				await kiosk.WaitForLogLineAsync("CourierPackageDepositViewModel: executing ChangeBoxSizeCommand", mark);
				var back = await kiosk.CloseDoorAsync(first, WorkflowState.CourierPackageDepositBarcodeScan);
				Assert.Equal("DepositChangeBoxRequested", back.Trigger);
				Assert.NotEmpty(kiosk.Log.LinesAfter(mark, "CourierUpdatePackageSize: [BadRequest] E2E: veće sanduče nije dostupno"));
				await kiosk.Ui.WaitForPageAsync("CourierBarcodeScanPage");
				await kiosk.WaitForScannerReadyAsync(back);

				var secondUnlockMark = await kiosk.Locker.MarkUnlocksAsync();
				await kiosk.ScanAsync(package.Barkod, WorkflowState.CourierPackageDeposit);
				Assert.Equal(first, (await kiosk.WaitForUnlockAsync(secondUnlockMark)).Door);
				await kiosk.WaitForDoorOpenAsync(first, "CourierPackageDepositPage");
				await kiosk.CloseDoorAsync(first, WorkflowState.CourierDepositConfirmation);
				await kiosk.Ui.WaitForPageAsync("CourierDepositConfirmationPage");
				var confirmMark = kiosk.Log.Mark();
				await kiosk.Ui.TapAsync("CourierDepositConfirmationPage.Confirm");
				await kiosk.WaitForStateAsync(WorkflowState.CourierPackageDepositBarcodeScan, confirmMark);
				await CourierTests.LogoutFromScanAsync(kiosk);

				var calls = await kiosk.Server.GetBusinessCallsAsync(serverMark);
				Assert.Equal(400, Assert.Single(calls, c => c.Endpoint == "KurirPromenaVelicinePosiljke").StatusCode);
				Assert.Equal(first.ToString(), Assert.Single(calls, c => c.Endpoint == "KurirPunjenjePozicijePotvrda").RequestValue("BrojPozicije"));
				var stored = Assert.Single(await kiosk.Server.GetPackagesAsync(), p => p.Barkod == package.Barkod);
				Assert.Equal((first, "S"), (stored.BrojPozicije, stored.Velicina));
			});

		/// <summary>S03: isto za pošiljaoca — odbijena promena veličine → ista vrata → zatvaranje → potvrda → Start.</summary>
		[PendingFlow("pošiljalac", "F1")]	// v4
		public Task Sender_ChangeBoxRejected_RescanGetsSameDoor()
			=> KioskSession.RunAsync(nameof(Sender_ChangeBoxRejected_RescanGetsSameDoor), output, async kiosk => {
				var package = kiosk.Data.DepositPackage(size: "S");
				await kiosk.Server.AddPackageAsync(package);
				var serverMark = await kiosk.Server.MarkAsync();
				var unlockMark = await kiosk.Locker.MarkUnlocksAsync();

				var openMark = kiosk.Log.Mark();
				await kiosk.Ui.TapAsync("StartPage.SenderDepositPackage");
				await kiosk.WaitForStateAsync(WorkflowState.SenderDepositBarcodeScan, openMark);
				await kiosk.Ui.WaitForPageAsync("SenderBarcodeScanPage");
				await kiosk.ScanAsync(package.Barkod, WorkflowState.SenderPackageDeposit);
				var first = (await kiosk.WaitForUnlockAsync(unlockMark)).Door;
				await kiosk.WaitForDoorOpenAsync(first, "SenderPackageDepositPage");
				await kiosk.Server.ArmFaultAsync("KurirPromenaVelicinePosiljke", error: "E2E: veće sanduče nije dostupno");

				var mark = kiosk.Log.Mark();
				await kiosk.Ui.TapAsync("SenderPackageDepositPage.ChangeBoxSize");
				await kiosk.WaitForLogLineAsync("SenderPackageDepositViewModel: executing ChangeBoxSizeCommand", mark);
				var back = await kiosk.CloseDoorAsync(first, WorkflowState.SenderDepositBarcodeScan);
				Assert.Equal("SenderChangeBoxRequested", back.Trigger);
				Assert.NotEmpty(kiosk.Log.LinesAfter(mark, "CourierUpdatePackageSize: [BadRequest] E2E: veće sanduče nije dostupno"));
				await kiosk.Ui.WaitForPageAsync("SenderBarcodeScanPage");
				await kiosk.WaitForScannerReadyAsync(back);

				var secondUnlockMark = await kiosk.Locker.MarkUnlocksAsync();
				await kiosk.ScanAsync(package.Barkod, WorkflowState.SenderPackageDeposit);
				Assert.Equal(first, (await kiosk.WaitForUnlockAsync(secondUnlockMark)).Door);
				await kiosk.WaitForDoorOpenAsync(first, "SenderPackageDepositPage");
				var toStart = await kiosk.CloseDoorAsync(first, WorkflowState.Start);
				Assert.Equal("SenderDoorClosedConfirm", toStart.Trigger);

				var calls = await kiosk.Server.GetBusinessCallsAsync(serverMark);
				Assert.Equal(400, Assert.Single(calls, c => c.Endpoint == "KurirPromenaVelicinePosiljke").StatusCode);
				Assert.Equal(first.ToString(), Assert.Single(calls, c => c.Endpoint == "KurirPunjenjePozicijePotvrda").RequestValue("BrojPozicije"));
				Assert.Equal(first, Assert.Single(await kiosk.Server.GetPackagesAsync(), p => p.Barkod == package.Barkod).BrojPozicije);
			});

		/// <summary>C01: dva paketa u jednoj prijavi — svaki dobija svoja vrata i svoju potvrdu, pa odjava.</summary>
		[SkippableFact]
		public Task Deposit_TwoPackagesInOneLogin_EachConfirmedInOwnDoor()
			=> KioskSession.RunAsync(nameof(Deposit_TwoPackagesInOneLogin_EachConfirmedInOwnDoor), output, async kiosk => {
				var packages = new[] { kiosk.Data.DepositPackage(size: "M"), kiosk.Data.DepositPackage(size: "M") };
				foreach (var package in packages) {
					await kiosk.Server.AddPackageAsync(package);
				}
				var serverMark = await kiosk.Server.MarkAsync();

				await CourierTests.OpenDepositScanAsync(kiosk);
				var doors = new List<int>();
				foreach (var package in packages) {
					var unlockMark = await kiosk.Locker.MarkUnlocksAsync();
					await kiosk.ScanAsync(package.Barkod, WorkflowState.CourierPackageDeposit);
					var door = (await kiosk.WaitForUnlockAsync(unlockMark)).Door;
					await kiosk.WaitForDoorOpenAsync(door, "CourierPackageDepositPage");
					await kiosk.CloseDoorAsync(door, WorkflowState.CourierDepositConfirmation);
					await kiosk.Ui.WaitForPageAsync("CourierDepositConfirmationPage");
					var confirmMark = kiosk.Log.Mark();
					await kiosk.Ui.TapAsync("CourierDepositConfirmationPage.Confirm");
					var back = await kiosk.WaitForStateAsync(WorkflowState.CourierPackageDepositBarcodeScan, confirmMark);
					Assert.Equal("DepositConfirmationConfirmed", back.Trigger);
					await kiosk.Ui.WaitForPageAsync("CourierBarcodeScanPage");
					await kiosk.WaitForScannerReadyAsync(back);
					doors.Add(door);
				}
				Assert.NotEqual(doors[0], doors[1]);
				await CourierTests.LogoutFromScanAsync(kiosk);

				var calls = await kiosk.Server.GetBusinessCallsAsync(serverMark);
				Assert.Single(calls, c => c.Endpoint == "KurirAutorizacija");
				var confirmations = calls.Where(c => c.Endpoint == "KurirPunjenjePozicijePotvrda").ToList();
				Assert.Equal(packages.Select(p => p.Barkod), confirmations.Select(c => c.RequestValue("Barkod")));
				Assert.All(confirmations, c => Assert.Equal(CourierCode, c.RequestValue("SifraKurira")));
				var stored = await kiosk.Server.GetPackagesAsync();
				Assert.Equal(doors, packages.Select(p => Assert.Single(stored, s => s.Barkod == p.Barkod).BrojPozicije));
			});

		/// <summary>Skeniranje QR-a kurira na Start ekranu. Vraća kursor uzet pre skeniranja.</summary>
		private static async Task<int> ScanCourierQrAsync(KioskSession kiosk)
		{
			var mark = kiosk.Log.Mark();
			await kiosk.Locker.ScanBarcodeAsync(kiosk.Data.CourierQrCode);
			kiosk.Trace($"skeniran QR kurira {kiosk.Data.CourierQrCode}");
			await kiosk.WaitForStateAsync(WorkflowState.CourierFetchingExpiredPackages, mark, TimeSpan.FromSeconds(20));
			return mark;
		}

		/// <summary>Posle neuspele liste isteklih kurir nije dobio nijedan ekran (ni preuzimanje, ni ostavljanje).</summary>
		private static void AssertNoCourierScreen(KioskSession kiosk, int mark)
			=> Assert.DoesNotContain(kiosk.Log.TransitionsAfter(mark), t => t.To is WorkflowState.CourierPickupPrompt or WorkflowState.CourierDepositPrompt);
	}
}
