using TopPos.Client.WPF.Workflow;
using Xunit;
using S = TopPos.Client.WPF.Workflow.WorkflowState;
using T = TopPos.Client.WPF.Workflow.WorkflowTrigger;

namespace TopPos.Workflow.Tests
{
	public class CourierPickupFlowTests
	{
		[Fact]
		public async Task PickupPromptCancelled_resets_to_Start()
		{
			var m = await DriveToPickupPrompt(new WorkflowContext());
			await m.FireAsync(T.PickupPromptCancelled);
			Assert.Equal(S.Start, m.State);
		}

		[Fact]
		public async Task PickupPromptPickupClicked_goes_to_PackagePickup()
		{
			var m = await DriveToPickupPrompt(new WorkflowContext());
			await m.FireAsync(T.PickupPromptPickupClicked);
			Assert.Equal(S.CourierPackagePickup, m.State);
		}

		[Fact]
		public async Task DoorClosedAfterOpen_goes_to_BarcodeScan()
		{
			var m = await DriveToPackagePickup(new WorkflowContext());
			await m.FireAsync(T.PickupDoorClosedAfterOpen);
			Assert.Equal(S.CourierPackagePickupBarcodeScan, m.State);
		}

		// NB: RemainingExpiredPackages se setuje POSLE drive-a — Start OnEntry zove _context.Reset()
		// (u pravom toku listu puni FetchExpiredPackagesAndRoute posle login-a, posle Start-a).

		[Fact]
		public async Task DoorActionFailed_with_more_packages_returns_to_PickupPrompt()
		{
			var ctx = new WorkflowContext();
			var m = await DriveToPackagePickup(ctx);
			ctx.RemainingExpiredPackages = Mk.Packages(1); // još ima (guard HasMoreExpiredPackages)
			await m.FireAsync(T.PickupDoorActionFailed);
			Assert.Equal(S.CourierPickupPrompt, m.State);
		}

		[Fact]
		public async Task DoorActionFailed_with_no_more_packages_goes_to_DepositPrompt()
		{
			var ctx = new WorkflowContext();
			var m = await DriveToPackagePickup(ctx);
			ctx.RemainingExpiredPackages = Mk.Packages(0); // prazno
			await m.FireAsync(T.PickupDoorActionFailed);
			Assert.Equal(S.CourierDepositPrompt, m.State);
		}

		[Fact]
		public async Task BarcodeResolved_with_more_packages_returns_to_PickupPrompt()
		{
			var ctx = new WorkflowContext();
			var m = await DriveToBarcodeScan(ctx);
			ctx.RemainingExpiredPackages = Mk.Packages(2);
			await m.FireAsync(T.PickupBarcodeResolved);
			Assert.Equal(S.CourierPickupPrompt, m.State);
		}

		[Fact]
		public async Task BarcodeResolved_with_no_more_packages_goes_to_DepositPrompt()
		{
			var ctx = new WorkflowContext();
			var m = await DriveToBarcodeScan(ctx);
			ctx.RemainingExpiredPackages = Mk.Packages(0);
			await m.FireAsync(T.PickupBarcodeResolved);
			Assert.Equal(S.CourierDepositPrompt, m.State);
		}

		[Fact]
		public async Task BarcodeScanTimeout_resets_to_Start()
		{
			var m = await DriveToBarcodeScan(new WorkflowContext());
			await m.FireAsync(T.PickupBarcodeScanTimeout);
			Assert.Equal(S.Start, m.State);
		}

		// ─── helpers ────────────────────────────────────────────────────────────
		private static async Task<WorkflowMachine> DriveToPickupPrompt(WorkflowContext ctx)
		{
			var m = await Mk.Started(ctx);
			await Mk.CourierLogin(m); // plain → FetchingExpired
			await m.FireAsync(T.HasExpiredPackages);        // → PickupPrompt
			return m;
		}

		private static async Task<WorkflowMachine> DriveToPackagePickup(WorkflowContext ctx)
		{
			var m = await DriveToPickupPrompt(ctx);
			await m.FireAsync(T.PickupPromptPickupClicked);  // → PackagePickup
			return m;
		}

		private static async Task<WorkflowMachine> DriveToBarcodeScan(WorkflowContext ctx)
		{
			var m = await DriveToPackagePickup(ctx);
			await m.FireAsync(T.PickupDoorClosedAfterOpen);  // → BarcodeScan
			return m;
		}
	}
}
