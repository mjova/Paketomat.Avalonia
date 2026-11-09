using TopPos.Kiosk.Workflow;
using Xunit;
using S = TopPos.Kiosk.Workflow.WorkflowState;

namespace TopPos.Kiosk.Tests
{
	/// <summary>
	/// Home je ugašen od plaćanja, odnosno dodele vrata, do potvrde serveru. Test prolazi kroz sva stanja, pa novo stanje
	/// mora svesno da se svrsta.
	/// </summary>
	public class HomeButtonTests
	{
		private static readonly S[] __irreversible = [
			S.RecipientPayment, S.RecipientFiscalReceipt, S.RecipientPackagePickup,
			S.CourierPackagePickup, S.CourierPackagePickupBarcodeScan,
			S.CourierPackageDeposit, S.CourierDepositConfirmation, S.CourierDepositConfirmationFailed,
			S.SenderPackageDeposit, S.SenderDepositConfirmationFailed,
			S.RemoteService,	// servisna sesija: završava je serviser, portal ili njen rok, ne Home
			S.RecipientRemoteValidating,	// provera zahteva Agenta: ishod servera mora da stigne do operacije Agenta
		];

		public static TheoryData<S> AllStates() => new(Enum.GetValues<S>());

		[Theory]
		[MemberData(nameof(AllStates))]
		public void Home_is_disabled_only_in_irreversible_procedure(S state)
			=> Assert.Equal(__irreversible.Contains(state), WorkflowMachine.IsHomeDisabled(state));
	}
}
