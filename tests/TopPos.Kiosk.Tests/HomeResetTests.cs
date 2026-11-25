using TopPos.Kiosk.Workflow;
using Xunit;
using S = TopPos.Kiosk.Workflow.WorkflowState;
using T = TopPos.Kiosk.Workflow.WorkflowTrigger;

namespace TopPos.Kiosk.Tests
{
	/// <summary>
	/// v4: <see cref="WorkflowMachine.FireHomeResetAsync"/> — Home proverava stanje kad triger dođe na red. Home pritisnut dok
	/// server dodeljuje vrata, a koji stigne posle dodele, ne prekida otvaranje vrata (ranije: Start dok su vrata otvorena).
	/// </summary>
	public class HomeResetTests
	{
		/// <summary>
		/// Kurir skenira paket: provera na serveru (CourierDepositBarcodeValidating, Home uključen) čeka <paramref name="server"/>.
		/// Za to vreme u red ulaze dodela vrata (kao iz aktivatora) i Home; puštanje servera prazni red redom.
		/// </summary>
		private static async Task<(WorkflowMachine Machine, Task Scanning)> WhileServerAssignsDoor(Task server)
		{
			var m = await Mk.Started(new WorkflowContext());
			await Mk.CourierLogin(m);
			await m.FireAsync(T.NoExpiredPackages);
			await m.FireAsync(T.DepositPromptDepositClicked);
			m.OnEntry(S.CourierDepositBarcodeValidating, async () => await server);
			var scanning = m.FireAsync(m.DepositBarcodeScanned, "PKG9");
			Assert.Equal(S.CourierDepositBarcodeValidating, m.State);
			Assert.False(WorkflowMachine.IsHomeDisabled(m.State));	// Home je uključen kad je pritisnut
			return (m, scanning);
		}

		[Fact]
		public async Task Home_queued_behind_door_assignment_does_not_reset()
		{
			var server = new TaskCompletionSource();
			var (m, scanning) = await WhileServerAssignsDoor(server.Task);
			var rejected = false;

			_ = m.FireAsync(m.DepositBarcodeAccepted, "PKG9", 7);
			_ = m.FireHomeResetAsync(() => rejected = true);
			server.SetResult();
			await scanning;

			Assert.Equal(S.CourierPackageDeposit, m.State);
			Assert.True(rejected);
		}

		/// <summary>Kontrola: običan Reset u istom redosledu vraća na Start (ponašanje pre ispravke).</summary>
		[Fact]
		public async Task Plain_reset_queued_behind_door_assignment_resets()
		{
			var server = new TaskCompletionSource();
			var (m, scanning) = await WhileServerAssignsDoor(server.Task);

			_ = m.FireAsync(m.DepositBarcodeAccepted, "PKG9", 7);
			_ = m.FireAsync(T.Reset);
			server.SetResult();
			await scanning;

			Assert.Equal(S.Start, m.State);
		}

		[Fact]
		public async Task Home_where_it_is_enabled_resets()
		{
			var m = await Mk.Started(new WorkflowContext());
			await Mk.CourierLogin(m);
			await m.FireAsync(T.NoExpiredPackages);

			await m.FireHomeResetAsync();

			Assert.Equal(S.Start, m.State);
		}
	}
}
