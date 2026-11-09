using TopPos.Kiosk.Workflow;
using TopPos.Service;
using Xunit;
using S = TopPos.Kiosk.Workflow.WorkflowState;
using T = TopPos.Kiosk.Workflow.WorkflowTrigger;

namespace TopPos.Kiosk.Tests
{
	public class SenderDepositFlowTests
	{
		// ishod neuspele potvrde putuje uz triger; za prelaz je svejedno koji je
		private static readonly DepositConfirmFailure __failure = new(DepositConfirmResult.NotDelivered, null);

		[Fact]
		public async Task DepositRequested_from_Start_goes_to_BarcodeScan()
		{
			var m = await Mk.Started(new WorkflowContext());
			await m.FireAsync(T.SenderDepositRequested);
			Assert.Equal(S.SenderDepositBarcodeScan, m.State);
		}

		[Fact]
		public async Task BarcodeScanned_goes_to_Validating()
		{
			var m = await DriveToBarcodeScan();
			await m.FireAsync(m.SenderBarcodeScanned, "BC-7");
			Assert.Equal(S.SenderDepositBarcodeValidating, m.State);
		}

		[Fact]
		public async Task BarcodeAccepted_goes_to_PackageDeposit()
		{
			// NB: upis barcode/doorNumber u kontekst radi OnEntryFrom u MWVM.WireWorkflowMachine
			// (nije deo WorkflowMachine-a), pa se na Nivou 1 testira samo tranzicija stanja.
			var m = await DriveToValidating();
			await m.FireAsync(m.SenderBarcodeAccepted, "BC-7", 12);
			Assert.Equal(S.SenderPackageDeposit, m.State);
		}

		[Fact]
		public async Task BarcodeRejected_returns_to_BarcodeScan()
		{
			var m = await DriveToValidating();
			await m.FireAsync(T.SenderBarcodeRejected);
			Assert.Equal(S.SenderDepositBarcodeScan, m.State);
		}

		[Theory]
		[InlineData(T.Reset)]
		[InlineData(T.SenderBarcodeScanCancelled)]
		public async Task Reset_or_Cancel_during_validation_goes_to_Start(T trigger)
		{
			var m = await DriveToValidating();
			await m.FireAsync(trigger);
			Assert.Equal(S.Start, m.State);
		}

		[Theory]
		[InlineData(T.SenderBarcodeAccepted)]
		[InlineData(T.SenderBarcodeRejected)]
		public async Task BarcodeScan_ignores_validation_outcomes(T outcome)
		{
			// Zakasneli ishod provere za prethodnog pošiljaoca ne sme da otvori vrata sledećem koji je na ekranu za skeniranje.
			var m = await DriveToBarcodeScan();
			if (outcome == T.SenderBarcodeAccepted) {
				await m.FireAsync(m.SenderBarcodeAccepted, "BC-OLD", 3);
			}
			else {
				await m.FireAsync(outcome);
			}
			Assert.Equal(S.SenderDepositBarcodeScan, m.State);
		}

		[Fact]
		public async Task Late_BarcodeAccepted_after_Reset_does_not_open_door()
		{
			// Regresija (zakasneli odgovor): Home dok se čeka server. Reset se naređa ispred uspeha, mašina ode na Start,
			// a uspeh se tamo ignoriše — ni pošiljalac koji je odustao ni sledeći na istom ekranu ne dobijaju vrata.
			var server = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var (m, scan) = await ValidatingWithPendingServer(server);
			await m.FireAsync(T.Reset);
			Assert.Equal(S.SenderDepositBarcodeValidating, m.State);   // kiosk zauzet dok odgovor ne stigne
			server.SetResult();
			await scan;
			Assert.Equal(S.Start, m.State);
		}

		[Fact]
		public async Task BarcodeAccepted_without_Reset_continues()
		{
			var server = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var (m, scan) = await ValidatingWithPendingServer(server);
			server.SetResult();
			await scan;
			Assert.Equal(S.SenderPackageDeposit, m.State);
		}

		[Fact]
		public async Task BarcodeScanCancelled_resets_to_Start()
		{
			var m = await DriveToBarcodeScan();
			await m.FireAsync(T.SenderBarcodeScanCancelled);
			Assert.Equal(S.Start, m.State);
		}

		[Fact]
		public async Task BarcodeScanTimeout_resets_to_Start()
		{
			var m = await DriveToBarcodeScan();
			await m.FireAsync(T.SenderBarcodeScanTimeout);
			Assert.Equal(S.Start, m.State);
		}

		[Fact]
		public async Task DoorOpenFailed_returns_to_BarcodeScan()
		{
			var m = await DriveToPackageDeposit();
			await m.FireAsync(T.SenderDoorOpenFailed);
			Assert.Equal(S.SenderDepositBarcodeScan, m.State);
		}

		[Fact]
		public async Task ChangeBoxRequested_returns_to_BarcodeScan()
		{
			var m = await DriveToPackageDeposit();
			await m.FireAsync(T.SenderChangeBoxRequested);
			Assert.Equal(S.SenderDepositBarcodeScan, m.State);
		}

		[Fact]
		public async Task DoorClosedConfirm_resets_to_Start()   // zatvaranje vrata = paket ostavljen, bez confirmation ekrana
		{
			var m = await DriveToPackageDeposit();
			await m.FireAsync(T.SenderDoorClosedConfirm);
			Assert.Equal(S.Start, m.State);
		}

		[Fact]
		public async Task Cancelled_resets_to_Start()
		{
			var m = await DriveToPackageDeposit();
			await m.FireAsync(T.SenderCancelled);
			Assert.Equal(S.Start, m.State);
		}

		[Fact]
		public async Task Confirmation_failure_goes_to_ConfirmationFailed()
		{
			var m = await DriveToPackageDeposit();
			await m.FireAsync(m.SenderConfirmationFailed, __failure);
			Assert.Equal(S.SenderDepositConfirmationFailed, m.State);
		}

		[Theory]
		[InlineData(T.SenderConfirmationRetrySucceeded)]
		[InlineData(T.SenderPackageReturned)]
		[InlineData(T.SenderLeftUnconfirmed)]
		[InlineData(T.SenderConfirmationFailedTimeout)]
		[InlineData(T.Reset)]
		public async Task ConfirmationFailed_every_exit_goes_to_Start(T trigger)
		{
			var m = await DriveToPackageDeposit();
			await m.FireAsync(m.SenderConfirmationFailed, __failure);
			await m.FireAsync(trigger);
			Assert.Equal(S.Start, m.State);
		}

		// ─── helpers ────────────────────────────────────────────────────────────
		private static async Task<WorkflowMachine> DriveToBarcodeScan()
		{
			var m = await Mk.Started(new WorkflowContext());
			await m.FireAsync(T.SenderDepositRequested);
			return m;
		}

		private static async Task<WorkflowMachine> DriveToValidating()
		{
			var m = await DriveToBarcodeScan();
			await m.FireAsync(m.SenderBarcodeScanned, "BC-1");
			return m;
		}

		private static async Task<WorkflowMachine> DriveToPackageDeposit()
		{
			var m = await DriveToValidating();
			await m.FireAsync(m.SenderBarcodeAccepted, "BC-1", 5);
			return m;
		}

		/// <summary>
		/// Machine sa OnEntry-jem provere koji čeka "server" (<paramref name="server"/>) pa javi uspeh — kao
		/// RequestSenderDepositDoorAndRoute. Vraća task skeniranja koji traje dok se OnEntry i red trigera ne isprazne.
		/// </summary>
		private static async Task<(WorkflowMachine m, Task scan)> ValidatingWithPendingServer(TaskCompletionSource server)
		{
			var m = await DriveToBarcodeScan();
			m.OnEntryFrom(S.SenderDepositBarcodeValidating, m.SenderBarcodeScanned, async barcode => {
				await server.Task;
				await m.FireAsync(m.SenderBarcodeAccepted, barcode, 5);
			});
			var scan = m.FireAsync(m.SenderBarcodeScanned, "BC-1");
			Assert.Equal(S.SenderDepositBarcodeValidating, m.State);
			return (m, scan);
		}
	}
}
