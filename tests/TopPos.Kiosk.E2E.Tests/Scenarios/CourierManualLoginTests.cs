using System.Diagnostics;
using TopPos.Kiosk.Workflow;
using TopPos.E2E.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace TopPos.E2E.Tests.Scenarios
{
	/// <summary>
	/// Ručna prijava kurira (84e955a): skeniran barkod otvara unos koda, a server dobija QrKod=&lt;barkod&gt;;&lt;kod&gt;.
	/// Plan „E2E — ručna prijava kurira i zakasneli odgovor", scenariji 1–3, i S5 iz plana „E2E — provere
	/// commit-ovanih ispravki". Konfiguracija po testu: EnableCourierManualLogin=true; server prihvata samo "QR-M1;4321".
	/// Kad je ručna prijava uključena, svaki skeniran barkod vodi na unos koda (TryHandleCourierOrServiceBarcode).
	/// </summary>
	[Collection(E2ECollection.Name)]
	public sealed class CourierManualLoginTests(ITestOutputHelper output)
	{
		private const string ScannedBarcode = "QR-M1";
		private const string Code = "4321";
		private const string WrongCode = "1111";
		private const string CourierCode = "K001";
		private static readonly TimeSpan __operationTimeout = TimeSpan.FromMilliseconds(20000);	// General.OperationTimeout u E2E konfiguraciji

		private static readonly KioskOptions __manualLogin = new() {
			ConfigureClient = c => c["General"]!["EnableCourierManualLogin"] = true,
			ConfigureServer = c => c["CourierQrCodes"]!.AsArray().Add($"{ScannedBarcode};{Code}"),
		};

		/// <summary>
		/// Scenario 1: pogrešan kod → server odbija → povratak na unos koda (ne na Start); ispravan kod bez ponovnog
		/// skeniranja → prijava → nema isteklih → ekran za ostavljanje → odustajanje → Start.
		/// </summary>
		[SkippableFact]
		public Task WrongCodeThenCorrect_WithoutRescan_LogsIn()
			=> KioskSession.RunAsync(nameof(WrongCodeThenCorrect_WithoutRescan_LogsIn), output, __manualLogin, async kiosk => {
				var serverMark = await kiosk.Server.MarkAsync();
				var startMark = kiosk.Log.Mark();

				await ScanToManualLoginAsync(kiosk);
				var mark = await EnterCodeAsync(kiosk, WrongCode);
				await WaitForReturnToCodeInputAsync(kiosk, mark);
				Assert.DoesNotContain(kiosk.Log.TransitionsAfter(mark), t => t.To == WorkflowState.Start);

				mark = await EnterCodeAsync(kiosk, Code);
				var prompt = await kiosk.WaitForStateAsync(WorkflowState.CourierDepositPrompt, mark, TimeSpan.FromSeconds(20));
				Assert.Equal("NoExpiredPackages", prompt.Trigger);
				Assert.Contains(kiosk.Log.TransitionsAfter(mark), t => t is { From: WorkflowState.CourierManualAuthenticating, Trigger: "CourierLoginSuccessful", To: WorkflowState.CourierFetchingExpiredPackages });
				await kiosk.Ui.WaitForPageAsync("CourierDepositPromptPage");
				await CancelDepositPromptAsync(kiosk);

				var calls = await kiosk.Server.GetBusinessCallsAsync(serverMark);
				var logins = calls.Where(c => c.Endpoint == "KurirAutorizacija").ToList();
				Assert.Equal([$"{ScannedBarcode};{WrongCode}", $"{ScannedBarcode};{Code}"], logins.Select(c => c.Query["QrKod"]));
				Assert.Equal([400, 200], logins.Select(c => c.StatusCode));
				Assert.Contains("Nepoznat kurir", logins[0].ResponseBody);
				var expired = Assert.Single(calls, c => c.Endpoint == "KurirPozicijeZaPraznjenje");
				Assert.True(expired.Seq > logins[1].Seq, $"Istekli paketi se traže tek posle uspešne prijave: {string.Join(", ", calls)}");
				Assert.Equal(CourierCode, expired.Query["SifraKurira"]);
				Assert.Empty(kiosk.Log.IgnoredTriggersAfter(startMark));
			});

		/// <summary>
		/// Scenario 2: posle neuspele prijave tajmer neaktivnosti ponovo teče — bez radnje, posle OperationTimeout (20 s)
		/// od povratka na unos koda Reset → Start.
		/// </summary>
		[SkippableFact]
		public Task WrongCode_NoActivity_TimesOutToStart()
			=> KioskSession.RunAsync(nameof(WrongCode_NoActivity_TimesOutToStart), output, __manualLogin, async kiosk => {
				await ScanToManualLoginAsync(kiosk);
				var mark = await EnterCodeAsync(kiosk, WrongCode);
				var back = await WaitForReturnToCodeInputAsync(kiosk, mark);
				var idle = Stopwatch.StartNew();

				var toStart = await kiosk.WaitForStateAsync(WorkflowState.Start, back.Line, TimeSpan.FromSeconds(30));
				kiosk.Trace($"unos koda → Start posle {idle.Elapsed.TotalSeconds:0.0} s bez radnje");
				Assert.Equal(WorkflowState.CourierManualLogin, toStart.From);
				Assert.Equal("Reset", toStart.Trigger);
				Assert.InRange(idle.Elapsed, __operationTimeout - TimeSpan.FromSeconds(1), __operationTimeout + TimeSpan.FromSeconds(5));
				Assert.NotEmpty(kiosk.Log.LinesAfter(back.Line, "OperationCountdownTimer: odbrojavanje zavrseno"));
				await kiosk.Ui.WaitForPageAsync("StartPage");
			});

		/// <summary>
		/// Scenario 3: Home dok server proverava ručno unet kod (odgovor kasni 3 s, uspešan) → kiosk ostaje zauzet, ni
		/// ponovno skeniranje ne radi ništa; kad odgovor stigne Reset → Start, a zakasneli CourierLoginSuccessful je
		/// ignorisan. Kontrola: nova prijava normalno prolazi — kontekst odbačene sesije se ne prenosi.
		/// </summary>
		[RealMouseFact]	// v4
		public Task HomeWhileAuthenticating_LateSuccessIgnored_NextLoginWorks()
			=> KioskSession.RunAsync(nameof(HomeWhileAuthenticating_LateSuccessIgnored_NextLoginWorks), output, __manualLogin, async kiosk => {
				var serverMark = await kiosk.Server.MarkAsync();

				await ScanToManualLoginAsync(kiosk);
				// Unapred: posle potvrde koda traženje Home dugmeta preko UIA je trajalo i ~3 s (Home stiže uz odgovor).
				var homeButton = await kiosk.Ui.GetClickablePointAsync("Header.Home");
				var serverDelay = TimeSpan.FromSeconds(3);	// zakasneli uspeh: kraće od RequestTimeout-a (5 s)
				await kiosk.Server.ArmFaultAsync("KurirAutorizacija", delayMs: (int)serverDelay.TotalMilliseconds);
				var mark = await EnterCodeAsync(kiosk, Code);
				var entered = kiosk.Log.TransitionsAfter(mark).First(t => t.To == WorkflowState.CourierManualAuthenticating);

				var homeMark = kiosk.Log.Mark();
				await kiosk.Ui.ClickAtAsync(homeButton, "Header.Home");
				await kiosk.Locker.ScanBarcodeAsync(ScannedBarcode);
				kiosk.Trace($"skeniran {ScannedBarcode} ponovo dok se čeka server");
				var toStart = await kiosk.WaitForStateAsync(WorkflowState.Start, homeMark, TimeSpan.FromSeconds(10));
				Assert.Equal(WorkflowState.CourierManualAuthenticating, toStart.From);
				Assert.Equal("Reset", toStart.Trigger);
				kiosk.AssertResetWaitedForServer(entered, homeMark, toStart, serverDelay);
				await kiosk.Ui.WaitForPageAsync("StartPage");
				await kiosk.AssertIgnoredTriggersAsync(homeMark, ("CourierLoginSuccessful", WorkflowState.Start));

				// Jedini prelaz posle Home-a je Reset: skeniranje dok se čekalo nije pokrenulo novu prijavu.
				Assert.Equal([WorkflowState.Start], kiosk.Log.TransitionsAfter(homeMark).Select(t => t.To));
				Assert.DoesNotContain(kiosk.Log.TransitionsAfter(mark), t => t.To is WorkflowState.CourierFetchingExpiredPackages or WorkflowState.CourierDepositPrompt);
				var calls = await kiosk.Server.GetBusinessCallsAsync(serverMark);
				Assert.Equal(200, Assert.Single(calls, c => c.Endpoint == "KurirAutorizacija").StatusCode);
				Assert.DoesNotContain(calls, c => c.Endpoint == "KurirPozicijeZaPraznjenje");

				// Kontrola: nova prijava (skeniranje + kod).
				var nextServerMark = await kiosk.Server.MarkAsync();
				await ScanToManualLoginAsync(kiosk);
				mark = await EnterCodeAsync(kiosk, Code);
				await kiosk.WaitForStateAsync(WorkflowState.CourierDepositPrompt, mark, TimeSpan.FromSeconds(20));
				await kiosk.Ui.WaitForPageAsync("CourierDepositPromptPage");

				var nextCalls = await kiosk.Server.GetBusinessCallsAsync(nextServerMark);
				var login = Assert.Single(nextCalls, c => c.Endpoint == "KurirAutorizacija");
				Assert.Equal((200, $"{ScannedBarcode};{Code}"), (login.StatusCode, login.Query["QrKod"]));
				Assert.Single(nextCalls, c => c.Endpoint == "KurirPozicijeZaPraznjenje");
			});

		/// <summary>
		/// S5: server ne odgovara na prvu prijavu (8 s, duže od RequestTimeout-a 5 s) → klijent odustaje i vraća na unos
		/// koda; drugi unos istog koda, bez skeniranja, prolazi. Zamena za ručnu proveru „uz ponovo pokrenut server".
		/// </summary>
		[SkippableFact]
		public Task ServerUnavailable_ThenSameCodeWithoutRescan_LogsIn()
			=> KioskSession.RunAsync(nameof(ServerUnavailable_ThenSameCodeWithoutRescan_LogsIn), output, __manualLogin, async kiosk => {
				var serverMark = await kiosk.Server.MarkAsync();

				await ScanToManualLoginAsync(kiosk);
				await kiosk.Server.ArmFaultAsync("KurirAutorizacija", delayMs: 8000, count: 1);
				var mark = await EnterCodeAsync(kiosk, Code);
				await WaitForReturnToCodeInputAsync(kiosk, mark, TimeSpan.FromSeconds(20));
				Assert.DoesNotContain(kiosk.Log.TransitionsAfter(mark), t => t.To == WorkflowState.Start);

				mark = await EnterCodeAsync(kiosk, Code);
				var prompt = await kiosk.WaitForStateAsync(WorkflowState.CourierDepositPrompt, mark, TimeSpan.FromSeconds(20));
				Assert.Equal("NoExpiredPackages", prompt.Trigger);
				await kiosk.Ui.WaitForPageAsync("CourierDepositPromptPage");

				// Server beleži zadržani zahtev tek kad ga obradi (8 s posle prijema) — može stići i posle drugog.
				var logins = await Poll.UntilAsync(async () => {
														var list = (await kiosk.Server.GetBusinessCallsAsync(serverMark)).Where(c => c.Endpoint == "KurirAutorizacija").ToList();
														return list.Count >= 2 ? list : null;
													},
													TimeSpan.FromSeconds(15), "oba KurirAutorizacija na serveru");
				Assert.Equal(2, logins.Count);
				Assert.All(logins, c => Assert.Equal($"{ScannedBarcode};{Code}", c.Query["QrKod"]));
				Assert.Single(await kiosk.Server.GetBusinessCallsAsync(serverMark), c => c.Endpoint == "KurirPozicijeZaPraznjenje");
			});

		/// <summary>Bilo koji barkod na Start-u → ekran za unos koda (barkod ide u kontekst, ne proverava se sam).</summary>
		private static async Task ScanToManualLoginAsync(KioskSession kiosk)
		{
			var reached = await kiosk.ScanAsync(ScannedBarcode, WorkflowState.CourierManualLogin);
			Assert.Equal(WorkflowState.Start, reached.From);
			Assert.Equal("CourierBarcodeScannedRequiringManualLogin", reached.Trigger);
			await kiosk.Ui.WaitForPageAsync("CourierManualLoginPage");
		}

		/// <summary>Kuca kod i potvrđuje → provera na serveru. Vraća kursor uzet pre unosa.</summary>
		private static async Task<int> EnterCodeAsync(KioskSession kiosk, string code)
		{
			var mark = kiosk.Log.Mark();
			await kiosk.Ui.TypeOnKeypadAsync(code);
			var entered = await kiosk.WaitForStateAsync(WorkflowState.CourierManualAuthenticating, mark);
			Assert.Equal("CourierManualCodeEntered", entered.Trigger);
			return mark;
		}

		/// <summary>
		/// Neuspela prijava → poruka pa povratak na unos koda. Stranica je ista, pa je znak spremnosti ponovna aktivacija
		/// tastature (OnEntry), a ne stranica.
		/// </summary>
		private static async Task<Transition> WaitForReturnToCodeInputAsync(KioskSession kiosk, int mark, TimeSpan? timeout = null)
		{
			var back = await kiosk.WaitForStateAsync(WorkflowState.CourierManualLogin, mark, timeout ?? KioskUi.DefaultTimeout);
			Assert.Equal(WorkflowState.CourierManualAuthenticating, back.From);
			Assert.Equal("CourierLoginFailed", back.Trigger);
			await kiosk.WaitForLogLineAsync("CourierManualLoginViewModel: aktivacija.", back.Line);
			return back;
		}

		private static async Task CancelDepositPromptAsync(KioskSession kiosk)
		{
			var mark = kiosk.Log.Mark();
			await kiosk.Ui.TapAsync("CourierDepositPromptPage.Cancel");
			await kiosk.WaitForStateAsync(WorkflowState.Start, mark);
		}
	}
}
