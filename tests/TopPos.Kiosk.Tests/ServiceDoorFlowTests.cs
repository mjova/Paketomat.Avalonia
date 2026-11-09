using System.IO;
using System.Text.Json;
using TopPos.Kiosk.Workflow;
using Xunit;
using S = TopPos.Kiosk.Workflow.WorkflowState;
using T = TopPos.Kiosk.Workflow.WorkflowTrigger;

namespace TopPos.Kiosk.Tests
{
	public class ServiceDoorFlowTests
	{
		[Fact]
		public async Task ServiceBarcode_in_Start_goes_to_ServiceDoorOpening()
		{
			var m = await Mk.Started(new WorkflowContext());
			await m.FireIfInStateAsync(S.Start, T.ServiceBarcodeScanned);
			Assert.Equal(S.ServiceDoorOpening, m.State);
		}

		[Fact]
		public async Task ServiceDoorHandled_goes_back_to_Start()
		{
			var m = await Mk.Started(new WorkflowContext());
			await m.FireAsync(T.ServiceBarcodeScanned);
			await m.FireAsync(T.ServiceDoorHandled);
			Assert.Equal(S.Start, m.State);
		}

		[Fact]
		public async Task Reset_during_ServiceDoorOpening_goes_to_Start()
		{
			var m = await Mk.Started(new WorkflowContext());
			await m.FireAsync(T.ServiceBarcodeScanned);
			await m.FireAsync(T.Reset);
			Assert.Equal(S.Start, m.State);
		}

		[Theory]
		[InlineData(T.RecipientPickupRequested)]
		[InlineData(T.SenderDepositRequested)]
		public async Task Start_buttons_are_ignored_during_ServiceDoorOpening(T trigger)
		{
			var m = await Mk.Started(new WorkflowContext());
			await m.FireAsync(T.ServiceBarcodeScanned);
			await m.FireIfInStateAsync(S.Start, trigger);
			Assert.Equal(S.ServiceDoorOpening, m.State);
		}

		[Fact]
		public async Task ServiceBarcode_during_CourierAuthenticating_is_dropped()
		{
			// Start ekran (i njegov barkod handler) ostaje prikazan tokom prijave kurira.
			var m = await Mk.Started(new WorkflowContext());
			await m.FireAsync(m.CourierBarcodeScanned, "QR-123");
			await m.FireIfInStateAsync(S.Start, T.ServiceBarcodeScanned);
			Assert.Equal(S.CourierAuthenticating, m.State);
		}

		[Fact]
		public async Task ServiceDoorHandled_does_not_abort_a_flow_started_meanwhile()
		{
			// Ranije je neuspeh otvaranja fire-ovao Reset na bilo koje trenutno stanje i prekidao kupčev tok.
			var m = await Mk.Started(new WorkflowContext());
			await m.FireAsync(T.RecipientPickupRequested);
			await m.FireAsync(T.ServiceDoorHandled);
			Assert.Equal(S.RecipientCodeInput, m.State);
		}

		[Fact]
		public async Task ServiceDoorHandled_in_Start_is_ignored()
		{
			var m = await Mk.Started(new WorkflowContext());
			await m.FireAsync(T.ServiceDoorHandled);
			Assert.Equal(S.Start, m.State);
		}

		[Fact]
		public async Task Opening_with_handler_always_returns_to_Start()
		{
			// Kao OpenServiceDoor: OnEntry na kraju (i posle neuspeha) fire-uje ServiceDoorHandled.
			var m = await Mk.Started(new WorkflowContext());
			m.OnEntry(S.ServiceDoorOpening, async () => await m.FireAsync(T.ServiceDoorHandled));
			await m.FireIfInStateAsync(S.Start, T.ServiceBarcodeScanned);
			Assert.Equal(S.Start, m.State);
		}

		[Fact]
		public async Task Reset_while_opening_is_pending_returns_to_Start_and_late_Handled_is_ignored()
		{
			// OnEntry čeka kontroler; Reset (npr. povratak operativnosti) se naređa ispred zakasnelog ServiceDoorHandled.
			var controller = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var m = await Mk.Started(new WorkflowContext());
			m.OnEntry(S.ServiceDoorOpening, async () => {
				await controller.Task;
				await m.FireAsync(T.ServiceDoorHandled);
			});
			var scan = m.FireIfInStateAsync(S.Start, T.ServiceBarcodeScanned);
			Assert.Equal(S.ServiceDoorOpening, m.State);

			await m.FireAsync(T.Reset);
			controller.SetResult();
			await scan;
			Assert.Equal(S.Start, m.State);

			// Kiosk je posle toga normalno slobodan — klik sa Start ekrana pokreće kupčev tok.
			await m.FireIfInStateAsync(S.Start, T.RecipientPickupRequested);
			Assert.Equal(S.RecipientCodeInput, m.State);
		}

		[Theory]
		[InlineData(S.ServiceDoorOpening, "Service")]
		[InlineData(S.CourierAuthenticating, "Courier")]
		[InlineData(S.CourierManualAuthenticating, "Courier")]
		[InlineData(S.CourierDepositBarcodeValidating, "Courier")]
		[InlineData(S.RecipientCodeInput, "Client")]
		[InlineData(S.RecipientValidating, "Client")]
		[InlineData(S.SenderDepositBarcodeScan, "Client")]
		[InlineData(S.SenderDepositBarcodeValidating, "Client")]
		public void StatusSemaphore_role_follows_state_prefix(S state, string expectedRole)
		{
			var dir = Directory.CreateTempSubdirectory("toppos-status-");
			try {
				var semaphore = new StatusSemaphore(dir.FullName, logger: null);
				semaphore.SetBusy(state);
				Assert.True(semaphore.WaitForPendingWrites(TimeSpan.FromSeconds(5)));
				using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir.FullName, "status.lock")));
				Assert.Equal(expectedRole, doc.RootElement.GetProperty("Role").GetString());
			}
			finally {
				dir.Delete(recursive: true);
			}
		}
	}
}
