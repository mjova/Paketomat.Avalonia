using TopPos.Kiosk.Workflow;
using TopPos.E2E.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace TopPos.E2E.Tests.Scenarios
{
	/// <summary>
	/// Ekran fiskalnog računa posle plaćanja (PenPOS uključen) — vault: backlog/Fiskalni račun — isteklo vreme pre otvaranja
	/// vrata (plan testa). Regresija: opšti tajmer (OperationCountdownTimer) na ovom ekranu ne radi (cbeb79b), pa ga završavaju
	/// samo rokovi ekrana, a svi otvaraju vrata. Da opšti tajmer radi, posle OperationTimeout-a bi poslao Reset → Start, a
	/// plaćeni kupac bi ostao bez paketa. PenPOS (/pin, /invoice) glumi ServerSimulator na istom portu (49b2f3c).
	/// Rokovi ekrana su OperationTimeout (20 s u e2e): dohvatanje računa i skeniranje QR-a; kad račun ne stigne, 10 s.
	/// </summary>
	[Collection(E2ECollection.Name)]
	public sealed class FiscalReceiptTests(ITestOutputHelper output)
	{
		private const decimal Amount = 250m;
		private static readonly TimeSpan __screenTimeout = TimeSpan.FromSeconds(20);		// OperationTimeout iz client.app-configuration.json
		private static readonly TimeSpan __noReceiptWait = TimeSpan.FromSeconds(10);		// RecipientFiscalReceiptViewModel: račun nije dobijen
		private static readonly TimeSpan __tolerance = TimeSpan.FromSeconds(1.5);

		/// <summary>
		/// Scenario 1 (glavni): račun stiže posle 5 s, kupac ništa ne dira → QR se prikazuje punih 20 s, pa istek skeniranja
		/// otvara vrata (oko 25 s od ulaska na ekran). Opšti tajmer bi istekao posle 20 s, dok QR još stoji na ekranu.
		/// </summary>
		[SkippableFact]
		public Task ReceiptLate_NoTouch_ScanTimeoutOpensDoor()
			=> RunAsync(nameof(ReceiptLate_NoTouch_ScanTimeoutOpensDoor), invoiceDelay: TimeSpan.FromSeconds(5), invoiceError: null,
						expectedLogLine: "scan timeout reached",
						expectedAfter: TimeSpan.FromSeconds(5) + __screenTimeout, maxAfter: TimeSpan.FromSeconds(5) + __screenTimeout + TimeSpan.FromSeconds(5));

		/// <summary>Scenario 2 (kontrola): čim se QR prikaže, kupac pritisne „Završi“ → vrata.</summary>
		[SkippableFact]
		public Task CompletePressed_OpensDoor()
			=> RunAsync(nameof(CompletePressed_OpensDoor), invoiceDelay: TimeSpan.Zero, invoiceError: null,
						expectedLogLine: "CompleteCommand: button clicked",
						expectedAfter: TimeSpan.Zero, maxAfter: TimeSpan.FromSeconds(10),
						onReceiptShown: kiosk => kiosk.Ui.TapAsync("RecipientFiscalReceiptPage.Complete"));

		/// <summary>Scenario 3 (kontrola): PenPOS vrati grešku → poruka „račun nije obrađen“ → posle 10 s vrata.</summary>
		[SkippableFact]
		public Task InvoiceError_OpensDoorAfterTenSeconds()
			=> RunAsync(nameof(InvoiceError_OpensDoorAfterTenSeconds), invoiceDelay: TimeSpan.Zero, invoiceError: "E2E: racun odbijen",
						expectedLogLine: "odgovor na zahtev za dobijanje fiskalnog racuna nije dobijen",
						expectedAfter: __noReceiptWait, maxAfter: __noReceiptWait + TimeSpan.FromSeconds(5));

		/// <summary>
		/// Scenario 4: račun kasni 25 s — duže od roka dohvatanja (20 s), kraće od PenPOS RequestTimeout-a (30 s). Rok dohvatanja
		/// otkaže zahtev → „račun nije obrađen“ → posle 10 s vrata (oko 30 s od ulaska). Ranije je ovde opšti tajmer (isto
		/// 20 s) bio u trci sa rokom dohvatanja.
		/// </summary>
		[SkippableFact]
		public Task ReceiptNeverInTime_OpensDoorAfterFetchTimeout()
			=> RunAsync(nameof(ReceiptNeverInTime_OpensDoorAfterFetchTimeout), invoiceDelay: TimeSpan.FromSeconds(25), invoiceError: null,
						expectedLogLine: "odgovor na zahtev za dobijanje fiskalnog racuna nije dobijen",
						expectedAfter: __screenTimeout + __noReceiptWait, maxAfter: __screenTimeout + __noReceiptWait + TimeSpan.FromSeconds(5));

		/// <summary>PenPOS u klijentu uključen i usmeren na ServerSimulator (BaseManagementUrl → /pin, BaseInvoiceUrl → /invoice).</summary>
		private static KioskOptions PenPosOptions() => new() {
			ConfigureClient = c => {
				var penPos = c["PenPosService"]!;
				// v4: adrese ServerSimulator-a su već u šablonu ({{ServerPort}} — port je poznat tek iz READY reda)
				penPos["IsEnabled"] = true;
				penPos["RequestTimeout"] = 30000;	// duže od kašnjenja u scenariju 4, inače klijent sam odustane i meri se druga grana
			},
		};

		/// <summary>
		/// Plaćanje odobreno → ekran fiskalnog računa (t0) → završetak ekrana (RecipientFiscalReceiptCompleted) → vrata →
		/// zatvaranje → Start. Završetak mora doći iz očekivane grane (<paramref name="expectedLogLine"/>) i ne pre
		/// <paramref name="expectedAfter"/> od t0; posle t0 opšti tajmer ne sme isteći, a Reset sa ekrana ne sme stići.
		/// </summary>
		private Task RunAsync(string testName, TimeSpan invoiceDelay, string? invoiceError, string expectedLogLine,
								TimeSpan expectedAfter, TimeSpan maxAfter, Func<KioskSession, Task>? onReceiptShown = null)
			=> KioskSession.RunAsync(testName, output, PenPosOptions(), async kiosk => {
				Assert.NotEmpty(kiosk.Log.LinesAfter(0, "PenPOS uredjaj inicijalizovan"));	// inače nema ekrana fiskalnog računa i test ne meri ništa
				var package = kiosk.Data.RecipientPackage(amount: Amount);
				await kiosk.Server.AddPackageAsync(package);
				var serverMark = await kiosk.Server.MarkAsync();
				var unlockMark = await kiosk.Locker.MarkUnlocksAsync();
				if (invoiceDelay > TimeSpan.Zero || invoiceError != null) {
					await kiosk.Server.ArmFaultAsync("invoice", delayMs: (int)invoiceDelay.TotalMilliseconds, error: invoiceError);
				}

				await RecipientPaymentTests.StartPaymentAsync(kiosk, package);
				var mark = kiosk.Log.Mark();
				await kiosk.Locker.PresentCardAsync("MastercardContactless");
				kiosk.Trace("POS: kartica MastercardContactless");
				var toReceipt = await kiosk.WaitForStateAsync(WorkflowState.RecipientFiscalReceipt, mark, TimeSpan.FromSeconds(45));
				Assert.Equal("RecipientPaymentApproved", toReceipt.Trigger);
				await kiosk.Ui.WaitForPageAsync("RecipientFiscalReceiptPage");

				if (onReceiptShown != null) {
					await kiosk.WaitForLogLineAsync("generisanje QR koda za fiskalni racun", toReceipt.Line);
					await onReceiptShown(kiosk);
				}

				var completed = await kiosk.WaitForStateAsync(WorkflowState.RecipientPackagePickup, toReceipt.Line, maxAfter + TimeSpan.FromSeconds(10));
				Assert.Equal(WorkflowState.RecipientFiscalReceipt, completed.From);
				Assert.Equal("RecipientFiscalReceiptCompleted", completed.Trigger);
				var elapsed = kiosk.Log.TimeOf(completed.Line) - kiosk.Log.TimeOf(toReceipt.Line);
				kiosk.Trace($"ekran fiskalnog računa: {elapsed.TotalSeconds:0.0} s");
				Assert.True(elapsed >= expectedAfter - __tolerance && elapsed <= maxAfter,
							$"Ekran fiskalnog računa trajao {elapsed.TotalSeconds:0.0} s, očekivano {expectedAfter.TotalSeconds:0}–{maxAfter.TotalSeconds:0} s.");
				Assert.NotEmpty(kiosk.Log.LinesAfter(toReceipt.Line, expectedLogLine));
				// Deterministički, nezavisno od rokova: opšti tajmer se na ovom ekranu ne pokreće (ni pre ni posle CurrentPage).
				var timerStarted = kiosk.Log.FirstLineAfter(toReceipt.Line, "OperationCountdownTimer: pokretanje odbrojavanja");
				Assert.True(timerStarted is null || timerStarted > completed.Line, "Opšti tajmer je pokrenut na ekranu fiskalnog računa.");
				Assert.Empty(kiosk.Log.LinesAfter(toReceipt.Line, "OperationCountdownTimer: odbrojavanje zavrseno"));
				Assert.DoesNotContain(kiosk.Log.TransitionsAfter(toReceipt.Line), t => t.From == WorkflowState.RecipientFiscalReceipt && t.To == WorkflowState.Start);

				await kiosk.WaitForDoorOpenAsync(package.BrojPozicije, "RecipientPackagePickupPage");
				await kiosk.CloseDoorAsync(package.BrojPozicije, WorkflowState.Start);
				await kiosk.Ui.WaitForPageAsync("StartPage");

				var calls = await kiosk.Server.GetBusinessCallsAsync(serverMark);
				var payment = calls.FindIndex(c => c.Endpoint == "PotvrdaPlacanja" && c.StatusCode == 200);
				var invoice = calls.FindIndex(c => c.Endpoint == "invoice");
				var pickup = calls.FindIndex(c => c.Endpoint == "PrimaocPraznjenjePozicijePotvrda" && c.StatusCode == 200);
				Assert.True(payment >= 0 && invoice > payment && pickup > payment,
							$"Očekivano: potvrda plaćanja, zatim račun i potvrda preuzimanja. Pozivi: {string.Join(", ", calls)}");
				if (invoiceDelay < __screenTimeout) {
					Assert.True(invoice < pickup, $"Račun mora biti zatražen pre preuzimanja. Pozivi: {string.Join(", ", calls)}");
				}
				await kiosk.AssertOnlyDoorUnlockedAsync(unlockMark, package.BrojPozicije);
			});
	}
}
