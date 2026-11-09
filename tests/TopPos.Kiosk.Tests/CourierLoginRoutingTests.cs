using TopPos.Client.WPF.Workflow;
using Xunit;
using S = TopPos.Client.WPF.Workflow.WorkflowState;
using T = TopPos.Client.WPF.Workflow.WorkflowTrigger;

namespace TopPos.Workflow.Tests
{
	public class CourierLoginRoutingTests
	{
		[Fact]
		public async Task ScannedBarcode_requiring_manual_login_goes_to_ManualLogin()
		{
			var m = await Mk.Started(new WorkflowContext());
			await m.FireAsync(m.CourierBarcodeScannedRequiringManualLogin, "QR-123");
			Assert.Equal(S.CourierManualLogin, m.State);
		}

		[Fact]
		public async Task ScannedBarcode_without_manual_login_goes_to_Authenticating()
		{
			var m = await Mk.Started(new WorkflowContext());
			await m.FireAsync(m.CourierBarcodeScanned, "QR-123");
			Assert.Equal(S.CourierAuthenticating, m.State);
		}

		[Fact]
		public async Task LoginSuccessful_with_BoxAvailability_enabled_goes_to_BoxAvailability()
		{
			var m = await Mk.Started(new WorkflowContext(), enableBox: true);
			await m.FireAsync(m.CourierBarcodeScanned, "QR-123");
			await m.FireAsync(T.CourierLoginSuccessful);
			Assert.Equal(S.CourierLockerBoxAvailability, m.State);
		}

		[Fact]
		public async Task LoginSuccessful_with_ConfirmPickup_goes_to_PickupConfirmation()
		{
			var m = await Mk.Started(new WorkflowContext(), enableBox: false, confirmPickup: true);
			await m.FireAsync(m.CourierBarcodeScanned, "QR-123");
			await m.FireAsync(T.CourierLoginSuccessful);
			Assert.Equal(S.CourierPickupConfirmation, m.State);
		}

		[Fact]
		public async Task LoginSuccessful_plain_goes_to_FetchingExpired()
		{
			var m = await Mk.Started(new WorkflowContext(), enableBox: false, confirmPickup: false);
			await m.FireAsync(m.CourierBarcodeScanned, "QR-123");
			await m.FireAsync(T.CourierLoginSuccessful);
			Assert.Equal(S.CourierFetchingExpiredPackages, m.State);
		}

		[Fact]
		public async Task LoginFailed_goes_back_to_Start()
		{
			var m = await Mk.Started(new WorkflowContext());
			await m.FireAsync(m.CourierBarcodeScanned, "QR-123");
			await m.FireAsync(T.CourierLoginFailed);
			Assert.Equal(S.Start, m.State);
		}

		[Fact]
		public async Task Reset_during_authentication_goes_to_Start()
		{
			var m = await Mk.Started(new WorkflowContext());
			await m.FireAsync(m.CourierBarcodeScanned, "QR-123");
			await m.FireAsync(T.Reset);
			Assert.Equal(S.Start, m.State);
		}

		[Fact]
		public async Task LoginSuccessful_arriving_after_Reset_is_ignored_in_Start()
		{
			// Reset (npr. povratak operativnosti) naređan ispred zakasnelog uspeha — kurir ne sme da upadne u tok.
			var ctx = new WorkflowContext();
			var m = await Mk.Started(ctx, enableBox: true);
			await m.FireAsync(m.CourierBarcodeScanned, "QR-123");
			await m.FireAsync(T.Reset);
			await m.FireAsync(T.CourierLoginSuccessful);
			Assert.Equal(S.Start, m.State);
		}

		[Fact]
		public async Task LoginSuccessful_is_not_permitted_directly_from_Start()
		{
			var m = await Mk.Started(new WorkflowContext(), enableBox: false, confirmPickup: false);
			await m.FireAsync(T.CourierLoginSuccessful);
			Assert.Equal(S.Start, m.State);
		}

		[Theory]
		[InlineData(T.RecipientPickupRequested)]
		[InlineData(T.SenderDepositRequested)]
		public async Task Start_buttons_are_ignored_during_authentication(T trigger)
		{
			// Start ekran ostaje prikazan dok traje prijava — klik ne sme da pokrene kupčev tok.
			var m = await Mk.Started(new WorkflowContext());
			await m.FireAsync(m.CourierBarcodeScanned, "QR-123");
			await m.FireAsync(trigger);
			Assert.Equal(S.CourierAuthenticating, m.State);
		}

		/// <summary>
		/// Machine sa OnEntry-jem prijave koji čeka "server" (<paramref name="server"/>) pa javi uspeh — kao
		/// AuthenticateCourierAndRoute. Vraća task skeniranja koji traje dok se OnEntry i red trigera ne isprazne.
		/// </summary>
		private static async Task<(WorkflowMachine m, Task scan)> AuthenticatingWithPendingServer(TaskCompletionSource server)
		{
			var m = await Mk.Started(new WorkflowContext());
			m.OnEntryFrom(S.CourierAuthenticating, m.CourierBarcodeScanned, async _ => {
				await server.Task;
				await m.FireAsync(T.CourierLoginSuccessful);
			});
			var scan = m.FireAsync(m.CourierBarcodeScanned, "QR-123");
			Assert.Equal(S.CourierAuthenticating, m.State);
			return (m, scan);
		}

		[Theory]
		[InlineData(T.RecipientPickupRequested)]
		[InlineData(T.SenderDepositRequested)]
		public async Task Start_click_during_authentication_is_dropped_even_if_Reset_was_queued_before_it(T trigger)
		{
			var server = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var (m, scan) = await AuthenticatingWithPendingServer(server);
			await m.FireAsync(T.Reset);                     // naređan (Home / povratak operativnosti)
			await m.FireIfInStateAsync(S.Start, trigger);   // klik sa Start ekrana dok prijava traje
			server.SetResult();
			await scan;
			Assert.Equal(S.Start, m.State);
		}

		[Fact]
		public async Task Unguarded_Start_click_queued_behind_Reset_would_start_customer_flow()
		{
			// Zašto postoji FireIfInStateAsync: običan FireAsync se naređa, a Reset ispred njega ga učini dozvoljenim.
			var server = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var (m, scan) = await AuthenticatingWithPendingServer(server);
			await m.FireAsync(T.Reset);
			await m.FireAsync(T.RecipientPickupRequested);
			server.SetResult();
			await scan;
			Assert.Equal(S.RecipientCodeInput, m.State);
		}

		[Fact]
		public async Task Start_click_in_Start_is_accepted()
		{
			var m = await Mk.Started(new WorkflowContext());
			await m.FireIfInStateAsync(S.Start, T.RecipientPickupRequested);
			Assert.Equal(S.RecipientCodeInput, m.State);
		}

		// ─── Ručna prijava: provera koda u virtuelnom stanju CourierManualAuthenticating ─────────────

		private static async Task<WorkflowMachine> ManualAuthenticating(bool enableBox = false, bool confirmPickup = false)
		{
			var m = await Mk.Started(new WorkflowContext(), enableBox: enableBox, confirmPickup: confirmPickup);
			await m.FireAsync(m.CourierBarcodeScannedRequiringManualLogin, "QR-123");
			await m.FireAsync(m.CourierManualCodeEntered, "4321");
			return m;
		}

		[Fact]
		public async Task ManualCode_entered_goes_to_ManualAuthenticating()
		{
			var m = await ManualAuthenticating();
			Assert.Equal(S.CourierManualAuthenticating, m.State);
		}

		[Theory]
		[InlineData(true, false, S.CourierLockerBoxAvailability)]
		[InlineData(false, true, S.CourierPickupConfirmation)]
		[InlineData(false, false, S.CourierFetchingExpiredPackages)]
		public async Task ManualLogin_then_LoginSuccessful_routes_same_as_direct(bool enableBox, bool confirmPickup, S expected)
		{
			var m = await ManualAuthenticating(enableBox, confirmPickup);
			await m.FireAsync(T.CourierLoginSuccessful);
			Assert.Equal(expected, m.State);
		}

		[Fact]
		public async Task Manual_LoginFailed_returns_to_code_entry()
		{
			// Odluka korisnika: pogrešan kod ne vraća na Start — kurir ponovo kuca kod, bez ponovnog skeniranja.
			var m = await ManualAuthenticating();
			await m.FireAsync(T.CourierLoginFailed);
			Assert.Equal(S.CourierManualLogin, m.State);
		}

		[Fact]
		public async Task Reset_during_manual_authentication_goes_to_Start()
		{
			var m = await ManualAuthenticating();
			await m.FireAsync(T.Reset);
			Assert.Equal(S.Start, m.State);
		}

		[Theory]
		[InlineData(T.CourierLoginSuccessful)]
		[InlineData(T.CourierLoginFailed)]
		public async Task ManualLogin_screen_ignores_login_outcomes(T outcome)
		{
			// Zakasneli ishod provere za prethodnog kurira ne sme da pomeri sledećeg koji je na ekranu za unos koda.
			var m = await Mk.Started(new WorkflowContext(), enableBox: true);
			await m.FireAsync(m.CourierBarcodeScannedRequiringManualLogin, "QR-456");
			await m.FireAsync(outcome);
			Assert.Equal(S.CourierManualLogin, m.State);
		}

		/// <summary>
		/// Machine sa OnEntry-jem ručne provere koji čeka "server" (<paramref name="server"/>) pa javi uspeh — kao
		/// AuthenticateManualCourierAndRoute. Vraća task unosa koda koji traje dok se OnEntry i red trigera ne isprazne.
		/// </summary>
		private static async Task<(WorkflowMachine m, Task entry)> ManualAuthenticatingWithPendingServer(TaskCompletionSource server)
		{
			var m = await Mk.Started(new WorkflowContext(), enableBox: true);
			await m.FireAsync(m.CourierBarcodeScannedRequiringManualLogin, "QR-123");
			m.OnEntryFrom(S.CourierManualAuthenticating, m.CourierManualCodeEntered, async _ => {
				await server.Task;
				await m.FireAsync(T.CourierLoginSuccessful);
			});
			var entry = m.FireAsync(m.CourierManualCodeEntered, "4321");
			Assert.Equal(S.CourierManualAuthenticating, m.State);
			return (m, entry);
		}

		[Fact]
		public async Task Late_manual_login_success_after_Reset_does_not_log_in_anyone()
		{
			// Regresija (zakasneli odgovor): Home ili tajmer dok se čeka server. Reset se naređa ispred uspeha, mašina ode na
			// Start, a uspeh se tamo ignoriše — ni kurir koji je odustao ni sledeći na istom ekranu ne bivaju prijavljeni.
			var server = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var (m, entry) = await ManualAuthenticatingWithPendingServer(server);
			await m.FireAsync(T.Reset);
			Assert.Equal(S.CourierManualAuthenticating, m.State);   // kiosk zauzet dok odgovor ne stigne
			server.SetResult();
			await entry;
			Assert.Equal(S.Start, m.State);
		}

		[Fact]
		public async Task Manual_login_success_without_Reset_continues()
		{
			var server = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var (m, entry) = await ManualAuthenticatingWithPendingServer(server);
			server.SetResult();
			await entry;
			Assert.Equal(S.CourierLockerBoxAvailability, m.State);
		}

		[Fact]
		public async Task BoxAvailabilityCompleted_goes_to_FetchingExpired()
		{
			var m = await Mk.Started(new WorkflowContext(), enableBox: true);
			await Mk.CourierLogin(m);
			await m.FireAsync(T.BoxAvailabilityCompleted);
			Assert.Equal(S.CourierFetchingExpiredPackages, m.State);
		}

		[Fact]
		public async Task PickupConfirmed_goes_to_FetchingExpired()
		{
			var m = await Mk.Started(new WorkflowContext(), confirmPickup: true);
			await Mk.CourierLogin(m);
			await m.FireAsync(T.PickupConfirmed);
			Assert.Equal(S.CourierFetchingExpiredPackages, m.State);
		}

		[Fact]
		public async Task PickupConfirmationCancelled_goes_to_DepositPrompt_and_marks_pickedUp()
		{
			var ctx = new WorkflowContext();
			var m = await Mk.Started(ctx, confirmPickup: true);
			await Mk.CourierLogin(m);
			await m.FireAsync(T.PickupConfirmationCancelled);
			Assert.Equal(S.CourierDepositPrompt, m.State);
			Assert.True(ctx.ExpiredPackagesPickedUp);   // OnEntryFrom side-effect
		}

		[Fact]
		public async Task HasExpiredPackages_goes_to_PickupPrompt()
		{
			var m = await Mk.Started(new WorkflowContext());
			await Mk.CourierLogin(m);
			await m.FireAsync(T.HasExpiredPackages);
			Assert.Equal(S.CourierPickupPrompt, m.State);
		}

		[Fact]
		public async Task NoExpiredPackages_goes_to_DepositPrompt_and_clears_pickedUp()
		{
			var ctx = new WorkflowContext();
			var m = await Mk.Started(ctx);
			await Mk.CourierLogin(m);
			await m.FireAsync(T.NoExpiredPackages);
			Assert.Equal(S.CourierDepositPrompt, m.State);
			Assert.False(ctx.ExpiredPackagesPickedUp);  // OnEntryFrom side-effect
		}
	}
}
