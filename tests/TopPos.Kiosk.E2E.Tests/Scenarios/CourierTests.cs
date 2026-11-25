using System.Diagnostics;
using TopPos.Kiosk.Workflow;
using TopPos.E2E.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace TopPos.E2E.Tests.Scenarios
{
	/// <summary>
	/// Kurir se prijavljuje QR kodom na Start ekranu (skener: LockerSimulator → COM → klijent), preuzima istekle
	/// pakete i ostavlja nove (doc/plan-e2e-ui-testing.md, F5 scenariji 6–7, F6 „nepoznat kurir").
	/// E2E konfiguracija: bez ručne prijave, bez stranice dostupnosti, ConfirmPackagePickup=false,
	/// ConfirmPackageDeposition=true. ServerSimulator prihvata samo QR iz CourierQrCodes i vraća šifru "K001".
	/// Provere ispravki iz plana „E2E — provere commit-ovanih ispravki": S2–S4 (ostavljanje), S6–S7 (stranica dostupnosti
	/// uključena po testu).
	/// </summary>
	[Collection(E2ECollection.Name)]
	public sealed class CourierTests(ITestOutputHelper output)
	{
		private const string CourierCode = "K001";
		private static readonly TimeSpan __operationTimeout = TimeSpan.FromMilliseconds(20000);	// General.OperationTimeout u E2E konfiguraciji

		private static readonly KioskOptions __boxAvailabilityPage = new() {
			ConfigureClient = c => c["General"]!["EnableLockerBoxAvailabilityPage"] = true,
		};

		/// <summary>
		/// Scenario 6: prijava (nema isteklih) → "Ostavi" → skeniranje paketa → server dodeljuje vrata → vrata se
		/// otvaraju → zatvaranje → potvrda → server pamti poziciju → odjava → Start.
		/// </summary>
		[SkippableFact]
		public Task Deposit_ScanOpenCloseConfirm_ServerStoresPosition()
			=> KioskSession.RunAsync(nameof(Deposit_ScanOpenCloseConfirm_ServerStoresPosition), output, DepositAsync);

		/// <summary>Telo testa Deposit_ScanOpenCloseConfirm_ServerStoresPosition — deli ga i <see cref="SerialControllerTests"/> (isti tok, drugi kontroler).</summary>
		internal static async Task DepositAsync(KioskSession kiosk)
		{
			var package = kiosk.Data.DepositPackage(size: "M");
			await kiosk.Server.AddPackageAsync(package);
			var serverMark = await kiosk.Server.MarkAsync();
			var unlockMark = await kiosk.Locker.MarkUnlocksAsync();

			await LoginAsync(kiosk, expect: WorkflowState.CourierDepositPrompt);
			await kiosk.Ui.WaitForPageAsync("CourierDepositPromptPage");

			var mark = kiosk.Log.Mark();
			await kiosk.Ui.TapAsync("CourierDepositPromptPage.Deposit");
			await kiosk.WaitForStateAsync(WorkflowState.CourierPackageDepositBarcodeScan, mark);
			await kiosk.Ui.WaitForPageAsync("CourierBarcodeScanPage");

			await kiosk.ScanAsync(package.Barkod, WorkflowState.CourierPackageDeposit);
			var door = (await kiosk.WaitForUnlockAsync(unlockMark)).Door;
			await kiosk.WaitForDoorOpenAsync(door, "CourierPackageDepositPage");

			var toConfirmation = await kiosk.CloseDoorAsync(door, WorkflowState.CourierDepositConfirmation);
			Assert.Equal("DepositDoorClosedExplicitConfirm", toConfirmation.Trigger);
			await kiosk.Ui.WaitForPageAsync("CourierDepositConfirmationPage");

			mark = kiosk.Log.Mark();
			await kiosk.Ui.TapAsync("CourierDepositConfirmationPage.Confirm");
			await kiosk.WaitForStateAsync(WorkflowState.CourierPackageDepositBarcodeScan, mark);
			await kiosk.Ui.WaitForPageAsync("CourierBarcodeScanPage");

			mark = kiosk.Log.Mark();
			await kiosk.Ui.TapAsync("CourierBarcodeScanPage.Cancel");	// odjava
			await kiosk.WaitForStateAsync(WorkflowState.Start, mark);
			await kiosk.Ui.WaitForPageAsync("StartPage");

			var calls = await kiosk.Server.GetBusinessCallsAsync(serverMark);
			AssertLogin(kiosk, calls);
			var request = Assert.Single(calls, c => c.Endpoint == "KurirPunjenjePozicije");
			Assert.Equal(package.Barkod, request.Query["Barkod"]);
			Assert.Equal(CourierCode, request.Query["SifraKurira"]);
			Assert.Equal(door.ToString(), request.ResponseValue("BrojPozicije"));
			var confirm = Assert.Single(calls, c => c.Endpoint == "KurirPunjenjePozicijePotvrda");
			Assert.Equal(package.Barkod, confirm.RequestValue("Barkod"));
			Assert.Equal(door.ToString(), confirm.RequestValue("BrojPozicije"));
			Assert.Equal(CourierCode, confirm.RequestValue("SifraKurira"));
			Assert.DoesNotContain(calls, c => c.IsUnexpectedError);

			var stored = Assert.Single(await kiosk.Server.GetPackagesAsync(), p => p.Barkod == package.Barkod);
			Assert.Equal(door, stored.BrojPozicije);
			await kiosk.AssertOnlyDoorUnlockedAsync(unlockMark, door);
		}

		/// <summary>
		/// Scenario 7: dva istekla paketa → za svaki: "Preuzmi" → vrata se otvaraju → zatvaranje → skeniranje barkoda (bez
		/// dugmeta Izloguj se) → potvrda pražnjenja; posle poslednjeg ekran za ostavljanje → odustajanje → Start. Server više nema pakete.
		/// </summary>
		[SkippableFact]
		public Task PickupExpired_TwoPackages_EachConfirmed_ThenDepositPrompt()
			=> KioskSession.RunAsync(nameof(PickupExpired_TwoPackages_EachConfirmed_ThenDepositPrompt), output, async kiosk => {
				var first = kiosk.Data.ExpiredPackage();
				var second = kiosk.Data.ExpiredPackage();
				await kiosk.Server.AddPackageAsync(first);
				await kiosk.Server.AddPackageAsync(second);
				var byDoor = new Dictionary<int, SimPackage> { [first.BrojPozicije] = first, [second.BrojPozicije] = second };
				var serverMark = await kiosk.Server.MarkAsync();
				var unlockMark = await kiosk.Locker.MarkUnlocksAsync();

				await LoginAsync(kiosk, expect: WorkflowState.CourierPickupPrompt);
				var picked = new List<int>();
				for (int i = 0; i < 2; i++) {
					await kiosk.Ui.WaitForPageAsync("CourierPickupPromptPage");
					var stepUnlockMark = await kiosk.Locker.MarkUnlocksAsync();
					var mark = kiosk.Log.Mark();
					await kiosk.Ui.TapAsync("CourierPickupPromptPage.PickUp");
					await kiosk.WaitForStateAsync(WorkflowState.CourierPackagePickup, mark);

					// Redosled paketa određuje server — vrata čitamo iz evidencije otključavanja.
					var door = (await kiosk.WaitForUnlockAsync(stepUnlockMark)).Door;
					Assert.True(byDoor.ContainsKey(door), $"Otključana su vrata {door}, a istekli paketi su u {string.Join(", ", byDoor.Keys)}");
					await kiosk.WaitForDoorOpenAsync(door, "CourierPackagePickupPage");
					await kiosk.CloseDoorAsync(door, WorkflowState.CourierPackagePickupBarcodeScan);
					await kiosk.Ui.WaitForPageAsync("CourierBarcodeScanPage");
					// Paket je izvađen, kurir mora da ga skenira — odjave nema (pri ostavljanju je ima, LogoutFromScanAsync).
					await kiosk.Ui.WaitForHiddenAsync("CourierBarcodeScanPage.Cancel", TimeSpan.FromSeconds(3));

					var next = i == 0 ? WorkflowState.CourierPickupPrompt : WorkflowState.CourierDepositPrompt;
					var resolved = await kiosk.ScanAsync(byDoor[door].Barkod, next);
					Assert.Equal("PickupBarcodeResolved", resolved.Trigger);
					picked.Add(door);
				}
				Assert.Equal(byDoor.Keys.Order(), picked.Order());

				await kiosk.Ui.WaitForPageAsync("CourierDepositPromptPage");
				var cancelMark = kiosk.Log.Mark();
				await kiosk.Ui.TapAsync("CourierDepositPromptPage.Cancel");
				await kiosk.WaitForStateAsync(WorkflowState.Start, cancelMark);

				var calls = await kiosk.Server.GetBusinessCallsAsync(serverMark);
				AssertLogin(kiosk, calls);
				Assert.Single(calls, c => c.Endpoint == "KurirPozicijeZaPraznjenje");
				var confirmations = calls.Where(c => c.Endpoint == "KurirPraznjenjePozicijePotvrda").ToList();
				Assert.Equal(2, confirmations.Count);
				foreach (var confirmation in confirmations) {
					var door = int.Parse(confirmation.RequestValue("BrojPozicije")!);
					Assert.Equal(byDoor[door].Barkod, confirmation.RequestValue("Barkod"));
					Assert.Equal(CourierCode, confirmation.RequestValue("SifraKurira"));
				}
				Assert.DoesNotContain(calls, c => c.IsUnexpectedError);
				Assert.Empty(await kiosk.Server.GetPackagesAsync());

				var unlocks = await kiosk.Locker.GetUnlocksAsync(unlockMark);
				Assert.Equal(byDoor.Keys.Order(), unlocks.Select(u => u.Door).Distinct().Order());
			});

		/// <summary>F6: QR koji server ne poznaje → greška prijave → Start; nijedan dalji kurirski poziv.</summary>
		[SkippableFact]
		public Task UnknownCourier_LoginFails_StaysOnStart()
			=> KioskSession.RunAsync(nameof(UnknownCourier_LoginFails_StaysOnStart), output, async kiosk => {
				var serverMark = await kiosk.Server.MarkAsync();

				var failed = await kiosk.ScanAsync("KURIR-NEPOZNAT-9", WorkflowState.Start, TimeSpan.FromSeconds(20));
				Assert.Equal(WorkflowState.CourierAuthenticating, failed.From);
				Assert.Equal("CourierLoginFailed", failed.Trigger);

				var calls = await kiosk.Server.GetBusinessCallsAsync(serverMark);
				var login = Assert.Single(calls, c => c.Endpoint == "KurirAutorizacija");
				Assert.Equal(400, login.StatusCode);
				Assert.Equal("KURIR-NEPOZNAT-9", login.Query["QrKod"]);
				Assert.DoesNotContain(calls, c => c.Endpoint is "KurirPozicijeZaPraznjenje" or "KurirPunjenjePozicije");
			});

		/// <summary>
		/// Redovna procedura — zamena veličine: paket ne staje u dodeljena vrata → „Promeni veličinu" dok su vrata otvorena
		/// (zahtev se pamti) → zatvaranje → server povećava veličinu (KurirPromenaVelicinePosiljke, S → M) → nazad na
		/// skeniranje → isti barkod → veća vrata → zatvaranje → potvrda za nova vrata → odjava. Prva vrata nemaju potvrdu.
		/// </summary>
		[SkippableFact]
		public Task Deposit_ChangeBoxSize_RescanGetsLargerBox_ConfirmsNewDoor()
			=> KioskSession.RunAsync(nameof(Deposit_ChangeBoxSize_RescanGetsLargerBox_ConfirmsNewDoor), output, async kiosk => {
				var package = kiosk.Data.DepositPackage(size: "S");
				await kiosk.Server.AddPackageAsync(package);
				var serverMark = await kiosk.Server.MarkAsync();
				var unlockMark = await kiosk.Locker.MarkUnlocksAsync();

				await OpenDepositScanAsync(kiosk);
				await kiosk.ScanAsync(package.Barkod, WorkflowState.CourierPackageDeposit);
				var first = (await kiosk.WaitForUnlockAsync(unlockMark)).Door;
				Assert.Equal("S", TestData.BoxSize(first));
				await kiosk.WaitForDoorOpenAsync(first, "CourierPackageDepositPage");

				var mark = kiosk.Log.Mark();
				await kiosk.Ui.TapAsync("CourierPackageDepositPage.ChangeBoxSize");
				// Zahtev se pamti kad handler preuzme semafor (odmah posle ove linije); zatvaranje pre toga vodi na potvrdu.
				await kiosk.WaitForLogLineAsync("CourierPackageDepositViewModel: executing ChangeBoxSizeCommand", mark);
				var back = await kiosk.CloseDoorAsync(first, WorkflowState.CourierPackageDepositBarcodeScan);
				Assert.Equal("DepositChangeBoxRequested", back.Trigger);
				Assert.NotEmpty(kiosk.Log.LinesAfter(mark, "odobreno novo sanduče veličine M"));
				await kiosk.Ui.WaitForPageAsync("CourierBarcodeScanPage");
				await kiosk.WaitForScannerReadyAsync(back);

				var secondUnlockMark = await kiosk.Locker.MarkUnlocksAsync();
				await kiosk.ScanAsync(package.Barkod, WorkflowState.CourierPackageDeposit);
				var second = (await kiosk.WaitForUnlockAsync(secondUnlockMark)).Door;
				Assert.Equal("M", TestData.BoxSize(second));
				await kiosk.WaitForDoorOpenAsync(second, "CourierPackageDepositPage");
				var toConfirmation = await kiosk.CloseDoorAsync(second, WorkflowState.CourierDepositConfirmation);
				Assert.Equal("DepositDoorClosedExplicitConfirm", toConfirmation.Trigger);
				await kiosk.Ui.WaitForPageAsync("CourierDepositConfirmationPage");
				var confirmMark = kiosk.Log.Mark();
				await kiosk.Ui.TapAsync("CourierDepositConfirmationPage.Confirm");
				await kiosk.WaitForStateAsync(WorkflowState.CourierPackageDepositBarcodeScan, confirmMark);
				await kiosk.Ui.WaitForPageAsync("CourierBarcodeScanPage");
				await LogoutFromScanAsync(kiosk);

				var calls = await kiosk.Server.GetBusinessCallsAsync(serverMark);
				AssertBoxChangeCalls(calls, package, CourierCode, second);
				Assert.Equal(second, Assert.Single(await kiosk.Server.GetPackagesAsync(), p => p.Barkod == package.Barkod).BrojPozicije);
				Assert.Equal([first, second], (await kiosk.Locker.GetUnlocksAsync(unlockMark)).Select(u => u.Door).Distinct());
			});

		/// <summary>
		/// Zamena veličine (kurir i pošiljalac): dodela vrata → promena veličine → ponovna dodela → potvrda samo za nova vrata,
		/// tim redom; promena nosi barkod i šifru kurira.
		/// </summary>
		internal static void AssertBoxChangeCalls(List<ServerCall> calls, SimPackage package, string courierCode, int confirmedDoor)
		{
			var sequence = calls.Select(c => c.Endpoint)
								.Where(e => e is "KurirPunjenjePozicije" or "KurirPromenaVelicinePosiljke" or "KurirPunjenjePozicijePotvrda")
								.ToList();
			Assert.Equal(["KurirPunjenjePozicije", "KurirPromenaVelicinePosiljke", "KurirPunjenjePozicije", "KurirPunjenjePozicijePotvrda"], sequence);
			var resize = Assert.Single(calls, c => c.Endpoint == "KurirPromenaVelicinePosiljke");
			Assert.Equal(package.Barkod, resize.RequestValue("Barkod"));
			Assert.Equal(courierCode, resize.RequestValue("SifraKurira"));
			var confirm = Assert.Single(calls, c => c.Endpoint == "KurirPunjenjePozicijePotvrda");
			Assert.Equal(package.Barkod, confirm.RequestValue("Barkod"));
			Assert.Equal(confirmedDoor.ToString(), confirm.RequestValue("BrojPozicije"));
			Assert.DoesNotContain(calls, c => c.IsUnexpectedError);
		}

		/// <summary>
		/// S2: Home dok server dodeljuje vrata kuriru (odgovor kasni 3 s) → kiosk čeka odgovor, pa Reset → Start; zakasneli
		/// DepositBarcodeAccepted je ignorisan, nema otključavanja ni potvrde (020aba5).
		/// </summary>
		[RealMouseFact]	// v4
		public Task Deposit_HomeWhileServerAssignsDoor_LateSuccessIgnored()
			=> KioskSession.RunAsync(nameof(Deposit_HomeWhileServerAssignsDoor_LateSuccessIgnored), output, async kiosk => {
				var package = kiosk.Data.DepositPackage(size: "M");
				await kiosk.Server.AddPackageAsync(package);
				var serverMark = await kiosk.Server.MarkAsync();
				var unlockMark = await kiosk.Locker.MarkUnlocksAsync();

				await OpenDepositScanAsync(kiosk);
				var homeButton = await kiosk.Ui.GetClickablePointAsync("Header.Home");	// unapred: posle skeniranja klik bez UIA poziva
				var serverDelay = TimeSpan.FromSeconds(3);	// zakasneli uspeh: kraće od RequestTimeout-a (5 s)
				await kiosk.Server.ArmFaultAsync("KurirPunjenjePozicije", delayMs: (int)serverDelay.TotalMilliseconds);	// tek posle prijave — ne usporava KurirAutorizacija
				var scanned = await kiosk.ScanAsync(package.Barkod, WorkflowState.CourierDepositBarcodeValidating);
				Assert.Equal("DepositBarcodeScanned", scanned.Trigger);

				var homeMark = kiosk.Log.Mark();
				await kiosk.Ui.ClickAtAsync(homeButton, "Header.Home");
				var toStart = await kiosk.WaitForStateAsync(WorkflowState.Start, homeMark, TimeSpan.FromSeconds(10));
				Assert.Equal(WorkflowState.CourierDepositBarcodeValidating, toStart.From);
				Assert.Equal("Reset", toStart.Trigger);
				kiosk.AssertResetWaitedForServer(scanned, homeMark, toStart, serverDelay);
				await kiosk.Ui.WaitForPageAsync("StartPage");
				await kiosk.AssertIgnoredTriggersAsync(homeMark, ("DepositBarcodeAccepted", WorkflowState.Start));

				Assert.DoesNotContain(kiosk.Log.TransitionsAfter(homeMark), t => t.To == WorkflowState.CourierPackageDeposit);
				Assert.Empty(await kiosk.Locker.GetUnlocksAsync(unlockMark));
				var calls = await kiosk.Server.GetBusinessCallsAsync(serverMark);
				var request = Assert.Single(calls, c => c.Endpoint == "KurirPunjenjePozicije");
				Assert.Equal(200, request.StatusCode);
				Assert.Equal(package.Barkod, request.Query["Barkod"]);
				Assert.DoesNotContain(calls, c => c.Endpoint == "KurirPunjenjePozicijePotvrda");
			});

		/// <summary>
		/// S3 (kurir): server odbija barkod (400, jednom) → poruka pa povratak na skeniranje, bez Start-a; ponovno skeniranje
		/// → vrata → zatvaranje → ekran potvrde → Potvrdi → skeniranje → odjava.
		/// </summary>
		[SkippableFact]
		public Task Deposit_RejectedBarcode_RescanSameBarcode_DepositsPackage()
			=> KioskSession.RunAsync(nameof(Deposit_RejectedBarcode_RescanSameBarcode_DepositsPackage), output, async kiosk => {
				var package = kiosk.Data.DepositPackage(size: "M");
				await kiosk.Server.AddPackageAsync(package);
				var serverMark = await kiosk.Server.MarkAsync();
				var unlockMark = await kiosk.Locker.MarkUnlocksAsync();

				await OpenDepositScanAsync(kiosk);
				await kiosk.Server.ArmFaultAsync("KurirPunjenjePozicije", error: "Nepoznat barkod", count: 1);
				var mark = kiosk.Log.Mark();
				await kiosk.Locker.ScanBarcodeAsync(package.Barkod);
				kiosk.Trace($"skeniran barkod {package.Barkod} (server ga odbija)");
				await kiosk.WaitForStateAsync(WorkflowState.CourierDepositBarcodeValidating, mark);
				// Poruka greške stoji CourierMessageDisplayTimeInSeconds (2 s) pre povratka na skeniranje.
				var rejected = await kiosk.WaitForStateAsync(WorkflowState.CourierPackageDepositBarcodeScan, mark);
				Assert.Equal(WorkflowState.CourierDepositBarcodeValidating, rejected.From);
				Assert.Equal("DepositBarcodeRejected", rejected.Trigger);
				await kiosk.WaitForScannerReadyAsync(rejected);

				var accepted = await kiosk.ScanAsync(package.Barkod, WorkflowState.CourierPackageDeposit);
				Assert.Equal("DepositBarcodeAccepted", accepted.Trigger);
				Assert.DoesNotContain(kiosk.Log.TransitionsAfter(mark), t => t.To == WorkflowState.Start && t.Line < accepted.Line);

				var assignment = (await kiosk.Server.GetBusinessCallsAsync(serverMark)).Last(c => c.Endpoint == "KurirPunjenjePozicije");
				var door = int.Parse(assignment.ResponseValue("BrojPozicije")!);
				Assert.Equal(door, (await kiosk.WaitForUnlockAsync(unlockMark)).Door);
				await kiosk.WaitForDoorOpenAsync(door, "CourierPackageDepositPage");
				await kiosk.CloseDoorAsync(door, WorkflowState.CourierDepositConfirmation);
				await kiosk.Ui.WaitForPageAsync("CourierDepositConfirmationPage");
				var confirmMark = kiosk.Log.Mark();
				await kiosk.Ui.TapAsync("CourierDepositConfirmationPage.Confirm");
				await kiosk.WaitForStateAsync(WorkflowState.CourierPackageDepositBarcodeScan, confirmMark);
				await kiosk.Ui.WaitForPageAsync("CourierBarcodeScanPage");
				await LogoutFromScanAsync(kiosk);

				var calls = await kiosk.Server.GetBusinessCallsAsync(serverMark);
				var requests = calls.Where(c => c.Endpoint == "KurirPunjenjePozicije").ToList();
				Assert.Equal([400, 200], requests.Select(c => c.StatusCode));
				Assert.All(requests, c => Assert.Equal(package.Barkod, c.Query["Barkod"]));
				Assert.Contains("Nepoznat barkod", requests[0].ResponseBody);
				var confirm = Assert.Single(calls, c => c.Endpoint == "KurirPunjenjePozicijePotvrda");
				Assert.Equal(door.ToString(), confirm.RequestValue("BrojPozicije"));
				Assert.Empty(kiosk.Log.IgnoredTriggersAfter(mark));
				await kiosk.AssertOnlyDoorUnlockedAsync(unlockMark, door);
			});

		/// <summary>
		/// S4 (kurir): Odustani dok server dodeljuje vrata → Start (odjava) odmah, jer Odustani prekida HTTP zahtev; vrata
		/// se ne otvaraju, potvrde nema.
		/// </summary>
		[SkippableFact]
		public Task Deposit_CancelWhileServerAssignsDoor_ReturnsToStartImmediately()
			=> KioskSession.RunAsync(nameof(Deposit_CancelWhileServerAssignsDoor_ReturnsToStartImmediately), output, async kiosk => {
				// 4 s (ne 3 s iz plana): odgovor koji Odustani ne bi prekinuo stigao bi tek posle roka od 2 s od tapa.
				const int delayMs = 4000;
				var package = kiosk.Data.DepositPackage(size: "M");
				await kiosk.Server.AddPackageAsync(package);
				var serverMark = await kiosk.Server.MarkAsync();
				var unlockMark = await kiosk.Locker.MarkUnlocksAsync();

				await OpenDepositScanAsync(kiosk);
				await kiosk.Server.ArmFaultAsync("KurirPunjenjePozicije", delayMs: delayMs);
				await kiosk.ScanAsync(package.Barkod, WorkflowState.CourierDepositBarcodeValidating);

				var cancelMark = kiosk.Log.Mark();
				await kiosk.Ui.TapAsync("CourierBarcodeScanPage.Cancel");
				var toStart = await kiosk.WaitForStateAsync(WorkflowState.Start, cancelMark, TimeSpan.FromSeconds(2));
				Assert.Equal(WorkflowState.CourierDepositBarcodeValidating, toStart.From);
				Assert.Equal("DepositBarcodeScanCancelled", toStart.Trigger);
				await kiosk.Ui.WaitForPageAsync("StartPage");

				await Task.Delay(TimeSpan.FromMilliseconds(delayMs + 1000));	// namerno: zakasneli odgovor bi do sad stigao
				KioskSteps.AssertAtMostDuplicateCancel(kiosk.Log.IgnoredTriggersAfter(cancelMark), "DepositBarcodeScanCancelled");
				Assert.DoesNotContain(kiosk.Log.TransitionsAfter(cancelMark), t => t.To == WorkflowState.CourierPackageDeposit);
				Assert.Empty(await kiosk.Locker.GetUnlocksAsync(unlockMark));
				Assert.DoesNotContain(await kiosk.Server.GetBusinessCallsAsync(serverMark), c => c.Endpoint == "KurirPunjenjePozicijePotvrda");
			});

		/// <summary>
		/// S6: ekran dostupnosti sanduka bez radnje → posle OperationTimeout (20 s) Reset → Start. Pre cbeb79b tajmer se
		/// pokretao pre promene stranice, koja ga je odmah zaustavljala — kurir je mogao zauvek da ostane na ekranu.
		/// </summary>
		[SkippableFact]
		public Task BoxAvailability_NoActivity_TimesOutToStart()
			=> KioskSession.RunAsync(nameof(BoxAvailability_NoActivity_TimesOutToStart), output, __boxAvailabilityPage, async kiosk => {
				var serverMark = await kiosk.Server.MarkAsync();

				await LoginAsync(kiosk, expect: WorkflowState.CourierLockerBoxAvailability);
				var shown = Stopwatch.StartNew();
				var mark = kiosk.Log.Mark();
				await kiosk.Ui.WaitForPageAsync("CourierLockerBoxAvailabilityPage");

				var toStart = await kiosk.WaitForStateAsync(WorkflowState.Start, mark, TimeSpan.FromSeconds(30));
				kiosk.Trace($"ekran dostupnosti → Start posle {shown.Elapsed.TotalSeconds:0.0} s");
				Assert.Equal(WorkflowState.CourierLockerBoxAvailability, toStart.From);
				Assert.Equal("Reset", toStart.Trigger);
				Assert.InRange(shown.Elapsed, __operationTimeout - TimeSpan.FromSeconds(1), __operationTimeout + TimeSpan.FromSeconds(5));
				Assert.NotEmpty(kiosk.Log.LinesAfter(mark, "OperationCountdownTimer: odbrojavanje zavrseno"));
				await kiosk.Ui.WaitForPageAsync("StartPage");

				Assert.DoesNotContain(await kiosk.Server.GetBusinessCallsAsync(serverMark), c => c.Endpoint == "KurirPozicijeZaPraznjenje");
			});

		/// <summary>
		/// S7 (kontrola za cbeb79b): „Završi" pred istek roka ekrana dostupnosti, a server sporo vraća istekle pakete →
		/// tajmer se vraća na početak pri učitavanju, pa kurir stiže do ekrana za ostavljanje. Bez ResetCounter u
		/// FetchExpiredPackagesAndRoute tajmer bi istekao usred učitavanja: Reset (dozvoljen u CourierFetchingExpiredPackages)
		/// bi se naredao ispred NoExpiredPackages i kurir bi završio na Start-u.
		/// </summary>
		[SkippableFact]
		public Task BoxAvailability_CompleteJustBeforeTimeout_SlowFetch_ReachesDepositPrompt()
			=> KioskSession.RunAsync(nameof(BoxAvailability_CompleteJustBeforeTimeout_SlowFetch_ReachesDepositPrompt), output, __boxAvailabilityPage, async kiosk => {
				await LoginAsync(kiosk, expect: WorkflowState.CourierLockerBoxAvailability);
				var shown = Stopwatch.StartNew();
				var mark = kiosk.Log.Mark();
				await kiosk.Ui.WaitForPageAsync("CourierLockerBoxAvailabilityPage");
				await kiosk.Server.ArmFaultAsync("KurirPozicijeZaPraznjenje", delayMs: 3500);

				// Namerno: 18 s na ekranu, tajmer bi istekao u 20 s — učitavanje (3,5 s) tada prelazi taj trenutak.
				await Task.Delay(TimeSpan.FromSeconds(18) - shown.Elapsed);
				Assert.Empty(kiosk.Log.TransitionsAfter(mark));

				await kiosk.Ui.TapAsync("CourierLockerBoxAvailabilityPage.Complete");
				var fetching = await kiosk.WaitForStateAsync(WorkflowState.CourierFetchingExpiredPackages, mark);
				Assert.Equal("BoxAvailabilityCompleted", fetching.Trigger);
				var prompt = await kiosk.WaitForStateAsync(WorkflowState.CourierDepositPrompt, mark, TimeSpan.FromSeconds(15));
				kiosk.Trace($"ekran za ostavljanje posle {shown.Elapsed.TotalSeconds:0.0} s od ekrana dostupnosti");
				Assert.Equal("NoExpiredPackages", prompt.Trigger);
				Assert.True(shown.Elapsed > __operationTimeout, $"Test ne razlikuje ponašanje: učitavanje je završeno posle {shown.Elapsed.TotalSeconds:0.0} s, pre isteka prvobitnog roka.");
				Assert.DoesNotContain(kiosk.Log.TransitionsAfter(mark), t => t.To == WorkflowState.Start);
				Assert.Empty(kiosk.Log.LinesAfter(mark, "OperationCountdownTimer: odbrojavanje zavrseno"));
				await kiosk.Ui.WaitForPageAsync("CourierDepositPromptPage");
			});

		/// <summary>Prijava (nema isteklih paketa) → "Ostavi" → ekran za skeniranje paketa.</summary>
		internal static async Task OpenDepositScanAsync(KioskSession kiosk)
		{
			await LoginAsync(kiosk, expect: WorkflowState.CourierDepositPrompt);
			await kiosk.Ui.WaitForPageAsync("CourierDepositPromptPage");
			var mark = kiosk.Log.Mark();
			await kiosk.Ui.TapAsync("CourierDepositPromptPage.Deposit");
			await kiosk.WaitForStateAsync(WorkflowState.CourierPackageDepositBarcodeScan, mark);
			await kiosk.Ui.WaitForPageAsync("CourierBarcodeScanPage");
		}

		/// <summary>Odustani na ekranu za skeniranje paketa = odjava → Start.</summary>
		internal static async Task LogoutFromScanAsync(KioskSession kiosk)
		{
			var mark = kiosk.Log.Mark();
			await kiosk.Ui.TapAsync("CourierBarcodeScanPage.Cancel");
			await kiosk.WaitForStateAsync(WorkflowState.Start, mark);
			await kiosk.Ui.WaitForPageAsync("StartPage");
		}

		/// <summary>Skeniranje kurirskog QR-a na Start ekranu → prijava → očekivano prvo stanje posle prijave.</summary>
		internal static async Task LoginAsync(KioskSession kiosk, WorkflowState expect)
		{
			var mark = kiosk.Log.Mark();
			await kiosk.Locker.ScanBarcodeAsync(kiosk.Data.CourierQrCode);
			kiosk.Trace($"skeniran QR kurira {kiosk.Data.CourierQrCode}");
			await kiosk.WaitForStateAsync(WorkflowState.CourierAuthenticating, mark);
			await kiosk.WaitForStateAsync(expect, mark, TimeSpan.FromSeconds(20));
		}

		private static void AssertLogin(KioskSession kiosk, List<ServerCall> calls)
		{
			var login = Assert.Single(calls, c => c.Endpoint == "KurirAutorizacija");
			Assert.Equal(200, login.StatusCode);
			Assert.Equal(kiosk.Data.CourierQrCode, login.Query["QrKod"]);
			Assert.Equal(kiosk.Settings.LockerId, login.Query["SifraBenzinskeStanice"]);
		}
	}
}
