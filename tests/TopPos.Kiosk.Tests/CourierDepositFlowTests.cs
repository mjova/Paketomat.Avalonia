using TopPos.Client.WPF.Workflow;
using TopPos.Service;
using Xunit;
using S = TopPos.Client.WPF.Workflow.WorkflowState;
using T = TopPos.Client.WPF.Workflow.WorkflowTrigger;

namespace TopPos.Workflow.Tests
{
	public class CourierDepositFlowTests
	{
		// ishod neuspele potvrde putuje uz triger; za prelaz je svejedno koji je
		private static readonly DepositConfirmFailure __failure = new(DepositConfirmResult.Unknown, null);

		[Fact]
		public async Task DepositPromptCancelled_resets_to_Start()
		{
			var m = await DriveToDepositPrompt();
			await m.FireAsync(T.DepositPromptCancelled);
			Assert.Equal(S.Start, m.State);
		}

		[Fact]
		public async Task DepositPromptDepositClicked_goes_to_BarcodeScan()
		{
			var m = await DriveToDepositPrompt();
			await m.FireAsync(T.DepositPromptDepositClicked);
			Assert.Equal(S.CourierPackageDepositBarcodeScan, m.State);
		}

		[Fact]
		public async Task DepositBarcodeAccepted_goes_to_PackageDeposit()
		{
			// NB: upis barcode/doorNumber u kontekst radi OnEntryFrom u MWVM.WireWorkflowMachine
			// (nije deo WorkflowMachine-a), pa se na Nivou 1 testira samo tranzicija stanja.
			var m = await DriveToDepositValidating(new WorkflowContext());
			await m.FireAsync(m.DepositBarcodeAccepted, "BC-7", 12);
			Assert.Equal(S.CourierPackageDeposit, m.State);
		}

		[Fact]
		public async Task DepositBarcodeScanCancelled_resets_to_Start()
		{
			var m = await DriveToDepositBarcodeScan(new WorkflowContext());
			await m.FireAsync(T.DepositBarcodeScanCancelled);
			Assert.Equal(S.Start, m.State);
		}

		[Fact]
		public async Task DepositBarcodeScanTimeout_resets_to_Start()
		{
			var m = await DriveToDepositBarcodeScan(new WorkflowContext());
			await m.FireAsync(T.DepositBarcodeScanTimeout);
			Assert.Equal(S.Start, m.State);
		}

		[Fact]
		public async Task DoorClosedAutoConfirm_returns_to_BarcodeScan()
		{
			var m = await DriveToPackageDeposit(new WorkflowContext());
			await m.FireAsync(T.DepositDoorClosedAutoConfirm);
			Assert.Equal(S.CourierPackageDepositBarcodeScan, m.State);
		}

		[Fact]
		public async Task DoorClosedExplicitConfirm_goes_to_Confirmation()
		{
			var m = await DriveToPackageDeposit(new WorkflowContext());
			await m.FireAsync(T.DepositDoorClosedExplicitConfirm);
			Assert.Equal(S.CourierDepositConfirmation, m.State);
		}

		[Fact]
		public async Task ChangeBoxRequested_returns_to_BarcodeScan()   // Faza 4 korekcija (bio PermitReentry)
		{
			var m = await DriveToPackageDeposit(new WorkflowContext());
			await m.FireAsync(T.DepositChangeBoxRequested);
			Assert.Equal(S.CourierPackageDepositBarcodeScan, m.State);
		}

		[Fact]
		public async Task DoorOpenFailed_returns_to_BarcodeScan()       // Faza 4 dodatak
		{
			var m = await DriveToPackageDeposit(new WorkflowContext());
			await m.FireAsync(T.DepositDoorOpenFailed);
			Assert.Equal(S.CourierPackageDepositBarcodeScan, m.State);
		}

		[Fact]
		public async Task CancelledLogout_resets_to_Start()
		{
			var m = await DriveToPackageDeposit(new WorkflowContext());
			await m.FireAsync(T.DepositCancelledLogout);
			Assert.Equal(S.Start, m.State);
		}

		[Fact]
		public async Task ConfirmationConfirmed_returns_to_BarcodeScan()
		{
			var m = await DriveToDepositConfirmation(new WorkflowContext());
			await m.FireAsync(T.DepositConfirmationConfirmed);
			Assert.Equal(S.CourierPackageDepositBarcodeScan, m.State);
		}

		[Fact]
		public async Task ConfirmationCancelled_returns_to_BarcodeScan()
		{
			var m = await DriveToDepositConfirmation(new WorkflowContext());
			await m.FireAsync(T.DepositConfirmationCancelled);
			Assert.Equal(S.CourierPackageDepositBarcodeScan, m.State);
		}

		// ─── Server nije potvrdio ostavljanje ───────────────────────────────────────────────────────

		[Fact]
		public async Task AutoConfirm_failure_goes_to_ConfirmationFailed()
		{
			var m = await DriveToPackageDeposit(new WorkflowContext());
			await m.FireAsync(m.DepositConfirmationFailed, __failure);
			Assert.Equal(S.CourierDepositConfirmationFailed, m.State);
		}

		[Fact]
		public async Task ConfirmationFailed_carries_outcome_to_entry_not_context()
		{
			var m = await DriveToPackageDeposit(new WorkflowContext());
			DepositConfirmFailure? received = null;
			m.OnEntryFrom(S.CourierDepositConfirmationFailed, m.DepositConfirmationFailed, failure => {
				received = failure;
				return ValueTask.CompletedTask;
			});
			var failure = new DepositConfirmFailure(DepositConfirmResult.Rejected, "poruka servera");
			await m.FireAsync(m.DepositConfirmationFailed, failure);
			Assert.Same(failure, received);
		}

		[Fact]
		public async Task ExplicitConfirm_failure_goes_to_ConfirmationFailed()
		{
			var m = await DriveToDepositConfirmation(new WorkflowContext());
			await m.FireAsync(m.DepositConfirmationFailed, __failure);
			Assert.Equal(S.CourierDepositConfirmationFailed, m.State);
		}

		[Theory]
		[InlineData(T.DepositConfirmationRetrySucceeded)]
		[InlineData(T.DepositPackageReturned)]
		[InlineData(T.DepositLeftUnconfirmed)]
		public async Task ConfirmationFailed_decision_returns_to_BarcodeScan(T decision)
		{
			var m = await DriveToConfirmationFailed();
			await m.FireAsync(decision);
			Assert.Equal(S.CourierPackageDepositBarcodeScan, m.State);
		}

		[Theory]
		[InlineData(T.DepositConfirmationFailedTimeout)]	// kurir je otišao: paket ostaje, bez potvrde
		[InlineData(T.Reset)]								// vrata nisu zatvorena posle „Preuzmi"
		public async Task ConfirmationFailed_timeout_or_Reset_goes_to_Start(T trigger)
		{
			var m = await DriveToConfirmationFailed();
			await m.FireAsync(trigger);
			Assert.Equal(S.Start, m.State);
		}

		// ─── Dodela vrata u virtuelnom stanju CourierDepositBarcodeValidating ─────────────────────────

		[Fact]
		public async Task DepositBarcodeScanned_goes_to_Validating()
		{
			var m = await DriveToDepositBarcodeScan(new WorkflowContext());
			await m.FireAsync(m.DepositBarcodeScanned, "BC-7");
			Assert.Equal(S.CourierDepositBarcodeValidating, m.State);
		}

		[Fact]
		public async Task DepositBarcodeRejected_returns_to_BarcodeScan()
		{
			var m = await DriveToDepositValidating(new WorkflowContext());
			await m.FireAsync(T.DepositBarcodeRejected);
			Assert.Equal(S.CourierPackageDepositBarcodeScan, m.State);
		}

		[Theory]
		[InlineData(T.Reset)]
		[InlineData(T.DepositBarcodeScanCancelled)]
		public async Task Reset_or_Cancel_during_validation_goes_to_Start(T trigger)
		{
			var m = await DriveToDepositValidating(new WorkflowContext());
			await m.FireAsync(trigger);
			Assert.Equal(S.Start, m.State);
		}

		[Theory]
		[InlineData(T.DepositBarcodeAccepted)]
		[InlineData(T.DepositBarcodeRejected)]
		public async Task DepositBarcodeScan_ignores_validation_outcomes(T outcome)
		{
			// Zakasneli ishod provere za prethodnog kurira ne sme da otvori vrata sledećem koji je na ekranu za skeniranje.
			var m = await DriveToDepositBarcodeScan(new WorkflowContext());
			if (outcome == T.DepositBarcodeAccepted) {
				await m.FireAsync(m.DepositBarcodeAccepted, "BC-OLD", 3);
			}
			else {
				await m.FireAsync(outcome);
			}
			Assert.Equal(S.CourierPackageDepositBarcodeScan, m.State);
		}

		[Fact]
		public async Task Late_DepositBarcodeAccepted_after_Reset_does_not_open_door()
		{
			// Regresija (zakasneli odgovor): Home dok se čeka server. Reset se naređa ispred uspeha, mašina ode na Start,
			// a uspeh se tamo ignoriše.
			var server = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var (m, scan) = await DepositValidatingWithPendingServer(server);
			await m.FireAsync(T.Reset);
			Assert.Equal(S.CourierDepositBarcodeValidating, m.State);   // kiosk zauzet dok odgovor ne stigne
			server.SetResult();
			await scan;
			Assert.Equal(S.Start, m.State);
		}

		[Fact]
		public async Task DepositBarcodeAccepted_without_Reset_continues()
		{
			var server = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var (m, scan) = await DepositValidatingWithPendingServer(server);
			server.SetResult();
			await scan;
			Assert.Equal(S.CourierPackageDeposit, m.State);
		}

		// ─── helpers ────────────────────────────────────────────────────────────
		private static async Task<WorkflowMachine> DriveToDepositPrompt()
		{
			var m = await Mk.Started(new WorkflowContext());
			await Mk.CourierLogin(m); // plain → FetchingExpired
			await m.FireAsync(T.NoExpiredPackages);        // → DepositPrompt
			return m;
		}

		private static async Task<WorkflowMachine> DriveToDepositBarcodeScan(WorkflowContext ctx)
		{
			var m = await Mk.Started(ctx);
			await Mk.CourierLogin(m);
			await m.FireAsync(T.NoExpiredPackages);
			await m.FireAsync(T.DepositPromptDepositClicked);
			return m;
		}

		private static async Task<WorkflowMachine> DriveToDepositValidating(WorkflowContext ctx)
		{
			var m = await DriveToDepositBarcodeScan(ctx);
			await m.FireAsync(m.DepositBarcodeScanned, "BC-1");
			return m;
		}

		private static async Task<WorkflowMachine> DriveToPackageDeposit(WorkflowContext ctx)
		{
			var m = await DriveToDepositValidating(ctx);
			await m.FireAsync(m.DepositBarcodeAccepted, "BC-1", 5);
			return m;
		}

		/// <summary>
		/// Machine sa OnEntry-jem provere koji čeka "server" (<paramref name="server"/>) pa javi uspeh — kao
		/// RequestCourierDepositDoorAndRoute. Vraća task skeniranja koji traje dok se OnEntry i red trigera ne isprazne.
		/// </summary>
		private static async Task<(WorkflowMachine m, Task scan)> DepositValidatingWithPendingServer(TaskCompletionSource server)
		{
			var m = await DriveToDepositBarcodeScan(new WorkflowContext());
			m.OnEntryFrom(S.CourierDepositBarcodeValidating, m.DepositBarcodeScanned, async barcode => {
				await server.Task;
				await m.FireAsync(m.DepositBarcodeAccepted, barcode, 5);
			});
			var scan = m.FireAsync(m.DepositBarcodeScanned, "BC-1");
			Assert.Equal(S.CourierDepositBarcodeValidating, m.State);
			return (m, scan);
		}

		private static async Task<WorkflowMachine> DriveToDepositConfirmation(WorkflowContext ctx)
		{
			var m = await DriveToPackageDeposit(ctx);
			await m.FireAsync(T.DepositDoorClosedExplicitConfirm);
			return m;
		}

		private static async Task<WorkflowMachine> DriveToConfirmationFailed()
		{
			var m = await DriveToPackageDeposit(new WorkflowContext());
			await m.FireAsync(m.DepositConfirmationFailed, __failure);
			return m;
		}
	}
}
