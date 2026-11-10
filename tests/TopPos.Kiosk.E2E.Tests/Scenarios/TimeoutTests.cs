using TopPos.Client.WPF.Workflow;
using TopPos.E2E.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace TopPos.E2E.Tests.Scenarios
{
	/// <summary>
	/// Istek rokova kad korisnik ne uradi ništa (storyboard C08, C10, S04, K14, C06): istek skeniranja (BarcodeScanTracker,
	/// BarcodeScanner.ScanTrackerTimeoutInMilliseconds — ovde skraćen na 8 s) i neaktivnost na ekranima sa opštim tajmerom
	/// (OperationCountdownTimer, General.OperationTimeout 20 s). Trajanje se meri po vremenima iz loga klijenta, od ulaska u
	/// stanje do prelaza u Start. Istek na unosu koda već pokriva RecipientPickupTests.CodeInput_NoActivity_TimesOutToStart.
	/// </summary>
	[Collection(E2ECollection.Name)]
	public sealed class TimeoutTests(ITestOutputHelper output)
	{
		private const int ScanTimeoutMs = 8000;
		private static readonly TimeSpan __scanTimeout = TimeSpan.FromMilliseconds(ScanTimeoutMs);
		private static readonly TimeSpan __operationTimeout = TimeSpan.FromMilliseconds(20000);	// General.OperationTimeout u E2E konfiguraciji
		private static readonly KioskOptions __shortScan = new() {
			ConfigureClient = c => c["BarcodeScanner"]!["ScanTrackerTimeoutInMilliseconds"] = ScanTimeoutMs,
		};

		/// <summary>C10: kurir ne skenira paket za ostavljanje → istek skeniranja → Start (odjava); dodele vrata nema.</summary>
		[SkippableFact]
		public Task CourierDepositScan_NoScan_TimesOutToStart()
			=> KioskSession.RunAsync(nameof(CourierDepositScan_NoScan_TimesOutToStart), output, __shortScan, async kiosk => {
				AssertShortScanConfigured(kiosk);
				var serverMark = await kiosk.Server.MarkAsync();
				var mark = kiosk.Log.Mark();
				await CourierTests.OpenDepositScanAsync(kiosk);
				var entered = LastTransitionTo(kiosk, mark, WorkflowState.CourierPackageDepositBarcodeScan);

				var toStart = await kiosk.WaitForStateAsync(WorkflowState.Start, entered.Line, __scanTimeout + TimeSpan.FromSeconds(10));
				Assert.Equal((WorkflowState.CourierPackageDepositBarcodeScan, "DepositBarcodeScanTimeout"), (toStart.From, toStart.Trigger));
				AssertElapsed(kiosk, entered, toStart, __scanTimeout, tolerance: TimeSpan.FromSeconds(2));
				await kiosk.Ui.WaitForPageAsync("StartPage");
				Assert.DoesNotContain(await kiosk.Server.GetBusinessCallsAsync(serverMark), c => c.Endpoint == "KurirPunjenjePozicije");
			});

		/// <summary>S04: pošiljalac ne skenira paket → istek skeniranja → Start; dodele vrata nema.</summary>
		[SkippableFact]
		public Task SenderDepositScan_NoScan_TimesOutToStart()
			=> KioskSession.RunAsync(nameof(SenderDepositScan_NoScan_TimesOutToStart), output, __shortScan, async kiosk => {
				AssertShortScanConfigured(kiosk);
				var serverMark = await kiosk.Server.MarkAsync();
				var mark = kiosk.Log.Mark();
				await kiosk.Ui.TapAsync("StartPage.SenderDepositPackage");
				var entered = await kiosk.WaitForStateAsync(WorkflowState.SenderDepositBarcodeScan, mark);
				await kiosk.Ui.WaitForPageAsync("SenderBarcodeScanPage");

				var toStart = await kiosk.WaitForStateAsync(WorkflowState.Start, entered.Line, __scanTimeout + TimeSpan.FromSeconds(10));
				Assert.Equal((WorkflowState.SenderDepositBarcodeScan, "SenderBarcodeScanTimeout"), (toStart.From, toStart.Trigger));
				AssertElapsed(kiosk, entered, toStart, __scanTimeout, tolerance: TimeSpan.FromSeconds(2));
				await kiosk.Ui.WaitForPageAsync("StartPage");
				Assert.DoesNotContain(await kiosk.Server.GetBusinessCallsAsync(serverMark), c => c.Endpoint == "KurirPunjenjePozicije");
			});

		/// <summary>
		/// C08: kurir izvadi istekao paket, a ne skenira ga → istek skeniranja → Start; potvrde pražnjenja nema, server i dalje
		/// vodi paket u vratima.
		/// </summary>
		[SkippableFact]
		public Task ExpiredPickupScan_NoScan_TimesOutToStart_PackageStaysOnServer()
			=> KioskSession.RunAsync(nameof(ExpiredPickupScan_NoScan_TimesOutToStart_PackageStaysOnServer), output, __shortScan, async kiosk => {
				AssertShortScanConfigured(kiosk);
				var mark = kiosk.Log.Mark();
				var (package, serverMark) = await ExpiredPickupTests.PickUpUntilScanAsync(kiosk);
				var entered = LastTransitionTo(kiosk, mark, WorkflowState.CourierPackagePickupBarcodeScan);

				var toStart = await kiosk.WaitForStateAsync(WorkflowState.Start, entered.Line, __scanTimeout + TimeSpan.FromSeconds(10));
				Assert.Equal((WorkflowState.CourierPackagePickupBarcodeScan, "PickupBarcodeScanTimeout"), (toStart.From, toStart.Trigger));
				AssertElapsed(kiosk, entered, toStart, __scanTimeout, tolerance: TimeSpan.FromSeconds(2));
				await kiosk.Ui.WaitForPageAsync("StartPage");

				Assert.DoesNotContain(await kiosk.Server.GetBusinessCallsAsync(serverMark), c => c.Endpoint == "KurirPraznjenjePozicijePotvrda");
				ExpiredPickupTests.AssertStillOnServer(await kiosk.Server.GetPackagesAsync(), package);
			});

		/// <summary>K14: primalac unese kod, a telefon ne → posle OperationTimeout-a Reset → Start; provere na serveru nema.</summary>
		[SkippableFact]
		public Task PhoneInput_NoActivity_TimesOutToStart()
			=> KioskSession.RunAsync(nameof(PhoneInput_NoActivity_TimesOutToStart), output, async kiosk => {
				var package = kiosk.Data.RecipientPackage();
				await kiosk.Server.AddPackageAsync(package);
				var serverMark = await kiosk.Server.MarkAsync();
				var entered = await OpenPhoneInputAsync(kiosk, package);

				var toStart = await kiosk.WaitForStateAsync(WorkflowState.Start, entered.Line, __operationTimeout + TimeSpan.FromSeconds(10));
				Assert.Equal((WorkflowState.RecipientPhoneInput, "Reset"), (toStart.From, toStart.Trigger));
				Assert.NotEmpty(kiosk.Log.LinesAfter(entered.Line, "OperationCountdownTimer: odbrojavanje zavrseno"));
				AssertElapsed(kiosk, entered, toStart, __operationTimeout, tolerance: TimeSpan.FromSeconds(2));
				await kiosk.Ui.WaitForPageAsync("StartPage");
				Assert.DoesNotContain(await kiosk.Server.GetBusinessCallsAsync(serverMark), c => c.Endpoint == "PrimaocPraznjenjePozicije");
			});

		/// <summary>K14: taster na ekranu za telefon vraća rok na početak — istek dolazi pun rok posle poslednjeg tastera.</summary>
		[SkippableFact]
		public Task PhoneInput_KeyPress_RestartsTimeout()
			=> KioskSession.RunAsync(nameof(PhoneInput_KeyPress_RestartsTimeout), output, async kiosk => {
				var package = kiosk.Data.RecipientPackage();
				await kiosk.Server.AddPackageAsync(package);
				var entered = await OpenPhoneInputAsync(kiosk, package);

				await Task.Delay(__operationTimeout - TimeSpan.FromSeconds(5));	// namerno: taster pred istek prvobitnog roka
				Assert.Empty(kiosk.Log.TransitionsAfter(entered.Line + 1));	// entered.Line je sam ulazni prelaz
				var keyMark = kiosk.Log.Mark();
				await kiosk.Ui.TypeOnKeypadAsync("0", confirm: false);
				await kiosk.WaitForLogLineAsync("OperationCountdownTimer: brojac resetovan", keyMark);
				var keyLine = kiosk.Log.FirstLineAfter(keyMark, "OperationCountdownTimer: brojac resetovan")!.Value;

				var toStart = await kiosk.WaitForStateAsync(WorkflowState.Start, keyMark, __operationTimeout + TimeSpan.FromSeconds(10));
				Assert.Equal((WorkflowState.RecipientPhoneInput, "Reset"), (toStart.From, toStart.Trigger));
				var afterKey = kiosk.Log.TimeOf(toStart.Line) - kiosk.Log.TimeOf(keyLine);
				kiosk.Trace($"istek {afterKey.TotalSeconds:0.0} s posle tastera");
				Assert.InRange(afterKey, __operationTimeout - TimeSpan.FromSeconds(1), __operationTimeout + TimeSpan.FromSeconds(2));
			});

		/// <summary>C06: kurir posle prijave ne izabere ništa na ekranu za ostavljanje → Reset → Start.</summary>
		[SkippableFact]
		public Task CourierDepositPrompt_NoActivity_TimesOutToStart()
			=> KioskSession.RunAsync(nameof(CourierDepositPrompt_NoActivity_TimesOutToStart), output, async kiosk => {
				var mark = kiosk.Log.Mark();
				await CourierTests.LoginAsync(kiosk, expect: WorkflowState.CourierDepositPrompt);
				var entered = LastTransitionTo(kiosk, mark, WorkflowState.CourierDepositPrompt);
				await kiosk.Ui.WaitForPageAsync("CourierDepositPromptPage");

				var toStart = await kiosk.WaitForStateAsync(WorkflowState.Start, entered.Line, __operationTimeout + TimeSpan.FromSeconds(10));
				Assert.Equal((WorkflowState.CourierDepositPrompt, "Reset"), (toStart.From, toStart.Trigger));
				AssertElapsed(kiosk, entered, toStart, __operationTimeout, tolerance: TimeSpan.FromSeconds(2));
				await kiosk.Ui.WaitForPageAsync("StartPage");
			});

		/// <summary>
		/// C06: kurir ne izabere ništa na ekranu za preuzimanje isteklih → Reset → Start; vrata se ne otvaraju, server i dalje
		/// vodi paket.
		/// </summary>
		[SkippableFact]
		public Task CourierPickupPrompt_NoActivity_TimesOutToStart()
			=> KioskSession.RunAsync(nameof(CourierPickupPrompt_NoActivity_TimesOutToStart), output, async kiosk => {
				var package = kiosk.Data.ExpiredPackage();
				await kiosk.Server.AddPackageAsync(package);
				var unlockMark = await kiosk.Locker.MarkUnlocksAsync();
				var mark = kiosk.Log.Mark();
				await CourierTests.LoginAsync(kiosk, expect: WorkflowState.CourierPickupPrompt);
				var entered = LastTransitionTo(kiosk, mark, WorkflowState.CourierPickupPrompt);
				await kiosk.Ui.WaitForPageAsync("CourierPickupPromptPage");

				var toStart = await kiosk.WaitForStateAsync(WorkflowState.Start, entered.Line, __operationTimeout + TimeSpan.FromSeconds(10));
				Assert.Equal((WorkflowState.CourierPickupPrompt, "Reset"), (toStart.From, toStart.Trigger));
				AssertElapsed(kiosk, entered, toStart, __operationTimeout, tolerance: TimeSpan.FromSeconds(2));
				await kiosk.Ui.WaitForPageAsync("StartPage");

				Assert.Empty(await kiosk.Locker.GetUnlocksAsync(unlockMark));
				ExpiredPickupTests.AssertStillOnServer(await kiosk.Server.GetPackagesAsync(), package);
			});

		/// <summary>Start → „Preuzmi paket" → kod → ekran za telefon. Vraća prelaz u RecipientPhoneInput.</summary>
		private static async Task<Transition> OpenPhoneInputAsync(KioskSession kiosk, SimPackage package)
		{
			await kiosk.StartRecipientPickupAsync();
			var mark = kiosk.Log.Mark();
			await kiosk.Ui.TypeOnKeypadAsync(package.KodZaOtvaranje.ToString());
			var entered = await kiosk.WaitForStateAsync(WorkflowState.RecipientPhoneInput, mark);
			await kiosk.Ui.WaitForPageAsync("RecipientPhoneInputPage");
			return entered;
		}

		/// <summary>Klijent je pročitao skraćen rok skeniranja (BarcodeScanTracker ga loguje pri inicijalizaciji).</summary>
		private static void AssertShortScanConfigured(KioskSession kiosk)
			=> Assert.NotEmpty(kiosk.Log.LinesAfter(0, $"Vremenski limit:{ScanTimeoutMs / 1000} s"));

		private static Transition LastTransitionTo(KioskSession kiosk, int mark, WorkflowState state)
			=> kiosk.Log.TransitionsAfter(mark).Last(t => t.To == state);

		/// <summary>Vreme od ulaska u stanje do izlaska, po vremenima iz loga klijenta.</summary>
		private static void AssertElapsed(KioskSession kiosk, Transition entered, Transition left, TimeSpan expected, TimeSpan tolerance)
		{
			var elapsed = kiosk.Log.TimeOf(left.Line) - kiosk.Log.TimeOf(entered.Line);
			kiosk.Trace($"{entered.To}: {elapsed.TotalSeconds:0.0} s do {left.Trigger}");
			Assert.InRange(elapsed, expected - TimeSpan.FromSeconds(1), expected + tolerance);
		}
	}
}
