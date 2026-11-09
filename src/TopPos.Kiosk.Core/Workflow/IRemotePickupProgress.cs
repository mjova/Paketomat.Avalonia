using TopPos.Agent.Contracts;
using TopPos.Client.WPF.AgentCommunication;
using TopPos.LockController;

namespace TopPos.Client.WPF.Workflow
{
	/// <summary>
	/// Preuzimanje na zahtev Agenta, iz ugla stranica toka. Stranica potvrde (<see cref="WorkflowState.RecipientRemoteConfirmation"/>)
	/// predaje kod prisustva. Stranica preuzimanja (<see cref="WorkflowState.RecipientPackagePickup"/>) pre otvaranja čeka upis
	/// faze otvaranja, a posle otvaranja i potvrde serveru javlja konačan ishod. U kontekstu je (<see cref="WorkflowContext.RemotePickup"/>)
	/// samo kad je tok pokrenuo Agent.
	/// </summary>
	public interface IRemotePickupProgress
	{
		/// <summary>Preostali pokušaji unosa koda prisustva.</summary>
		int RemainingCodeAttempts { get; }

		/// <summary>
		/// Kod prisustva sa ekrana: <see cref="PresenceCodeResult.Accepted"/> (sledi <see cref="WorkflowTrigger.RecipientRemoteAccepted"/>),
		/// <see cref="PresenceCodeResult.Rejected"/> (ima još pokušaja), <see cref="PresenceCodeResult.Failed"/> (ishod
		/// <see cref="RecipientPickupOutcomes.WrongCode"/> je upisan; sledi <see cref="WorkflowTrigger.RecipientRemoteCodeFailed"/>)
		/// ili <see cref="PresenceCodeResult.NotAwaiting"/>.
		/// </summary>
		Task<PresenceCodeResult> SubmitPresenceCodeAsync(string code);

		/// <summary>Čeka upis faze <see cref="RecipientPickupPhases.Opening"/> u dnevnik; false kad nije upisana na disk.</summary>
		Task<bool> OpeningRecordedAsync();

		/// <summary>Konačan ishod otvaranja (<see cref="RecipientPickupOutcomes"/>) i, posle otvorenih vrata, stanje potvrde serveru.</summary>
		Task CompleteOpeningAsync(string outcome, string? confirmation);
	}

	/// <summary>Pravila ishoda preuzimanja na zahtev Agenta, iz rezultata otvaranja i potvrde serveru.</summary>
	public static class RemotePickupReport
	{
		/// <summary>Poslednji odgovor kontrolera: otvorena, zatvorena, ili bez odgovora (vrata su možda otvorena).</summary>
		public static string OutcomeOf(DoorStatusCode? status)
			=> status is not { } code ? RecipientPickupOutcomes.OutcomeUnknown
				: code.IsOpen ? RecipientPickupOutcomes.OpenConfirmed
				: RecipientPickupOutcomes.OpenNotConfirmed;

		/// <summary>Potvrda preuzimanja serveru: potvrđena, u redu potvrda (server nije odgovorio), ili neuspela.</summary>
		public static string ConfirmationOf(bool confirmed, bool queued)
			=> confirmed ? PickupConfirmationStates.Confirmed
				: queued ? PickupConfirmationStates.Queued
				: PickupConfirmationStates.Failed;
	}
}