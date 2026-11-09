using TopPos.Kiosk.Workflow;
using Xunit;
using S = TopPos.Kiosk.Workflow.WorkflowState;
using T = TopPos.Kiosk.Workflow.WorkflowTrigger;

namespace TopPos.Kiosk.Tests
{
	/// <summary>
	/// Preuzimanje na zahtev Agenta (<c>RecipientPickup</c>): Start → provera na serveru → potvrda korisnika (Da/Ne) →
	/// postojeće <see cref="S.RecipientPackagePickup"/>. Provera se ne prekida Reset-om; na potvrdi Home znači „Ne".
	/// </summary>
	public class RecipientRemoteFlowTests
	{
		private static readonly RecipientRemotePickupRequest __request = new("r1", "0641234567", "123456");

		private static async Task<WorkflowMachine> Validating(WorkflowContext? ctx = null)
		{
			var m = await Mk.Started(ctx ?? new WorkflowContext());
			await m.FireAsync(m.RecipientRemotePickupStarted, __request);
			return m;
		}

		private static async Task<WorkflowMachine> Confirmation()
		{
			var m = await Validating();
			await m.FireAsync(T.RecipientNoPaymentNeeded);
			return m;
		}

		[Fact]
		public async Task RecipientRemotePickupStarted_in_Start_goes_to_RecipientRemoteValidating()
			=> Assert.Equal(S.RecipientRemoteValidating, (await Validating()).State);

		[Fact]
		public async Task Request_data_is_in_context_before_OnEntry_handler()
		{
			var ctx = new WorkflowContext();
			var m = await Mk.Started(ctx);
			(string?, string?, string?) seen = default;
			m.OnEntry(S.RecipientRemoteValidating, () => seen = (ctx.RemotePickupRequestId, ctx.RecipientPhoneNumber, ctx.UnlockCode));

			await m.FireAsync(m.RecipientRemotePickupStarted, __request);

			Assert.Equal(("r1", "0641234567", "123456"), seen);
		}

		[Fact]
		public async Task Context_of_the_request_is_cleared_on_Start()
		{
			var ctx = new WorkflowContext();
			var m = await Validating(ctx);
			await m.FireAsync(T.RecipientValidationFailed);

			Assert.Equal(S.Start, m.State);
			Assert.Null(ctx.RemotePickupRequestId);
			Assert.Null(ctx.RecipientPhoneNumber);
			Assert.Null(ctx.UnlockCode);
		}

		[Fact]
		public async Task Door_without_payment_goes_to_RecipientRemoteConfirmation()
			=> Assert.Equal(S.RecipientRemoteConfirmation, (await Confirmation()).State);

		[Theory]
		[InlineData(T.RecipientPaymentRequired)]	// bez kartice, i kad POS postoji
		[InlineData(T.RecipientValidationFailed)]
		public async Task Payment_or_failed_validation_goes_to_Start(T outcome)
		{
			var m = await Validating();
			await m.FireAsync(outcome);
			Assert.Equal(S.Start, m.State);
		}

		[Fact]
		public async Task Payment_does_not_go_to_card_payment_even_with_pos()
		{
			var m = await Mk.Started(new WorkflowContext(), penPos: true);
			await m.FireAsync(m.RecipientRemotePickupStarted, __request);
			await m.FireAsync(T.RecipientPaymentRequired);
			Assert.Equal(S.Start, m.State);
		}

		[Fact]
		public async Task Reset_is_ignored_during_RecipientRemoteValidating()
		{
			var m = await Validating();
			await m.FireAsync(T.Reset);
			Assert.Equal(S.RecipientRemoteValidating, m.State);
		}

		[Fact]
		public async Task Accepted_goes_to_RecipientPackagePickup()
		{
			var m = await Confirmation();
			await m.FireAsync(T.RecipientRemoteAccepted);
			Assert.Equal(S.RecipientPackagePickup, m.State);
		}

		[Theory]
		[InlineData(T.RecipientRemoteDeclined)]
		[InlineData(T.RecipientRemoteNoAnswer)]
		[InlineData(T.RecipientRemoteCodeFailed)]
		[InlineData(T.Reset)]	// Home na stranici potvrde = „Odustani"
		public async Task Declined_no_answer_or_Home_goes_to_Start(T trigger)
		{
			var m = await Confirmation();
			await m.FireAsync(trigger);
			Assert.Equal(S.Start, m.State);
		}

		[Fact]
		public async Task Pickup_after_acceptance_ends_on_Start()
		{
			var m = await Confirmation();
			await m.FireAsync(T.RecipientRemoteAccepted);
			await m.FireAsync(T.RecipientPickupComplete);
			Assert.Equal(S.Start, m.State);
		}

		[Fact]
		public async Task RecipientRemotePickupStarted_is_ignored_outside_Start()
		{
			var m = await Mk.Started(new WorkflowContext());
			await m.FireAsync(T.RecipientPickupRequested);
			await m.FireAsync(m.RecipientRemotePickupStarted, __request);
			Assert.Equal(S.RecipientCodeInput, m.State);
		}

		[Theory]
		[InlineData(T.RecipientPickupRequested)]
		[InlineData(T.SenderDepositRequested)]
		[InlineData(T.ServiceBarcodeScanned)]
		public async Task Start_inputs_are_ignored_during_RecipientRemoteValidating(T trigger)
		{
			var m = await Validating();
			await m.FireIfInStateAsync(S.Start, trigger);
			Assert.Equal(S.RecipientRemoteValidating, m.State);
		}

		[Fact]
		public async Task Late_validation_outcome_does_not_affect_the_local_flow()
		{
			var m = await Validating();
			await m.FireAsync(T.RecipientValidationFailed);
			await m.FireAsync(T.RecipientPickupRequested);

			await m.FireAsync(T.RecipientNoPaymentNeeded);
			Assert.Equal(S.RecipientCodeInput, m.State);
		}

		[Theory]
		[InlineData(S.RecipientRemoteValidating, true)]
		[InlineData(S.RecipientRemoteConfirmation, false)]
		public void Home_is_disabled_only_while_validating(S state, bool disabled)
			=> Assert.Equal(disabled, WorkflowMachine.IsHomeDisabled(state));

		[Theory]
		[InlineData(S.RecipientRemoteValidating)]
		[InlineData(S.RecipientRemoteConfirmation)]
		public void Role_is_Client_and_page_is_not_full_screen(S state)
		{
			Assert.Equal("Client", WorkflowStatusRules.ResolveRole(state));
			Assert.False(WorkflowMachine.IsFullScreen(state));
		}
	}
}