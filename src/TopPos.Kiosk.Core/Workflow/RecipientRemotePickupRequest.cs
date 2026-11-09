namespace TopPos.Client.WPF.Workflow
{
	/// <summary>
	/// Zahtev Agenta za preuzimanje (<c>RecipientPickup</c>): <c>requestId</c> operacije i podaci koje je podrška unela umesto
	/// primaoca (već provereni po pravilima unosa sa ekrana). Putuje uz <see cref="WorkflowTrigger.RecipientRemotePickupStarted"/>
	/// i na ulazu u <see cref="WorkflowState.RecipientRemoteValidating"/> se upisuje u <see cref="WorkflowContext"/>.
	/// </summary>
	public sealed record RecipientRemotePickupRequest(string RequestId, string Phone, string Code);
}