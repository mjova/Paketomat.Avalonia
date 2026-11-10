namespace TopPos.LockerSimulator.Pos
{
	public enum SimCardOutcome
	{
		Approve,
		Decline,
		Expired,
	}

	/// <summary>
	/// Izmišljena kartica simulatora. Kartica sa čipom traži PIN i na kraju "IZVADI KARTICU" (kao u snimljenim logovima),
	/// beskontaktna ne traži ni jedno ni drugo.
	/// </summary>
	public sealed record SimCard(
		string Name,
		string Scheme,
		string MaskedPan,
		bool IsChip,
		SimCardOutcome Outcome,
		string CardholderName)
	{
		public bool RequiresPin => IsChip;

		public static SimCard VisaChip { get; } = new("VISA čip — odobrava (PIN)", "VISA", "9999999999990001", IsChip: true, SimCardOutcome.Approve, "TEST/PERA");

		public static SimCard MastercardContactless { get; } = new("MASTERCARD beskontaktno — odobrava", "MASTERCARD", "9999999999990002", IsChip: false, SimCardOutcome.Approve, "TEST/MIKA");

		public static SimCard DinacardDeclined { get; } = new("DINACARD — banka odbija", "DINACARD", "9999999999990003", IsChip: false, SimCardOutcome.Decline, "TEST/LAZA");

		public static SimCard VisaExpired { get; } = new("VISA — istekla", "VISA", "9999999999990004", IsChip: false, SimCardOutcome.Expired, "TEST/ZIKA");

		public static IReadOnlyList<SimCard> Catalog { get; } = [VisaChip, MastercardContactless, DinacardDeclined, VisaExpired];

		public override string ToString() => Name;
	}
}
