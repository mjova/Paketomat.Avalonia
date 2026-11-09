using TopPos.Client.WPF.Workflow;
using Xunit;
using S = TopPos.Client.WPF.Workflow.WorkflowState;
using T = TopPos.Client.WPF.Workflow.WorkflowTrigger;

namespace TopPos.Workflow.Tests
{
	public class RecipientFlowTests
	{
		[Fact]
		public async Task Idle_to_Start_on_Reset()
		{
			var m = Mk.Machine(new WorkflowContext());
			Assert.Equal(S.Idle, m.State);
			await m.FireAsync(T.Reset);
			Assert.Equal(S.Start, m.State);
		}

		[Fact]
		public async Task Start_to_RecipientCodeInput()
		{
			var m = await Mk.Started(new WorkflowContext());
			await m.FireAsync(T.RecipientPickupRequested);
			Assert.Equal(S.RecipientCodeInput, m.State);
		}

		[Fact]
		public async Task CodeAccepted_goes_to_PhoneInput()
		{
			var m = await Mk.Started(new WorkflowContext());
			await m.FireAsync(T.RecipientPickupRequested);
			await m.FireAsync(m.RecipientCodeAccepted, "123456");
			Assert.Equal(S.RecipientPhoneInput, m.State);
		}

		[Fact]
		public async Task CodeAccepted_carries_code_to_entry_not_context()
		{
			// Kod putuje uz triger: handler skenera stare sesije ne piše kontekst, upis je u OnEntryFrom (izvršavanje mašine).
			var m = await Mk.Started(new WorkflowContext());
			await m.FireAsync(T.RecipientPickupRequested);
			string? toPhoneInput = null, toValidating = null;
			m.OnEntryFrom(S.RecipientPhoneInput, m.RecipientCodeAccepted, code => {
				toPhoneInput = code;
				return ValueTask.CompletedTask;
			});
			m.OnEntryFrom(S.RecipientValidating, m.RecipientCodeAcceptedWithDefaultPhone, code => {
				toValidating = code;
				return ValueTask.CompletedTask;
			});
			await m.FireAsync(m.RecipientCodeAccepted, "123456");
			Assert.Equal("123456", toPhoneInput);

			await m.FireAsync(T.Reset);
			await m.FireAsync(T.RecipientPickupRequested);
			await m.FireAsync(m.RecipientCodeAcceptedWithDefaultPhone, "654321");
			Assert.Equal("654321", toValidating);
		}

		[Fact]
		public async Task CodeAcceptedWithDefaultPhone_goes_straight_to_Validating()
		{
			// Telefon iz konfiguracije — bez ekrana za telefon, pravo u proveru na serveru.
			var m = await Mk.Started(new WorkflowContext());
			await m.FireAsync(T.RecipientPickupRequested);
			await m.FireAsync(m.RecipientCodeAcceptedWithDefaultPhone, "123456");
			Assert.Equal(S.RecipientValidating, m.State);
		}

		[Fact]
		public async Task PhoneEntered_goes_to_Validating()
		{
			var m = await DriveTo(S.RecipientPhoneInput);
			await m.FireAsync(m.RecipientPhoneEntered, "0641234567");
			Assert.Equal(S.RecipientValidating, m.State);
		}

		[Fact]
		public async Task NoPaymentNeeded_goes_straight_to_Pickup()
		{
			var m = await DriveToValidating();
			await m.FireAsync(T.RecipientNoPaymentNeeded);
			Assert.Equal(S.RecipientPackagePickup, m.State);
		}

		[Fact]
		public async Task PaymentRequired_goes_to_Payment()
		{
			var m = await DriveToValidating();
			await m.FireAsync(T.RecipientPaymentRequired);
			Assert.Equal(S.RecipientPayment, m.State);
		}

		[Fact]
		public async Task PaymentNotPossible_resets_to_Start()
		{
			var m = await DriveToValidating();
			await m.FireAsync(T.RecipientPaymentNotPossible);
			Assert.Equal(S.Start, m.State);
		}

		[Fact]
		public async Task ValidationFailed_resets_to_Start()
		{
			var m = await DriveToValidating();
			await m.FireAsync(T.RecipientValidationFailed);
			Assert.Equal(S.Start, m.State);
		}

		[Theory]
		[InlineData(T.RecipientNoPaymentNeeded)]
		[InlineData(T.RecipientPaymentRequired)]
		[InlineData(T.RecipientPaymentNotPossible)]
		[InlineData(T.RecipientValidationFailed)]
		public async Task PhoneInput_ignores_validation_outcomes(T outcome)
		{
			// Zakasneli ishod provere za prethodnog kupca ne sme da pomeri sledećeg kupca koji je na ekranu za telefon.
			var m = await DriveTo(S.RecipientPhoneInput);
			await m.FireAsync(outcome);
			Assert.Equal(S.RecipientPhoneInput, m.State);
		}

		[Fact]
		public async Task PaymentApproved_with_PenPos_goes_to_FiscalReceipt()
		{
			var m = await DriveToValidating(penPos: true);
			await m.FireAsync(T.RecipientPaymentRequired);
			await m.FireAsync(T.RecipientPaymentApproved);
			Assert.Equal(S.RecipientFiscalReceipt, m.State);
		}

		[Fact]
		public async Task PaymentApproved_without_PenPos_goes_to_Pickup()
		{
			var m = await DriveToValidating(penPos: false);
			await m.FireAsync(T.RecipientPaymentRequired);
			await m.FireAsync(T.RecipientPaymentApproved);
			Assert.Equal(S.RecipientPackagePickup, m.State);
		}

		[Fact]
		public async Task PaymentDeclined_resets_to_Start()
		{
			var m = await DriveToValidating();
			await m.FireAsync(T.RecipientPaymentRequired);
			await m.FireAsync(T.RecipientPaymentDeclined);
			Assert.Equal(S.Start, m.State);
		}

		[Fact]
		public async Task FiscalReceiptCompleted_goes_to_Pickup()
		{
			var m = await DriveToValidating(penPos: true);
			await m.FireAsync(T.RecipientPaymentRequired);
			await m.FireAsync(T.RecipientPaymentApproved);
			await m.FireAsync(T.RecipientFiscalReceiptCompleted);
			Assert.Equal(S.RecipientPackagePickup, m.State);
		}

		[Fact]
		public async Task PickupComplete_resets_to_Start()
		{
			var m = await DriveToValidating();
			await m.FireAsync(T.RecipientNoPaymentNeeded);
			await m.FireAsync(T.RecipientPickupComplete);
			Assert.Equal(S.Start, m.State);
		}

		[Fact]
		public async Task PickupDoorActionFailed_resets_to_Start()
		{
			var m = await DriveToValidating();
			await m.FireAsync(T.RecipientNoPaymentNeeded);
			await m.FireAsync(T.RecipientPickupDoorActionFailed);
			Assert.Equal(S.Start, m.State);
		}

		[Theory]
		[InlineData(T.RecipientPickupComplete)]
		[InlineData(T.RecipientPickupDoorActionFailed)]
		public async Task Pickup_outcome_arriving_after_Reset_is_ignored_in_Start(T outcome)
		{
			// Ishod praćenja vrata (callback DoorStatusTracker-a) stigne kad je mašina već na Start-u → samo se loguje, bez izuzetka.
			var m = await DriveToValidating();
			await m.FireAsync(T.RecipientNoPaymentNeeded);
			await m.FireAsync(T.Reset);
			await m.FireAsync(outcome);
			Assert.Equal(S.Start, m.State);
		}

		[Theory]
		[InlineData(S.RecipientCodeInput)]
		[InlineData(S.RecipientPhoneInput)]
		[InlineData(S.RecipientValidating)]
		public async Task Reset_from_input_state_goes_to_Start(S state)
		{
			// Reset (Home) je dozvoljen iz unosnih recipient stanja i iz provere na serveru → Start.
			var m = await DriveTo(state);
			Assert.Equal(state, m.State);
			await m.FireAsync(T.Reset);
			Assert.Equal(S.Start, m.State);
		}

		[Fact]
		public async Task Reset_during_Payment_is_ignored_so_paid_session_is_not_lost()
		{
			// Regresija (race sa Home tasterom): dok je plaćanje u toku, OnEntry await-uje POS Sale,
			// a Stateless naređuje sve Reset-trigere u red. Da Reset NIJE ignorisan u RecipientPayment,
			// prvi naređani Home-pritisak bi posle naplate pretekao RecipientPaymentApproved → Start,
			// i korisnik bi ostao bez paketa iako je platio. Zato Reset ovde ne sme da menja stanje.
			var m = await DriveToValidating();
			await m.FireAsync(T.RecipientPaymentRequired);
			Assert.Equal(S.RecipientPayment, m.State);

			await m.FireAsync(T.Reset);                       // Home pritisak tokom plaćanja
			Assert.Equal(S.RecipientPayment, m.State);        // ostaje na plaćanju (ignorisano)

			await m.FireAsync(T.RecipientPaymentApproved);     // naplata uspela → tok se nastavlja
			Assert.Equal(S.RecipientPackagePickup, m.State);   // vrata se otvaraju (penPos=false)
		}

		/// <summary>
		/// Machine sa OnEntry-jem provere koji čeka "server" (<paramref name="server"/>) pa javi <paramref name="outcome"/> —
		/// kao ValidateRecipientAndRoute. Vraća task potvrde telefona koji traje dok se OnEntry i red trigera ne isprazne.
		/// </summary>
		private static async Task<(WorkflowMachine m, Task confirm)> ValidatingWithPendingServer(TaskCompletionSource server, T outcome)
		{
			var m = await DriveTo(S.RecipientPhoneInput);
			async ValueTask Validate()
			{
				await server.Task;
				await m.FireAsync(outcome);
			}
			m.OnEntry(S.RecipientValidating, Validate);
			var confirm = m.FireAsync(m.RecipientPhoneEntered, "0641234567");
			Assert.Equal(S.RecipientValidating, m.State);
			return (m, confirm);
		}

		[Theory]
		[InlineData(T.RecipientNoPaymentNeeded)]
		[InlineData(T.RecipientPaymentRequired)]
		public async Task Late_server_answer_after_Reset_does_not_continue_the_session(T outcome)
		{
			// Regresija (zakasneli RRPP): Home ili tajmer dok se čeka server. Reset se naređa ispred ishoda,
			// mašina ode na Start, a ishod se tamo ignoriše — vrata se ne otvaraju i naplata se ne nudi.
			var server = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var (m, confirm) = await ValidatingWithPendingServer(server, outcome);
			await m.FireAsync(T.Reset);                     // naređan dok OnEntry čeka server
			Assert.Equal(S.RecipientValidating, m.State);   // kiosk ostaje zauzet dok odgovor ne stigne
			server.SetResult();
			await confirm;
			Assert.Equal(S.Start, m.State);
		}

		[Fact]
		public async Task Server_answer_without_Reset_continues_to_Pickup()
		{
			var server = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var (m, confirm) = await ValidatingWithPendingServer(server, T.RecipientNoPaymentNeeded);
			server.SetResult();
			await confirm;
			Assert.Equal(S.RecipientPackagePickup, m.State);
		}

		private static async Task<WorkflowMachine> DriveTo(S state, bool penPos = false)
		{
			var m = await Mk.Started(new WorkflowContext(), penPos: penPos);
			await m.FireAsync(T.RecipientPickupRequested);    // → RecipientCodeInput
			if (state == S.RecipientCodeInput) return m;
			await m.FireAsync(m.RecipientCodeAccepted, "123456");       // → RecipientPhoneInput
			if (state == S.RecipientPhoneInput) return m;
			await m.FireAsync(m.RecipientPhoneEntered, "0641234567");  // → RecipientValidating
			return m;
		}

		private static Task<WorkflowMachine> DriveToValidating(bool penPos = false) => DriveTo(S.RecipientValidating, penPos);
	}
}
