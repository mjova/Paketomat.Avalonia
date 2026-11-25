using TopPos.Kiosk.Workflow;
using TopPos.E2E.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace TopPos.E2E.Tests.Scenarios
{
	/// <summary>
	/// Pošiljalac ostavlja paket sa Start ekrana (doc/plan-e2e-ui-testing.md, F5 scenario 8), i provere ispravki iz plana
	/// „E2E — provere commit-ovanih ispravki" (S1, S3, S4): zakasneli odgovor posle Home-a, odbijen barkod, Odustani.
	/// </summary>
	[Collection(E2ECollection.Name)]
	public sealed class SenderDepositTests(ITestOutputHelper output)
	{
		private const string SenderCourierCode = "30";	// General.SenderDepositCourierCode u client.app-configuration.json

		/// <summary>
		/// Scenario 8: "Ostavi paket" → skeniranje → server dodeljuje vrata (šifra kurira "30") → vrata se otvaraju →
		/// zatvaranje = paket ostavljen → potvrda serveru → Start (bez ekrana za potvrdu).
		/// </summary>
		[SkippableFact]
		public Task Deposit_ScanOpenClose_ConfirmsWithSenderCode()
			=> KioskSession.RunAsync(nameof(Deposit_ScanOpenClose_ConfirmsWithSenderCode), output, async kiosk => {
				var package = kiosk.Data.DepositPackage(size: "M");
				await kiosk.Server.AddPackageAsync(package);
				var serverMark = await kiosk.Server.MarkAsync();
				var unlockMark = await kiosk.Locker.MarkUnlocksAsync();

				var mark = kiosk.Log.Mark();
				await kiosk.Ui.TapAsync("StartPage.SenderDepositPackage");
				await kiosk.WaitForStateAsync(WorkflowState.SenderDepositBarcodeScan, mark);
				await kiosk.Ui.WaitForPageAsync("SenderBarcodeScanPage");

				await kiosk.ScanAsync(package.Barkod, WorkflowState.SenderPackageDeposit);
				// Očekivana vrata su ona koja je server dodelio — ne ona koja je klijent otključao (inače bi pogrešan
				// izbor klijenta, poslat i u potvrdi, prošao sve provere).
				var assignment = Assert.Single(await kiosk.Server.GetBusinessCallsAsync(serverMark), c => c.Endpoint == "KurirPunjenjePozicije");
				var door = int.Parse(assignment.ResponseValue("BrojPozicije")!);
				Assert.InRange(door, 1, 48);
				Assert.Equal(door, (await kiosk.WaitForUnlockAsync(unlockMark)).Door);
				await kiosk.WaitForDoorOpenAsync(door, "SenderPackageDepositPage");

				var toStart = await kiosk.CloseDoorAsync(door, WorkflowState.Start);
				Assert.Equal("SenderDoorClosedConfirm", toStart.Trigger);
				await kiosk.Ui.WaitForPageAsync("StartPage");

				var calls = await kiosk.Server.GetBusinessCallsAsync(serverMark);
				var request = Assert.Single(calls, c => c.Endpoint == "KurirPunjenjePozicije");
				Assert.Equal(package.Barkod, request.Query["Barkod"]);
				Assert.Equal(SenderCourierCode, request.Query["SifraKurira"]);
				var confirm = Assert.Single(calls, c => c.Endpoint == "KurirPunjenjePozicijePotvrda");
				Assert.Equal(package.Barkod, confirm.RequestValue("Barkod"));
				Assert.Equal(door.ToString(), confirm.RequestValue("BrojPozicije"));
				Assert.Equal(SenderCourierCode, confirm.RequestValue("SifraKurira"));
				Assert.True(confirm.Seq > request.Seq);
				Assert.DoesNotContain(calls, c => c.IsError || c.Endpoint == "KurirAutorizacija");

				var stored = Assert.Single(await kiosk.Server.GetPackagesAsync(), p => p.Barkod == package.Barkod);
				Assert.Equal(door, stored.BrojPozicije);
				await kiosk.AssertOnlyDoorUnlockedAsync(unlockMark, door);
			});

		/// <summary>
		/// Redovna procedura — zamena veličine: „Promeni veličinu" dok su vrata otvorena → zatvaranje → server povećava
		/// veličinu (S → M) → nazad na skeniranje → isti barkod → veća vrata → zatvaranje → potvrda za nova vrata → Start.
		/// </summary>
		[SkippableFact]
		public Task ChangeBoxSize_RescanGetsLargerBox_ConfirmsNewDoor()
			=> KioskSession.RunAsync(nameof(ChangeBoxSize_RescanGetsLargerBox_ConfirmsNewDoor), output, async kiosk => {
				var package = kiosk.Data.DepositPackage(size: "S");
				await kiosk.Server.AddPackageAsync(package);
				var serverMark = await kiosk.Server.MarkAsync();
				var unlockMark = await kiosk.Locker.MarkUnlocksAsync();

				await OpenScanPageAsync(kiosk);
				await kiosk.ScanAsync(package.Barkod, WorkflowState.SenderPackageDeposit);
				var first = (await kiosk.WaitForUnlockAsync(unlockMark)).Door;
				Assert.Equal("S", TestData.BoxSize(first));
				await kiosk.WaitForDoorOpenAsync(first, "SenderPackageDepositPage");

				var mark = kiosk.Log.Mark();
				await kiosk.Ui.TapAsync("SenderPackageDepositPage.ChangeBoxSize");
				// Zahtev se pamti kad handler preuzme semafor (odmah posle ove linije); zatvaranje pre toga vodi na potvrdu.
				await kiosk.WaitForLogLineAsync("SenderPackageDepositViewModel: executing ChangeBoxSizeCommand", mark);
				var back = await kiosk.CloseDoorAsync(first, WorkflowState.SenderDepositBarcodeScan);
				Assert.Equal("SenderChangeBoxRequested", back.Trigger);
				Assert.NotEmpty(kiosk.Log.LinesAfter(mark, "odobreno novo sanduče veličine M"));
				await kiosk.Ui.WaitForPageAsync("SenderBarcodeScanPage");
				await kiosk.WaitForScannerReadyAsync(back);

				var secondUnlockMark = await kiosk.Locker.MarkUnlocksAsync();
				await kiosk.ScanAsync(package.Barkod, WorkflowState.SenderPackageDeposit);
				var second = await DepositAfterAcceptedAsync(kiosk, serverMark, secondUnlockMark);
				Assert.Equal("M", TestData.BoxSize(second));

				CourierTests.AssertBoxChangeCalls(await kiosk.Server.GetBusinessCallsAsync(serverMark), package, SenderCourierCode, second);
				Assert.Equal(second, Assert.Single(await kiosk.Server.GetPackagesAsync(), p => p.Barkod == package.Barkod).BrojPozicije);
				Assert.Equal([first, second], (await kiosk.Locker.GetUnlocksAsync(unlockMark)).Select(u => u.Door).Distinct());
			});

		/// <summary>
		/// S1: Home dok server dodeljuje vrata (odgovor kasni 3 s, kraće od RequestTimeout-a) → kiosk ostaje zauzet dok
		/// odgovor ne stigne, pa Reset → Start; zakasneli SenderBarcodeAccepted je ignorisan, vrata se ne otvaraju i potvrde
		/// nema. Pre ispravke (020aba5) Home je odmah vraćao na Start, a zakasneli odgovor je otvarao vrata sledećem.
		/// Kontrola: isti pošiljalac zatim normalno ostavlja drugi paket — kontekst odbačene sesije se ne prenosi.
		/// </summary>
		[RealMouseFact]	// v4
		public Task HomeWhileServerAssignsDoor_LateSuccessIgnored_NextDepositWorks()
			=> KioskSession.RunAsync(nameof(HomeWhileServerAssignsDoor_LateSuccessIgnored_NextDepositWorks), output, async kiosk => {
				var abandoned = kiosk.Data.DepositPackage(size: "M");
				var next = kiosk.Data.DepositPackage(size: "M");
				await kiosk.Server.AddPackageAsync(abandoned);
				await kiosk.Server.AddPackageAsync(next);
				var serverMark = await kiosk.Server.MarkAsync();
				var unlockMark = await kiosk.Locker.MarkUnlocksAsync();
				var serverDelay = TimeSpan.FromSeconds(3);	// zakasneli uspeh: kraće od RequestTimeout-a (5 s)
				await kiosk.Server.ArmFaultAsync("KurirPunjenjePozicije", delayMs: (int)serverDelay.TotalMilliseconds);

				await OpenScanPageAsync(kiosk);
				var homeButton = await kiosk.Ui.GetClickablePointAsync("Header.Home");	// unapred: posle skeniranja klik bez UIA poziva
				var validating = await kiosk.ScanAsync(abandoned.Barkod, WorkflowState.SenderDepositBarcodeValidating);

				var homeMark = kiosk.Log.Mark();
				await kiosk.Ui.ClickAtAsync(homeButton, "Header.Home");
				var toStart = await kiosk.WaitForStateAsync(WorkflowState.Start, homeMark, TimeSpan.FromSeconds(10));
				Assert.Equal(WorkflowState.SenderDepositBarcodeValidating, toStart.From);
				Assert.Equal("Reset", toStart.Trigger);
				kiosk.AssertResetWaitedForServer(validating, homeMark, toStart, serverDelay);
				await kiosk.Ui.WaitForPageAsync("StartPage");
				await kiosk.AssertIgnoredTriggersAsync(homeMark, ("SenderBarcodeAccepted", WorkflowState.Start));

				Assert.DoesNotContain(kiosk.Log.TransitionsAfter(homeMark), t => t.To == WorkflowState.SenderPackageDeposit);
				Assert.Empty(await kiosk.Locker.GetUnlocksAsync(unlockMark));
				var calls = await kiosk.Server.GetBusinessCallsAsync(serverMark);
				var request = Assert.Single(calls, c => c.Endpoint == "KurirPunjenjePozicije");
				Assert.Equal(200, request.StatusCode);
				Assert.Equal(abandoned.Barkod, request.Query["Barkod"]);
				Assert.DoesNotContain(calls, c => c.Endpoint == "KurirPunjenjePozicijePotvrda");

				// Kontrola: sledeće ostavljanje radi i potvrđuje samo novi paket.
				var nextServerMark = await kiosk.Server.MarkAsync();
				var nextUnlockMark = await kiosk.Locker.MarkUnlocksAsync();
				await OpenScanPageAsync(kiosk);
				await kiosk.ScanAsync(next.Barkod, WorkflowState.SenderPackageDeposit);
				var door = await DepositAfterAcceptedAsync(kiosk, nextServerMark, nextUnlockMark);

				var nextCalls = await kiosk.Server.GetBusinessCallsAsync(nextServerMark);
				Assert.Equal(next.Barkod, Assert.Single(nextCalls, c => c.Endpoint == "KurirPunjenjePozicije").Query["Barkod"]);
				var confirm = Assert.Single(nextCalls, c => c.Endpoint == "KurirPunjenjePozicijePotvrda");
				Assert.Equal(next.Barkod, confirm.RequestValue("Barkod"));
				Assert.Equal(door.ToString(), confirm.RequestValue("BrojPozicije"));
				Assert.Equal(0, Assert.Single(await kiosk.Server.GetPackagesAsync(), p => p.Barkod == abandoned.Barkod).BrojPozicije);
				await kiosk.AssertOnlyDoorUnlockedAsync(nextUnlockMark, door);
			});

		/// <summary>
		/// S3: server odbija barkod (400, jednom) → poruka pa povratak na skeniranje, bez Start-a; ponovno skeniranje istog
		/// barkoda → server dodeljuje vrata → otvaranje → zatvaranje → Start.
		/// </summary>
		[SkippableFact]
		public Task RejectedBarcode_RescanSameBarcode_DepositsPackage()
			=> KioskSession.RunAsync(nameof(RejectedBarcode_RescanSameBarcode_DepositsPackage), output, async kiosk => {
				var package = kiosk.Data.DepositPackage(size: "M");
				await kiosk.Server.AddPackageAsync(package);
				var serverMark = await kiosk.Server.MarkAsync();
				var unlockMark = await kiosk.Locker.MarkUnlocksAsync();
				await kiosk.Server.ArmFaultAsync("KurirPunjenjePozicije", error: "Nepoznat barkod", count: 1);

				await OpenScanPageAsync(kiosk);
				var mark = kiosk.Log.Mark();
				await kiosk.Locker.ScanBarcodeAsync(package.Barkod);
				kiosk.Trace($"skeniran barkod {package.Barkod} (server ga odbija)");
				await kiosk.WaitForStateAsync(WorkflowState.SenderDepositBarcodeValidating, mark);
				// Poruka greške stoji CourierMessageDisplayTimeInSeconds (2 s) pre povratka na skeniranje.
				var rejected = await kiosk.WaitForStateAsync(WorkflowState.SenderDepositBarcodeScan, mark);
				Assert.Equal(WorkflowState.SenderDepositBarcodeValidating, rejected.From);
				Assert.Equal("SenderBarcodeRejected", rejected.Trigger);
				await kiosk.WaitForScannerReadyAsync(rejected);

				var accepted = await kiosk.ScanAsync(package.Barkod, WorkflowState.SenderPackageDeposit);
				Assert.Equal("SenderBarcodeAccepted", accepted.Trigger);
				Assert.DoesNotContain(kiosk.Log.TransitionsAfter(mark), t => t.To == WorkflowState.Start && t.Line < accepted.Line);
				var door = await DepositAfterAcceptedAsync(kiosk, serverMark, unlockMark);

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
		/// S4: Odustani dok server dodeljuje vrata → Start odmah (Odustani prekida HTTP zahtev), ne tek kad bi stigao
		/// odgovor; vrata se ne otvaraju, potvrde nema. Isti triger šalju i dugme i activator, pa drugi sme biti ignorisan.
		/// </summary>
		[SkippableFact]
		public Task CancelWhileServerAssignsDoor_ReturnsToStartImmediately()
			=> KioskSession.RunAsync(nameof(CancelWhileServerAssignsDoor_ReturnsToStartImmediately), output, async kiosk => {
				// 4 s (ne 3 s iz plana): odgovor koji Odustani ne bi prekinuo stigao bi tek posle roka od 2 s od tapa.
				// I dalje kraće od RequestTimeout-a (5 s), pa bi to bio zakasneli uspeh, a ne odustajanje klijenta.
				const int delayMs = 4000;
				var package = kiosk.Data.DepositPackage(size: "M");
				await kiosk.Server.AddPackageAsync(package);
				var serverMark = await kiosk.Server.MarkAsync();
				var unlockMark = await kiosk.Locker.MarkUnlocksAsync();
				await kiosk.Server.ArmFaultAsync("KurirPunjenjePozicije", delayMs: delayMs);

				await OpenScanPageAsync(kiosk);
				await kiosk.ScanAsync(package.Barkod, WorkflowState.SenderDepositBarcodeValidating);

				var cancelMark = kiosk.Log.Mark();
				await kiosk.Ui.TapAsync("SenderBarcodeScanPage.Cancel");
				var toStart = await kiosk.WaitForStateAsync(WorkflowState.Start, cancelMark, TimeSpan.FromSeconds(2));
				Assert.Equal(WorkflowState.SenderDepositBarcodeValidating, toStart.From);
				Assert.Equal("SenderBarcodeScanCancelled", toStart.Trigger);
				await kiosk.Ui.WaitForPageAsync("StartPage");

				await Task.Delay(TimeSpan.FromMilliseconds(delayMs + 1000));	// namerno: zakasneli odgovor bi do sad stigao
				KioskSteps.AssertAtMostDuplicateCancel(kiosk.Log.IgnoredTriggersAfter(cancelMark), "SenderBarcodeScanCancelled");
				Assert.DoesNotContain(kiosk.Log.TransitionsAfter(cancelMark), t => t.To == WorkflowState.SenderPackageDeposit);
				Assert.Empty(await kiosk.Locker.GetUnlocksAsync(unlockMark));
				Assert.DoesNotContain(await kiosk.Server.GetBusinessCallsAsync(serverMark), c => c.Endpoint == "KurirPunjenjePozicijePotvrda");
			});

		/// <summary>Start → "Ostavi paket" → ekran za skeniranje.</summary>
		private static async Task OpenScanPageAsync(KioskSession kiosk)
		{
			var mark = kiosk.Log.Mark();
			await kiosk.Ui.TapAsync("StartPage.SenderDepositPackage");
			await kiosk.WaitForStateAsync(WorkflowState.SenderDepositBarcodeScan, mark);
			await kiosk.Ui.WaitForPageAsync("SenderBarcodeScanPage");
		}

		/// <summary>
		/// Posle prihvaćenog barkoda: otključana su vrata iz poslednjeg uspešnog odgovora servera → zatvaranje → Start.
		/// Vraća vrata.
		/// </summary>
		private static async Task<int> DepositAfterAcceptedAsync(KioskSession kiosk, long serverMark, long unlockMark)
		{
			var assignment = (await kiosk.Server.GetBusinessCallsAsync(serverMark)).Last(c => c.Endpoint == "KurirPunjenjePozicije" && c.StatusCode == 200);
			var door = int.Parse(assignment.ResponseValue("BrojPozicije")!);
			Assert.InRange(door, 1, 48);
			Assert.Equal(door, (await kiosk.WaitForUnlockAsync(unlockMark)).Door);
			await kiosk.WaitForDoorOpenAsync(door, "SenderPackageDepositPage");

			var toStart = await kiosk.CloseDoorAsync(door, WorkflowState.Start);
			Assert.Equal("SenderDoorClosedConfirm", toStart.Trigger);
			await kiosk.Ui.WaitForPageAsync("StartPage");
			return door;
		}
	}
}
