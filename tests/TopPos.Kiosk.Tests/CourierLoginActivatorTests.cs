using System.Net;
using Flatsoft.Common.UI;
using TopPos.Kiosk.ViewModels;
using TopPos.Kiosk.Workflow;
using TopPos.Service;
using Xunit;
using S = TopPos.Kiosk.Workflow.WorkflowState;
using T = TopPos.Kiosk.Workflow.WorkflowTrigger;

namespace TopPos.Kiosk.Tests
{
	/// <summary>
	/// v4: <see cref="CourierLoginActivator"/> (v2 ga pokriva samo E2E-om) — provera kurira na serveru (skeniran barkod i
	/// ručni kod), istekli paketi, dostupnost sanduka i pitanje o preuzimanju isteklih paketa: poruka koju kurir vidi,
	/// sledeće stanje toka i kontekst. Aktivator radi sa lažnim hostom i stub serverom; mašina je dovedena u stanje bez
	/// OnEntry handlera, pa se handler zove ručno.
	/// </summary>
	public class CourierLoginActivatorTests : IDisposable
	{
		private const string Barcode = "QR-123";

		private readonly StubHttpServer _server = new();
		private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("toppos-courier-");
		private readonly List<FakeWorkflowHost> _hosts = [];

		public void Dispose()
		{
			foreach (var host in _hosts) {
				host.Dispose();
			}
			_server.Dispose();
			_dir.Delete(recursive: true);
		}

		private static string Error(string message) => $"{{\"responseStatus\":{{\"errorCode\":\"ArgumentException\",\"message\":\"{message}\"}}}}";

		private const string Availability = "{\"statusiPoVelicinamaPozicija\":[{\"velicina\":\"S\",\"slobodno\":3,\"zauzeto\":1,\"zaPreuzimanje\":0}]," +
											"\"posiljkePoPozicijama\":[]}";

		/// <summary>Server: prijava kurira, istekli paketi i dostupnost sanduka po putanji zahteva.</summary>
		private void Server((HttpStatusCode, string)? login = null, (HttpStatusCode, string)? expired = null, (HttpStatusCode, string)? availability = null)
			=> _server.Handler = (requestLine, _) =>
				requestLine.Contains("KurirAutorizacija") ? login ?? (HttpStatusCode.OK, "{\"sifraKurira\":\"K7\"}") :
				requestLine.Contains("KurirPozicijeZaPraznjenje") ? expired ?? (HttpStatusCode.OK, "{\"listaPaketa\":[]}") :
				requestLine.Contains("StatusPaketomata") ? availability ?? (HttpStatusCode.OK, Availability) :
				(HttpStatusCode.NotFound, "{}");

		private async Task<FakeWorkflowHost> Started(bool enableBox = false, bool confirmPickup = false)
		{
			var host = new FakeWorkflowHost(_server.BaseUrl, _dir.FullName, enableLockerBoxAvailabilityPage: enableBox, confirmPackagePickup: confirmPickup);
			_hosts.Add(host);
			await host.WorkflowMachine.FireAsync(T.Reset);
			return host;
		}

		// ─── Prijava skeniranim barkodom (CourierAuthenticating) ──────────────

		[Theory]
		[InlineData(false, false, S.CourierFetchingExpiredPackages)]
		[InlineData(true, false, S.CourierLockerBoxAvailability)]
		[InlineData(false, true, S.CourierPickupConfirmation)]
		public async Task Accepted_barcode_logs_in_and_routes(bool enableBox, bool confirmPickup, S next)
		{
			Server();
			var host = await Started(enableBox, confirmPickup);
			var m = host.WorkflowMachine;
			await m.FireAsync(m.CourierBarcodeScanned, Barcode);

			await new CourierLoginActivator(host).AuthenticateCourierAndRoute(Barcode);

			Assert.Empty(host.Messages);
			Assert.Equal(next, m.State);
			Assert.Equal("K7", host.WorkflowContext.CourierCode);
			Assert.Contains($"QrKod={Barcode}", _server.LastRequestLine);
		}

		[Fact]
		public async Task Rejected_barcode_shows_server_message_and_returns_to_start()
		{
			Server(login: (HttpStatusCode.BadRequest, Error("Nepoznat kurir")));
			var host = await Started();
			var m = host.WorkflowMachine;
			await m.FireAsync(m.CourierBarcodeScanned, Barcode);

			await new CourierLoginActivator(host).AuthenticateCourierAndRoute(Barcode);

			Assert.Equal(("Nepoznat kurir", MessageType.Error), Assert.Single(host.MessagesWithType));
			Assert.Equal(S.Start, m.State);
			Assert.Null(host.WorkflowContext.CourierCode);
		}

		[Fact]
		public async Task Unreachable_server_shows_default_message_and_returns_to_start()
		{
			Server(login: (HttpStatusCode.InternalServerError, "<html>500</html>"));
			var host = await Started();
			var m = host.WorkflowMachine;
			await m.FireAsync(m.CourierBarcodeScanned, Barcode);

			await new CourierLoginActivator(host).AuthenticateCourierAndRoute(Barcode);

			Assert.Equal("StartPage.CourierAuthenticationFailed.Default", Assert.Single(host.Messages));
			Assert.Equal(S.Start, m.State);
		}

		// ─── Ručna prijava (CourierManualLogin → CourierManualAuthenticating) ──

		/// <summary>
		/// Mašina sa handlerima ručne prijave kao u <c>MainWindowViewModel.WireWorkflowMachine</c> (barkod iz trigera u kontekst,
		/// prikaz unosa koda, provera koda iz trigera); barkod stiže sa Start-a parametrizovanim trigerom. Produkcijske
		/// registracije proverava E2E (<c>CourierManualLoginTests</c>, F1 korak 3).
		/// </summary>
		private static async Task<CourierManualLoginViewModel> ShowManualLogin(FakeWorkflowHost host)
		{
			var m = host.WorkflowMachine;
			var activator = new CourierLoginActivator(host);
			m.OnEntryFrom(S.CourierManualLogin, m.CourierBarcodeScannedRequiringManualLogin, barcode => {
				host.WorkflowContext.CourierLoginBarcode = barcode;
				return ValueTask.CompletedTask;
			});
			m.OnEntry(S.CourierManualLogin, activator.GoToCourierManualLoginPage);
			m.OnEntryFrom(S.CourierManualAuthenticating, m.CourierManualCodeEntered, activator.AuthenticateManualCourierAndRoute);
			await m.FireAsync(m.CourierBarcodeScannedRequiringManualLogin, Barcode);
			return Assert.IsType<CourierManualLoginViewModel>(host.Pages[^1]);
		}

		/// <summary>Cifre i potvrda kroz komandu tastature (kao dodir), pa čekanje da provera koda završi.</summary>
		private static async Task EnterCode(FakeWorkflowHost host, CourierManualLoginViewModel page, string code, S expected)
		{
			foreach (var key in code.Select(c => c.ToString()).Append("confirm")) {
				page.Keypad.KeyInputCommand.Execute(key);
			}
			await WaitUntil(() => host.WorkflowMachine.State == expected);
		}

		[Fact]
		public async Task Accepted_manual_code_sends_barcode_with_code_and_routes()
		{
			Server();
			var host = await Started();
			var page = await ShowManualLogin(host);

			await EnterCode(host, page, "4321", S.CourierFetchingExpiredPackages);

			Assert.Empty(host.Messages);
			Assert.Equal("K7", host.WorkflowContext.CourierCode);
			Assert.Contains($"QrKod={Barcode};4321", Assert.Single(_server.Requests).RequestLine);
		}

		[Fact]
		public async Task Rejected_manual_code_returns_to_code_input_and_next_code_is_sent()
		{
			Server(login: (HttpStatusCode.BadRequest, Error("Pogrešan kod")));
			var host = await Started();
			var page = await ShowManualLogin(host);

			await EnterCode(host, page, "4321", S.CourierManualLogin);

			Assert.Equal("Pogrešan kod", Assert.Single(host.Messages));
			Assert.Null(host.WorkflowContext.CourierCode);
			// Povratak prikazuje unos ponovo (prazan), a barkod ostaje u kontekstu za sledeći kod.
			Assert.Equal(2, host.Pages.Count(p => p == page));
			Server();
			await EnterCode(host, page, "5555", S.CourierFetchingExpiredPackages);
			Assert.Contains($"QrKod={Barcode};5555", _server.LastRequestLine);
		}

		// ─── Istekli paketi (CourierFetchingExpiredPackages) ──────────────────

		private static async Task<FakeWorkflowHost> LoggedIn(FakeWorkflowHost host)
		{
			await Mk.CourierLogin(host.WorkflowMachine);
			Assert.Equal(S.CourierFetchingExpiredPackages, host.WorkflowMachine.State);
			host.WorkflowContext.CourierCode = "K7";
			return host;
		}

		[Fact]
		public async Task Expired_packages_go_to_pickup()
		{
			Server(expired: (HttpStatusCode.OK, "{\"listaPaketa\":[{\"barkod\":\"PKG1\",\"brojPozicije\":4},{\"barkod\":\"PKG2\",\"brojPozicije\":9}]}"));
			var host = await LoggedIn(await Started());

			await new CourierLoginActivator(host).FetchExpiredPackagesAndRoute();

			Assert.Empty(host.Messages);
			Assert.Equal(S.CourierPickupPrompt, host.WorkflowMachine.State);
			Assert.Equal(["PKG1", "PKG2"], host.WorkflowContext.RemainingExpiredPackages!.Select(p => p.Barkod));
			Assert.Contains("SifraKurira=K7", _server.LastRequestLine);
		}

		[Fact]
		public async Task No_expired_packages_go_to_deposit()
		{
			Server();
			var host = await LoggedIn(await Started());

			await new CourierLoginActivator(host).FetchExpiredPackagesAndRoute();

			Assert.Empty(host.Messages);
			Assert.Equal(S.CourierDepositPrompt, host.WorkflowMachine.State);
			Assert.False(host.WorkflowContext.ExpiredPackagesPickedUp);
		}

		[Fact]
		public async Task Failed_expired_packages_request_shows_message_and_returns_to_start()
		{
			Server(expired: (HttpStatusCode.BadRequest, Error("Kurir nije aktivan")));
			var host = await LoggedIn(await Started());

			await new CourierLoginActivator(host).FetchExpiredPackagesAndRoute();

			Assert.Equal(("Kurir nije aktivan", MessageType.Error), Assert.Single(host.MessagesWithType));
			Assert.Equal(S.Start, host.WorkflowMachine.State);
		}

		// ─── Dostupnost sanduka i pitanje o preuzimanju ────────────────────────

		[Fact]
		public async Task Box_availability_is_shown_and_next_fetches_expired_packages()
		{
			Server();
			var host = await Started(enableBox: true);
			await Mk.CourierLogin(host.WorkflowMachine);
			Assert.Equal(S.CourierLockerBoxAvailability, host.WorkflowMachine.State);

			await new CourierLoginActivator(host).GoToLockerBoxAvailabilityPage();
			var page = Assert.IsType<CourierLockerBoxAvailabilityViewModel>(host.Pages[^1]);
			await WaitUntil(() => page.BoxSizeAvailabilityStatuses != null);

			var status = Assert.Single(page.BoxSizeAvailabilityStatuses!);
			Assert.Equal(("S", 3, 1, 0), (status.Velicina, status.Slobodno, status.Zauzeto, status.ZaPreuzimanje));
			await page.CompleteCommand.ExecuteAsync(null);
			Assert.Equal(S.CourierFetchingExpiredPackages, host.WorkflowMachine.State);
		}

		[Fact]
		public async Task Failed_box_availability_shows_failure_and_next_still_works()
		{
			Server(availability: (HttpStatusCode.BadRequest, Error("Greška")));
			var host = await Started(enableBox: true);
			await Mk.CourierLogin(host.WorkflowMachine);

			await new CourierLoginActivator(host).GoToLockerBoxAvailabilityPage();
			var page = Assert.IsType<CourierLockerBoxAvailabilityViewModel>(host.Pages[^1]);
			await WaitUntil(() => page.StatusMessage?.Contains("AvailabilityFetchFailed") == true);

			Assert.Null(page.BoxSizeAvailabilityStatuses);
			await page.CompleteCommand.ExecuteAsync(null);
			Assert.Equal(S.CourierFetchingExpiredPackages, host.WorkflowMachine.State);
		}

		[Theory]
		[InlineData(true, S.CourierFetchingExpiredPackages)]
		[InlineData(false, S.CourierDepositPrompt)]
		public async Task Pickup_question_routes_by_answer(bool confirm, S next)
		{
			var host = await Started(confirmPickup: true);
			await Mk.CourierLogin(host.WorkflowMachine);
			Assert.Equal(S.CourierPickupConfirmation, host.WorkflowMachine.State);

			await new CourierLoginActivator(host).GoToCourierPickupConfirmationPage();
			var page = Assert.IsType<CourierPickupConfirmationViewModel>(host.Pages[^1]);
			await (confirm ? page.ConfirmCommand : (IAsyncCommand) page.CancelCommand).ExecuteAsync(null);

			Assert.Equal(next, host.WorkflowMachine.State);
			Assert.Equal(!confirm, host.WorkflowContext.ExpiredPackagesPickedUp);
		}

		/// <summary>
		/// Odgovor za prethodnog kurira stigne kad je ekran već otvoren za sledećeg: ne prikazuje se, sledeći čeka svoj. Uzak
		/// razmak između provere aktivacije i upisa (Deactivate baš tada) test ne zahvata deterministički — njega štiti lock.
		/// </summary>
		[Fact]
		public async Task Late_box_availability_of_previous_activation_is_not_shown()
		{
			static StatusPaketomata Status(string size) => new() { StatusiPoVelicinamaPozicija = [new() { Velicina = size, Slobodno = 1 }] };
			var page = new CourierLockerBoxAvailabilityViewModel(TimeSpan.FromSeconds(20), new Flatsoft.Common.LanguageManager(null), null);
			var first = new TaskCompletionSource<StatusPaketomata?>(TaskCreationOptions.RunContinuationsAsynchronously);
			var second = new TaskCompletionSource<StatusPaketomata?>(TaskCreationOptions.RunContinuationsAsynchronously);
			page.FuncFetchLockerBoxAvailabilityInfo = _ => new ValueTask<StatusPaketomata?>(first.Task);
			page.Activate();
			page.Deactivate();
			page.FuncFetchLockerBoxAvailabilityInfo = _ => new ValueTask<StatusPaketomata?>(second.Task);
			page.Activate();

			first.SetResult(Status("S"));
			await Task.Delay(200);
			Assert.Null(page.BoxSizeAvailabilityStatuses);
			Assert.Contains("FetchingLockerBoxAvailability", page.StatusMessage);

			second.SetResult(Status("M"));
			await WaitUntil(() => page.BoxSizeAvailabilityStatuses != null);
			Assert.Equal("M", Assert.Single(page.BoxSizeAvailabilityStatuses!).Velicina);
		}

		/// <summary>Aktivacija bez prethodne deaktivacije ne pokazuje tabelu prethodne (nalaz iz v2; bez praznjenja u Activate pada).</summary>
		[Fact]
		public void Activation_starts_without_previous_table()
		{
			var page = new CourierLockerBoxAvailabilityViewModel(TimeSpan.FromSeconds(20), new Flatsoft.Common.LanguageManager(null), null) {
				BoxSizeAvailabilityStatuses = [new() { Velicina = "S", Slobodno = 1 }],
				FuncFetchLockerBoxAvailabilityInfo = _ => new ValueTask<StatusPaketomata?>(new TaskCompletionSource<StatusPaketomata?>().Task)
			};

			page.Activate();

			Assert.Null(page.BoxSizeAvailabilityStatuses);
			Assert.Contains("FetchingLockerBoxAvailability", page.StatusMessage);
		}

		private static async Task WaitUntil(Func<bool> condition)
		{
			var deadline = DateTime.UtcNow.AddSeconds(5);
			while (!condition()) {
				Assert.True(DateTime.UtcNow < deadline, "uslov nije ispunjen za 5 s");
				await Task.Delay(20);
			}
		}
	}
}