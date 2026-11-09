using TopPos.Service;

namespace TopPos.Kiosk.Workflow
{
	/// <summary>
	/// Neuspela potvrda ostavljanja: ishod i poruka servera (uz odbijanje). Putuje uz triger do ekrana neuspele potvrde, a ne
	/// kroz kontekst — potvrda koja se završi posle kraja sesije (dugme, istek roka) tako ne menja kontekst sledeće sesije.
	/// </summary>
	public sealed record DepositConfirmFailure(DepositConfirmResult Result, string? ServerMessage);
}