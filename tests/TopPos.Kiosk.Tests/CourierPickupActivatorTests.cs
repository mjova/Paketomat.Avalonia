using System.Net;
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
	/// v4: <see cref="CourierPickupActivator"/> (v2 ga pokriva samo E2E-om) — preuzimanje isteklih paketa: pitanje za sledeći
	/// paket, otvaranje vrata i čekanje zatvaranja (i podsetnik), skeniranje izvađenog paketa i potvrda serveru. Mašina ima
	/// handlere preuzimanja kao u <c>MainWindowViewModel.WireWorkflowMachine</c>; vrata i skener su lažni (host), server stub.
	/// </summary>
	public class CourierPickupActivatorTests : IDisposable
	{
		private readonly StubHttpServer _server = new();
		private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("toppos-pickup-");
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

		/// <summary>Server potvrđuje pražnjenje vrata iz zahteva (kao pravi server) ili vraća zadati odgovor.</summary>
		private void Server((HttpStatusCode, string)? confirm = null)
			=> _server.Handler = (requestLine, body) => requestLine.Contains("KurirPraznjenjePozicijePotvrda")
				? confirm ?? (HttpStatusCode.OK, $"{{\"ispraznjenBrojPozicije\":{System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("brojPozicije").GetInt32()}}}")
				: (HttpStatusCode.NotFound, "{}");

		/// <summary>Kurir prijavljen, istekli paketi u kontekstu, mašina na pitanju za prvi paket (prikazano).</summary>
		private async Task<(FakeWorkflowHost Host, CourierPickupPromptViewModel Prompt)> AtPrompt(int packages, TimeSpan? scanLimit = null)
		{
			var host = new FakeWorkflowHost(_server.BaseUrl, _dir.FullName) { BarcodeScanLimit = scanLimit ?? TimeSpan.FromSeconds(30) };
			_hosts.Add(host);
			var m = host.WorkflowMachine;
			var activator = new CourierPickupActivator(host);
			m.OnEntry(S.CourierPickupPrompt, activator.GoToCourierPickupPromptPage);
			m.OnEntry(S.CourierPackagePickup, activator.GoToCourierPackagePickupPage);
			m.OnEntry(S.CourierPackagePickupBarcodeScan, activator.GoToCourierPackagePickupBarcodeScanPage);
			await m.FireAsync(T.Reset);
			await Mk.CourierLogin(m);
			host.WorkflowContext.CourierCode = "K7";
			host.WorkflowContext.RemainingExpiredPackages = [.. Enumerable.Range(1, packages).Select(i => new Service.BarkodPozicija { Barkod = $"PKG{i}", BrojPozicije = i + 3 })];
			await m.FireAsync(T.HasExpiredPackages);
			return (host, Assert.IsType<CourierPickupPromptViewModel>(host.Pages[^1]));
		}

		/// <summary>Sa pitanja „Preuzmi“: vrata prvog paketa otvorena, praćenje zatvaranja pokrenuto.</summary>
		private static async Task<CourierPackagePickupViewModel> OpenDoor(FakeWorkflowHost host, CourierPickupPromptViewModel prompt)
		{
			await prompt.PickUpCommand.ExecuteAsync(null);
			Assert.Equal(S.CourierPackagePickup, host.WorkflowMachine.State);
			return Assert.IsType<CourierPackagePickupViewModel>(host.Pages[^1]);
		}

		private static (int Door, DoorStatusCode Target, Func<DoorStatusCode?, ValueTask> Notify) LastTrack(FakeWorkflowHost host)
			=> host.DoorTracks.Last();

		// ─── Pitanje za sledeći paket ─────────────────────────────────────────

		[Fact]
		public async Task Prompt_shows_next_package_and_count()
		{
			var (host, prompt) = await AtPrompt(packages: 3);

			Assert.Equal(3, prompt.RemainingExpiredPackagesCount);
			Assert.Equal("PKG1", host.WorkflowContext.CurrentPickupPackage!.Barkod);
			Assert.Equal(["PKG2", "PKG3"], host.WorkflowContext.RemainingExpiredPackages!.Select(p => p.Barkod));
		}

		[Fact]
		public async Task Log_out_on_prompt_returns_to_start()
		{
			var (host, prompt) = await AtPrompt(packages: 1);

			await prompt.CancelCommand.ExecuteAsync(null);

			Assert.Equal(S.Start, host.WorkflowMachine.State);
			Assert.Empty(host.OpenedDoors);
		}

		// ─── Vrata ────────────────────────────────────────────────────────────

		[Fact]
		public async Task Door_opens_and_closing_it_goes_to_barcode_scan()
		{
			var (host, prompt) = await AtPrompt(packages: 1);
			var page = await OpenDoor(host, prompt);

			Assert.Equal([4], host.OpenedDoors);
			Assert.False(page.ShowCloseDoorMessage);
			var track = LastTrack(host);
			Assert.Equal((4, DoorStatusCode.Closed), (track.Door, track.Target));

			await track.Notify(DoorStatusCode.Closed);

			Assert.Equal(S.CourierPackagePickupBarcodeScan, host.WorkflowMachine.State);
			var scan = Assert.IsType<CourierBarcodeScanViewModel>(host.Pages[^1]);
			Assert.Equal(("PKG1", true), (scan.Barcode, scan.PackagePickup));
		}

		[Theory]
		[InlineData(2, S.CourierPickupPrompt)]
		[InlineData(1, S.CourierDepositPrompt)]
		public async Task Door_that_does_not_open_skips_package(int packages, S next)
		{
			var (host, prompt) = await AtPrompt(packages);
			host.OpenDoorResult = _ => null;

			await prompt.PickUpCommand.ExecuteAsync(null);

			Assert.Equal(("General.DoorOpeningFailed.Courier.Template", MessageType.Error), Assert.Single(host.MessagesWithType));
			Assert.Equal((4, (DoorStatusCode?) null), Assert.Single(host.DoorFailures));
			Assert.Empty(host.DoorTracks);
			Assert.Equal(next, host.WorkflowMachine.State);
			Assert.True(host.WorkflowContext.ExpiredPickupIncomplete);
		}

		[Fact]
		public async Task Door_left_open_shows_reminder_then_closing_goes_to_scan()
		{
			var (host, prompt) = await AtPrompt(packages: 1);
			var page = await OpenDoor(host, prompt);

			await LastTrack(host).Notify(DoorStatusCode.Open);

			Assert.True(page.ShowCloseDoorMessage);
			Assert.Equal(S.CourierPackagePickup, host.WorkflowMachine.State);
			Assert.Equal(2, host.DoorTracks.Count);
			await LastTrack(host).Notify(DoorStatusCode.Closed);
			Assert.Equal(S.CourierPackagePickupBarcodeScan, host.WorkflowMachine.State);
		}

		[Fact]
		public async Task Door_still_open_after_reminder_reports_failure_and_returns_to_start()
		{
			var (host, prompt) = await AtPrompt(packages: 1);
			await OpenDoor(host, prompt);
			await LastTrack(host).Notify(DoorStatusCode.Open);

			await LastTrack(host).Notify(DoorStatusCode.Open);

			Assert.Equal((4, (DoorStatusCode?) DoorStatusCode.Open), Assert.Single(host.DoorFailures));
			Assert.Equal(S.Start, host.WorkflowMachine.State);
		}

		// ─── Skeniranje izvađenog paketa ──────────────────────────────────────

		private async Task<FakeWorkflowHost> AtScan(int packages, TimeSpan? scanLimit = null)
		{
			var (host, prompt) = await AtPrompt(packages, scanLimit);
			await OpenDoor(host, prompt);
			await LastTrack(host).Notify(DoorStatusCode.Closed);
			Assert.Equal(S.CourierPackagePickupBarcodeScan, host.WorkflowMachine.State);
			return host;
		}

		[Theory]
		[InlineData(2, S.CourierPickupPrompt)]
		[InlineData(1, S.CourierDepositPrompt)]
		public async Task Scanned_package_is_confirmed_and_flow_continues(int packages, S next)
		{
			Server();
			var host = await AtScan(packages);

			await host.ScanAsync("PKG1");
			await WaitUntil(() => host.WorkflowMachine.State == next);

			Assert.Empty(host.Messages);
			Assert.False(host.WorkflowContext.ExpiredPickupIncomplete);
			var (requestLine, body) = Assert.Single(_server.Requests);
			Assert.Contains("KurirPraznjenjePozicijePotvrda", requestLine);
			var request = System.Text.Json.JsonDocument.Parse(body).RootElement;
			Assert.Equal(("K7", "PKG1", 4), (request.GetProperty("sifraKurira").GetString(), request.GetProperty("barkod").GetString(),
											  request.GetProperty("brojPozicije").GetInt32()));
			if (next == S.CourierPickupPrompt) {
				Assert.Equal("PKG2", host.WorkflowContext.CurrentPickupPackage!.Barkod);
			}
		}

		[Fact]
		public async Task Wrong_barcode_twice_skips_package_without_confirmation()
		{
			Server();
			var host = await AtScan(packages: 1);

			await host.ScanAsync("DRUGI1");
			await WaitUntil(() => host.Messages.Count() == 1);
			Assert.Equal(S.CourierPackagePickupBarcodeScan, host.WorkflowMachine.State);
			await host.ScanAsync("DRUGI2");
			await WaitUntil(() => host.WorkflowMachine.State == S.CourierDepositPrompt);

			Assert.Equal(["CourierBarcodeScanPage.BarcodeMismatch.Template", "CourierBarcodeScanPage.BarcodeMismatch.Template"], host.Messages);
			Assert.Empty(_server.Requests);
			Assert.True(host.WorkflowContext.ExpiredPickupIncomplete);
		}

		/// <summary>Ispravan sken dok još stoji poruka o pogrešnom se obrađuje posle nje (ranije ga je tracker brisao).</summary>
		[Fact]
		public async Task Correct_scan_during_mismatch_message_is_confirmed_after_it()
		{
			Server();
			var host = await AtScan(packages: 1);
			var message = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			host.MessageShown = message.Task;

			await host.ScanAsync("DRUGI1");
			await WaitUntil(() => host.Messages.Any());
			await host.ScanAsync("PKG1");
			message.SetResult();

			await WaitUntil(() => host.WorkflowMachine.State == S.CourierDepositPrompt);
			Assert.Single(_server.Requests);
		}

		[Fact]
		public async Task Rejected_confirmation_shows_server_message_and_waits_for_next_scan()
		{
			Server(confirm: (HttpStatusCode.BadRequest, Error("Paket nije u pretincu")));
			var host = await AtScan(packages: 1);

			await host.ScanAsync("PKG1");
			await WaitUntil(() => host.Messages.Any());

			Assert.Equal(("Paket nije u pretincu", MessageType.Error), Assert.Single(host.MessagesWithType));
			Assert.Equal(S.CourierPackagePickupBarcodeScan, host.WorkflowMachine.State);
			Server();
			await host.ScanAsync("PKG1");
			await WaitUntil(() => host.WorkflowMachine.State == S.CourierDepositPrompt);
		}

		[Fact]
		public async Task No_scan_until_limit_returns_to_start()
		{
			var host = await AtScan(packages: 1, scanLimit: TimeSpan.FromSeconds(1));

			await WaitUntil(() => host.WorkflowMachine.State == S.Start);

			Assert.Empty(_server.Requests);
		}

		private static async Task WaitUntil(Func<bool> condition)
		{
			var deadline = DateTime.UtcNow.AddSeconds(10);
			while (!condition()) {
				Assert.True(DateTime.UtcNow < deadline, "uslov nije ispunjen za 10 s");
				await Task.Delay(20);
			}
		}
	}
}