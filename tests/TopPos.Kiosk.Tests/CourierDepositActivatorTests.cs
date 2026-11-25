using System.Net;
using System.Text.Json;
using Flatsoft.Common.UI;
using TopPos.Kiosk.ViewModels;
using TopPos.Kiosk.Workflow;
using TopPos.LockController;
using Xunit;
using S = TopPos.Kiosk.Workflow.WorkflowState;
using T = TopPos.Kiosk.Workflow.WorkflowTrigger;

namespace TopPos.Kiosk.Tests
{
	/// <summary>
	/// v4: <see cref="CourierDepositActivator"/> i <see cref="DepositConfirmationFailedActivator"/> za kurira (v2 ih pokriva samo
	/// E2E-om) — pitanje za ostavljanje, skeniranje i dodela vrata, vrata (zatvaranje, odjava i veće sanduče dok su otvorena),
	/// potvrda serveru (automatska i sa pitanjem) i ekran neuspele potvrde (ponovi, preuzmi paket, ostavi, istek). Mašina ima
	/// handlere ostavljanja kao u <c>MainWindowViewModel.WireWorkflowMachine</c>; vrata i skener su lažni (host), server stub.
	/// </summary>
	public class CourierDepositActivatorTests : IDisposable
	{
		private const int Door = 7;

		private readonly StubHttpServer _server = new();
		private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("toppos-deposit-");
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

		private static readonly (HttpStatusCode, string) Confirmed = (HttpStatusCode.OK, $"{{\"popunjenBrojPozicije\":{Door}}}");

		/// <summary>Server: dodela vrata, potvrda ostavljanja i veće sanduče (potvrda pre dodele — isti prefiks putanje).</summary>
		private void Server((HttpStatusCode, string)? door = null, Func<(HttpStatusCode, string)>? confirm = null)
			=> _server.Handler = (requestLine, _) =>
				requestLine.Contains("KurirPunjenjePozicijePotvrda") ? confirm?.Invoke() ?? Confirmed :
				requestLine.Contains("KurirPunjenjePozicije") ? door ?? (HttpStatusCode.OK, $"{{\"brojPozicije\":{Door}}}") :
				requestLine.Contains("KurirPromenaVelicinePosiljke") ? (HttpStatusCode.OK, "{\"novaVelicina\":\"L\"}") :
				(HttpStatusCode.NotFound, "{}");

		private IEnumerable<(string RequestLine, string Body)> Confirmations
			=> _server.Requests.Where(r => r.RequestLine.Contains("KurirPunjenjePozicijePotvrda"));

		/// <summary>Kurir prijavljen bez isteklih paketa: mašina na pitanju za ostavljanje (prikazano).</summary>
		private async Task<(FakeWorkflowHost Host, CourierDepositPromptViewModel Prompt)> AtPrompt(bool confirmDeposition = false, int operationTimeout = 60_000)
		{
			var host = new FakeWorkflowHost(_server.BaseUrl, _dir.FullName);
			host.Configuration.General.ConfirmPackageDeposition = confirmDeposition;
			host.Configuration.General.OperationTimeout = operationTimeout;
			_hosts.Add(host);
			var m = host.WorkflowMachine;
			var deposit = new CourierDepositActivator(host);
			var failed = new DepositConfirmationFailedActivator(host);
			m.OnEntry(S.CourierDepositPrompt, deposit.GoToCourierDepositPromptPage);
			m.OnEntry(S.CourierPackageDepositBarcodeScan, deposit.GoToCourierPackageDepositBarcodeScanPage);
			m.OnEntryFrom(S.CourierDepositBarcodeValidating, m.DepositBarcodeScanned, deposit.RequestCourierDepositDoorAndRoute);
			m.OnEntryFrom(S.CourierPackageDeposit, m.DepositBarcodeAccepted, (barcode, doorNumber) => {
				host.WorkflowContext.DepositBarcode = barcode;
				host.WorkflowContext.DepositDoorNumber = doorNumber;
				return ValueTask.CompletedTask;
			});
			m.OnEntry(S.CourierPackageDeposit, deposit.GoToCourierPackageDepositPage);
			m.OnEntry(S.CourierDepositConfirmation, deposit.GoToCourierDepositConfirmationPage);
			m.OnEntryFrom(S.CourierDepositConfirmationFailed, m.DepositConfirmationFailed, failed.GoToCourierDepositConfirmationFailedPage);
			await m.FireAsync(T.Reset);
			await Mk.CourierLogin(m);
			host.WorkflowContext.CourierCode = "K7";
			await m.FireAsync(T.NoExpiredPackages);
			return (host, Assert.IsType<CourierDepositPromptViewModel>(host.Pages[^1]));
		}

		/// <summary>Sa pitanja „Ostavi paket“: ekran za skeniranje.</summary>
		private async Task<FakeWorkflowHost> AtScan(bool confirmDeposition = false, int operationTimeout = 60_000)
		{
			var (host, prompt) = await AtPrompt(confirmDeposition, operationTimeout);
			await prompt.DepositCommand.ExecuteAsync(null);
			Assert.Equal(S.CourierPackageDepositBarcodeScan, host.WorkflowMachine.State);
			return host;
		}

		/// <summary>Skeniran paket, server dodelio vrata, vrata otvorena.</summary>
		private async Task<(FakeWorkflowHost Host, CourierPackageDepositViewModel Page)> AtOpenDoor(bool confirmDeposition = false, int operationTimeout = 60_000)
		{
			var host = await AtScan(confirmDeposition, operationTimeout);
			await host.ScanAsync("PKG9");
			// stanje se menja pre OnEntry-ja: čeka se i stranica vrata i pokrenuto praćenje
			await WaitUntil(() => host.Pages[^1] is CourierPackageDepositViewModel && !host.DoorTracks.IsEmpty);
			Assert.Equal(S.CourierPackageDeposit, host.WorkflowMachine.State);
			return (host, Assert.IsType<CourierPackageDepositViewModel>(host.Pages[^1]));
		}

		private static Task CloseDoor(FakeWorkflowHost host, DoorStatusCode? status = null)
			=> host.DoorTracks.Last().Notify(status ?? DoorStatusCode.Closed).AsTask();

		// ─── Pitanje i skeniranje ─────────────────────────────────────────────

		[Fact]
		public async Task Prompt_without_expired_packages_says_so_and_logout_returns_to_start()
		{
			var (host, prompt) = await AtPrompt();

			Assert.True(prompt.NoExpiredPackages);
			await ((IAsyncCommand) prompt.CancelCommand).ExecuteAsync(null);
			Assert.Equal(S.Start, host.WorkflowMachine.State);
		}

		[Fact]
		public async Task Scanned_package_gets_door_which_is_opened()
		{
			Server();
			var (host, page) = await AtOpenDoor();

			Assert.Equal("PKG9", page.Barcode);
			Assert.Equal([Door], host.OpenedDoors);
			Assert.Equal((Door, DoorStatusCode.Closed), (host.DoorTracks.Last().Door, host.DoorTracks.Last().Target));
			Assert.Contains("SifraKurira=K7&Barkod=PKG9", Assert.Single(_server.Requests).RequestLine);
			Assert.Equal(("PKG9", Door), (host.WorkflowContext.DepositBarcode, host.WorkflowContext.DepositDoorNumber));
		}

		[Fact]
		public async Task Rejected_package_shows_server_message_and_scan_continues()
		{
			Server(door: (HttpStatusCode.BadRequest, Error("Paket nije najavljen")));
			var host = await AtScan();

			await host.ScanAsync("PKG9");
			await WaitUntil(() => host.Messages.Any() && host.WorkflowMachine.State == S.CourierPackageDepositBarcodeScan);

			Assert.Equal(("Paket nije najavljen", MessageType.Error), Assert.Single(host.MessagesWithType));
			Assert.Empty(host.OpenedDoors);
			Server();
			await host.ScanAsync("PKG10");
			await WaitUntil(() => host.WorkflowMachine.State == S.CourierPackageDeposit);
		}

		[Fact]
		public async Task Log_out_on_scan_returns_to_start()
		{
			var host = await AtScan();
			var scan = Assert.IsType<CourierBarcodeScanViewModel>(host.Pages[^1]);
			Assert.True(scan.PackageDeposit);

			await ((IAsyncCommand) scan.CancelCommand).ExecuteAsync(null);

			Assert.Equal(S.Start, host.WorkflowMachine.State);
		}

		// ─── Vrata i potvrda ──────────────────────────────────────────────────

		[Fact]
		public async Task Closed_door_confirms_deposit_and_returns_to_scan()
		{
			Server();
			var (host, _) = await AtOpenDoor();

			await CloseDoor(host);

			Assert.Equal(S.CourierPackageDepositBarcodeScan, host.WorkflowMachine.State);
			var body = JsonDocument.Parse(Assert.Single(Confirmations).Body).RootElement;
			Assert.Equal(("K7", "PKG9", Door), (body.GetProperty("sifraKurira").GetString(), body.GetProperty("barkod").GetString(),
												body.GetProperty("brojPozicije").GetInt32()));
			Assert.IsType<CourierBarcodeScanViewModel>(host.Pages[^1]);
		}

		[Fact]
		public async Task Door_that_does_not_open_is_reported_and_scan_continues()
		{
			Server();
			var host = await AtScan();
			host.OpenDoorResult = _ => null;

			await host.ScanAsync("PKG9");
			await WaitUntil(() => host.DoorFailures.Any() && host.WorkflowMachine.State == S.CourierPackageDepositBarcodeScan);

			Assert.Equal([Door, Door], host.OpenedDoors);   // drugi pokušaj posle 5 s
			Assert.Equal("General.DoorOpeningFailed.Courier.Template", Assert.Single(host.Messages));
			Assert.Empty(Confirmations);
		}

		[Fact]
		public async Task Door_left_open_is_reported_and_returns_to_start()
		{
			Server();
			var (host, _) = await AtOpenDoor();

			await CloseDoor(host, DoorStatusCode.Open);

			Assert.Equal((Door, (DoorStatusCode?) DoorStatusCode.Open), Assert.Single(host.DoorFailures));
			Assert.Equal(S.Start, host.WorkflowMachine.State);
			Assert.Empty(Confirmations);
		}

		[Fact]
		public async Task Log_out_while_door_is_open_ends_session_when_door_closes_without_confirmation()
		{
			Server();
			var (host, page) = await AtOpenDoor();

			await ((IAsyncCommand) page.CancelCommand).ExecuteAsync(null);
			Assert.Equal(S.CourierPackageDeposit, host.WorkflowMachine.State);
			Assert.Equal("CourierPackageDepositPage.CancelRequestPending.Template", Assert.Single(host.Messages));
			await CloseDoor(host);

			Assert.Equal(S.Start, host.WorkflowMachine.State);
			Assert.Empty(Confirmations);
		}

		[Fact]
		public async Task Larger_box_requested_while_door_is_open_is_sent_when_door_closes()
		{
			Server();
			var (host, page) = await AtOpenDoor();

			await page.ChangeBoxSizeCommand.ExecuteAsync(null);
			Assert.Equal(S.CourierPackageDeposit, host.WorkflowMachine.State);
			await CloseDoor(host);

			Assert.Equal(S.CourierPackageDepositBarcodeScan, host.WorkflowMachine.State);
			Assert.Contains(_server.Requests, r => r.RequestLine.Contains("KurirPromenaVelicinePosiljke"));
			Assert.Empty(Confirmations);
			Assert.Equal(["CourierPackageDepositPage.PackageSizeChangeRequestPending.Template", "CourierPackageDepositPage.NewPackageSize.Template"],
						 host.Messages);
		}

		[Theory]
		[InlineData(true, 1)]
		[InlineData(false, 0)]
		public async Task Explicit_confirmation_sends_only_on_yes(bool yes, int confirmations)
		{
			Server();
			var (host, _) = await AtOpenDoor(confirmDeposition: true);
			await CloseDoor(host);
			var question = Assert.IsType<CourierDepositConfirmationViewModel>(host.Pages[^1]);

			await (yes ? question.ConfirmCommand : (IAsyncCommand) question.CancelCommand).ExecuteAsync(null);

			Assert.Equal(S.CourierPackageDepositBarcodeScan, host.WorkflowMachine.State);
			Assert.Equal(confirmations, Confirmations.Count());
		}

		[Fact]
		public async Task Unanswered_explicit_confirmation_is_sent_when_time_runs_out()
		{
			Server();
			var (host, _) = await AtOpenDoor(confirmDeposition: true, operationTimeout: 300);

			await CloseDoor(host);
			await WaitUntil(() => host.WorkflowMachine.State == S.CourierPackageDepositBarcodeScan);

			Assert.Single(Confirmations);
		}

		// ─── Neuspela potvrda ─────────────────────────────────────────────────

		private async Task<(FakeWorkflowHost Host, DepositConfirmationFailedViewModel Page)> AtFailed((HttpStatusCode, string) confirm, int operationTimeout = 60_000)
		{
			Server(confirm: () => confirm);
			var (host, _) = await AtOpenDoor(operationTimeout: operationTimeout);
			await CloseDoor(host);
			Assert.Equal(S.CourierDepositConfirmationFailed, host.WorkflowMachine.State);
			return (host, Assert.IsType<DepositConfirmationFailedViewModel>(host.Pages[^1]));
		}

		[Fact]
		public async Task Rejected_confirmation_is_not_retried_and_offers_only_package_return()
		{
			var (host, page) = await AtFailed((HttpStatusCode.BadRequest, Error("Pozicija nije rezervisana")));

			Assert.Single(Confirmations);
			Assert.Equal((false, true, false), (page.CanRetry, page.CanReturnPackage, page.CanLeavePackage));
		}

		[Fact]
		public async Task Returned_package_reopens_the_door_and_scan_continues()
		{
			var (host, page) = await AtFailed((HttpStatusCode.BadRequest, Error("Pozicija nije rezervisana")));

			var returning = ((IAsyncCommand) page.ReturnPackageCommand).ExecuteAsync(null);
			await WaitUntil(() => host.DoorTracks.Count == 2);   // praćenje povrata (prvo je bilo zatvaranje posle ostavljanja)
			var track = host.DoorTracks.Last();
			Assert.Equal((Door, DoorStatusCode.Closed), (track.Door, track.Target));
			Assert.Equal(2, host.OpenedDoors.Count);
			await track.Notify(DoorStatusCode.Closed);
			await returning.WaitAsync(TimeSpan.FromSeconds(10));

			Assert.Equal(S.CourierPackageDepositBarcodeScan, host.WorkflowMachine.State);
			Assert.Single(Confirmations);
			Assert.Contains("CourierPackagePickupPage.Instructions", host.Messages);
		}

		[Fact]
		public async Task Unknown_outcome_is_sent_twice_and_package_can_be_left()
		{
			var (host, page) = await AtFailed((HttpStatusCode.InternalServerError, "<html>500</html>"));

			Assert.Equal(2, Confirmations.Count());
			Assert.Equal((true, false, true), (page.CanRetry, page.CanReturnPackage, page.CanLeavePackage));
			await ((IAsyncCommand) page.LeavePackageCommand).ExecuteAsync(null);
			Assert.Equal(S.CourierPackageDepositBarcodeScan, host.WorkflowMachine.State);
		}

		[Fact]
		public async Task Successful_retry_continues_scan()
		{
			var (host, page) = await AtFailed((HttpStatusCode.InternalServerError, "<html>500</html>"));
			Server();

			await ((IAsyncCommand) page.RetryCommand).ExecuteAsync(null);

			Assert.Equal(S.CourierPackageDepositBarcodeScan, host.WorkflowMachine.State);
			Assert.Equal(3, Confirmations.Count());
		}

		[Fact]
		public async Task Unanswered_failure_screen_leaves_package_and_returns_to_start()
		{
			var (host, _) = await AtFailed((HttpStatusCode.InternalServerError, "<html>500</html>"), operationTimeout: 300);

			await WaitUntil(() => host.WorkflowMachine.State == S.Start);

			Assert.Equal(2, Confirmations.Count());
		}

		private static async Task WaitUntil(Func<bool> condition)
		{
			var deadline = DateTime.UtcNow.AddSeconds(15);
			while (!condition()) {
				Assert.True(DateTime.UtcNow < deadline, "uslov nije ispunjen za 15 s");
				await Task.Delay(20);
			}
		}
	}
}