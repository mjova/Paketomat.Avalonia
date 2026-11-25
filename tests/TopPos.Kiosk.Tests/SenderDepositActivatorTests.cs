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
	/// v4: <see cref="SenderDepositActivator"/> i grana pošiljaoca u <see cref="DepositConfirmationFailedActivator"/> (v2 ih pokriva
	/// samo E2E-om) — skeniranje i dodela vrata sa šifrom pošiljaoca iz konfiguracije, zatvaranje vrata sa potvrdom pa Start,
	/// odustajanje i veće sanduče dok su vrata otvorena, neuspela potvrda (svaki izlaz vodi na Start). Mašina ima handlere kao
	/// u <c>MainWindowViewModel.WireWorkflowMachine</c>; vrata i skener su lažni (host), server stub.
	/// </summary>
	public class SenderDepositActivatorTests : IDisposable
	{
		private const int Door = 7;
		private const string SenderCode = "30";

		private readonly StubHttpServer _server = new();
		private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("toppos-sender-");
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

		/// <summary>Server: dodela vrata, potvrda ostavljanja i veće sanduče (potvrda pre dodele — isti prefiks putanje).</summary>
		private void Server((HttpStatusCode, string)? door = null, (HttpStatusCode, string)? confirm = null)
			=> _server.Handler = (requestLine, _) =>
				requestLine.Contains("KurirPunjenjePozicijePotvrda") ? confirm ?? (HttpStatusCode.OK, $"{{\"popunjenBrojPozicije\":{Door}}}") :
				requestLine.Contains("KurirPunjenjePozicije") ? door ?? (HttpStatusCode.OK, $"{{\"brojPozicije\":{Door}}}") :
				requestLine.Contains("KurirPromenaVelicinePosiljke") ? (HttpStatusCode.OK, "{\"novaVelicina\":\"L\"}") :
				(HttpStatusCode.NotFound, "{}");

		private IEnumerable<(string RequestLine, string Body)> Confirmations
			=> _server.Requests.Where(r => r.RequestLine.Contains("KurirPunjenjePozicijePotvrda"));

		/// <summary>Sa Start-a „Ostavi paket“: ekran za skeniranje (prikazan).</summary>
		private async Task<(FakeWorkflowHost Host, SenderBarcodeScanViewModel Scan)> AtScan()
		{
			var host = new FakeWorkflowHost(_server.BaseUrl, _dir.FullName);
			host.Configuration.General.SenderDepositCourierCode = SenderCode;
			_hosts.Add(host);
			var m = host.WorkflowMachine;
			var sender = new SenderDepositActivator(host);
			var failed = new DepositConfirmationFailedActivator(host);
			m.OnEntry(S.SenderDepositBarcodeScan, sender.GoToSenderDepositBarcodeScanPage);
			m.OnEntryFrom(S.SenderDepositBarcodeValidating, m.SenderBarcodeScanned, sender.RequestSenderDepositDoorAndRoute);
			m.OnEntryFrom(S.SenderPackageDeposit, m.SenderBarcodeAccepted, (barcode, doorNumber) => {
				host.WorkflowContext.DepositBarcode = barcode;
				host.WorkflowContext.DepositDoorNumber = doorNumber;
				return ValueTask.CompletedTask;
			});
			m.OnEntry(S.SenderPackageDeposit, sender.GoToSenderPackageDepositPage);
			m.OnEntryFrom(S.SenderDepositConfirmationFailed, m.SenderConfirmationFailed, failed.GoToSenderDepositConfirmationFailedPage);
			await m.FireAsync(T.Reset);
			await m.FireAsync(T.SenderDepositRequested);
			Assert.Equal(S.SenderDepositBarcodeScan, m.State);
			return (host, Assert.IsType<SenderBarcodeScanViewModel>(host.Pages[^1]));
		}

		private async Task<(FakeWorkflowHost Host, SenderPackageDepositViewModel Page)> AtOpenDoor()
		{
			var (host, _) = await AtScan();
			await host.ScanAsync("PKG9");
			// stanje se menja pre OnEntry-ja: čeka se i stranica vrata i pokrenuto praćenje
			await WaitUntil(() => host.Pages[^1] is SenderPackageDepositViewModel && !host.DoorTracks.IsEmpty);
			Assert.Equal(S.SenderPackageDeposit, host.WorkflowMachine.State);
			return (host, Assert.IsType<SenderPackageDepositViewModel>(host.Pages[^1]));
		}

		private static Task CloseDoor(FakeWorkflowHost host, DoorStatusCode? status = null)
			=> host.DoorTracks.Last().Notify(status ?? DoorStatusCode.Closed).AsTask();

		[Fact]
		public async Task Deposit_uses_sender_code_and_closed_door_confirms_then_start()
		{
			Server();
			var (host, page) = await AtOpenDoor();

			Assert.Equal("PKG9", page.Barcode);
			Assert.Equal([Door], host.OpenedDoors);
			Assert.Contains($"SifraKurira={SenderCode}&Barkod=PKG9", _server.Requests[0].RequestLine);
			await CloseDoor(host);

			Assert.Equal(S.Start, host.WorkflowMachine.State);
			var body = JsonDocument.Parse(Assert.Single(Confirmations).Body).RootElement;
			Assert.Equal((SenderCode, "PKG9", Door), (body.GetProperty("sifraKurira").GetString(), body.GetProperty("barkod").GetString(),
													  body.GetProperty("brojPozicije").GetInt32()));
		}

		[Fact]
		public async Task Rejected_package_shows_server_message_and_scan_continues()
		{
			Server(door: (HttpStatusCode.BadRequest, Error("Paket nije najavljen")));
			var (host, _) = await AtScan();

			await host.ScanAsync("PKG9");
			await WaitUntil(() => host.Messages.Any() && host.WorkflowMachine.State == S.SenderDepositBarcodeScan);

			Assert.Equal(("Paket nije najavljen", MessageType.Error), Assert.Single(host.MessagesWithType));
			Assert.Empty(host.OpenedDoors);
		}

		[Fact]
		public async Task Cancel_on_scan_returns_to_start()
		{
			var (host, scan) = await AtScan();

			await ((IAsyncCommand) scan.CancelCommand).ExecuteAsync(null);

			Assert.Equal(S.Start, host.WorkflowMachine.State);
		}

		[Fact]
		public async Task Cancel_while_door_is_open_returns_to_start_when_door_closes_without_confirmation()
		{
			Server();
			var (host, page) = await AtOpenDoor();

			await ((IAsyncCommand) page.CancelCommand).ExecuteAsync(null);
			Assert.Equal(S.SenderPackageDeposit, host.WorkflowMachine.State);
			Assert.Equal(["General.DoorOpening.Template", "SenderPackageDepositPage.CancelRequestPending.Template"], host.Messages);
			await CloseDoor(host);

			Assert.Equal(S.Start, host.WorkflowMachine.State);
			Assert.Empty(Confirmations);
		}

		/// <summary>v4: odustajanje odlučuje odmah — vrata zatvorena odmah posle klika (ranije u 200 ms odziva) ne šalju potvrdu.</summary>
		[Fact]
		public async Task Cancel_right_before_door_closes_wins_over_confirmation()
		{
			Server();
			var (host, page) = await AtOpenDoor();

			var cancel = ((IAsyncCommand) page.CancelCommand).ExecuteAsync(null);
			await CloseDoor(host);
			await cancel;

			Assert.Equal(S.Start, host.WorkflowMachine.State);
			Assert.Empty(Confirmations);
		}

		[Fact]
		public async Task Larger_box_requested_while_door_is_open_returns_to_scan_when_door_closes()
		{
			Server();
			var (host, page) = await AtOpenDoor();

			await page.ChangeBoxSizeCommand.ExecuteAsync(null);
			await CloseDoor(host);

			Assert.Equal(S.SenderDepositBarcodeScan, host.WorkflowMachine.State);
			Assert.Contains(_server.Requests, r => r.RequestLine.Contains("KurirPromenaVelicinePosiljke"));
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
		public async Task Rejected_confirmation_offers_package_return_which_ends_at_start()
		{
			Server(confirm: (HttpStatusCode.BadRequest, Error("Pozicija nije rezervisana")));
			var (host, _) = await AtOpenDoor();
			await CloseDoor(host);
			Assert.Equal(S.SenderDepositConfirmationFailed, host.WorkflowMachine.State);
			var failed = Assert.IsType<DepositConfirmationFailedViewModel>(host.Pages[^1]);
			Assert.Equal((false, true, false), (failed.CanRetry, failed.CanReturnPackage, failed.CanLeavePackage));

			var returning = ((IAsyncCommand) failed.ReturnPackageCommand).ExecuteAsync(null);
			await WaitUntil(() => host.DoorTracks.Count == 2);
			await host.DoorTracks.Last().Notify(DoorStatusCode.Closed);
			await returning.WaitAsync(TimeSpan.FromSeconds(10));

			Assert.Equal(S.Start, host.WorkflowMachine.State);
			Assert.Equal(2, host.OpenedDoors.Count);
			Assert.Single(Confirmations);
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