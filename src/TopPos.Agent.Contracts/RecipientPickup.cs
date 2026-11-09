namespace TopPos.Agent.Contracts
{
	/// <summary>
	/// Parametri komande <see cref="AgentCommands.RecipientPickup"/>: podrška preuzima paket umesto primaoca, a kiosk otvara
	/// vrata tek kad korisnik na ekranu unese kod prisustva koji mu je operater diktirao. <see cref="Phone"/> i
	/// <see cref="Code"/> su samo cifre, iste dužine kao unos na ekranu kioska.
	/// </summary>
	public class RecipientPickupPayload
	{
		public string Phone { get; set; } = "";

		/// <summary>Šifra za preuzimanje; mora stati u <c>int</c>.</summary>
		public string Code { get; set; } = "";

		/// <summary>
		/// Kod prisustva: generiše ga server za svaki pokušaj, operater ga diktira korisniku, a korisnik ga unosi na ekranu.
		/// Samo cifre, <see cref="RecipientPickupLimits.MinPresenceCodeLength"/>..<see cref="RecipientPickupLimits.MaxPresenceCodeLength"/>.
		/// </summary>
		public string PresenceCode { get; set; } = "";
	}

	/// <summary>Granice unosa koda prisustva (<see cref="RecipientPickupPayload.PresenceCode"/>).</summary>
	public static class RecipientPickupLimits
	{
		public const int MinPresenceCodeLength = 4;
		public const int MaxPresenceCodeLength = 5;
		/// <summary>Posle ovoliko pogrešnih unosa ishod je <see cref="RecipientPickupOutcomes.WrongCode"/>.</summary>
		public const int MaxCodeAttempts = 3;
		/// <summary>Rok za unos koda, od početka faze <see cref="RecipientPickupPhases.AwaitingAnswer"/>; posle njega <see cref="RecipientPickupOutcomes.NoAnswer"/>.</summary>
		public const int CodeTimeoutSeconds = 120;
	}

	/// <summary>Rezultat preuzimanja (<see cref="AgentResponse.Result"/>) za <see cref="AgentCommands.GetCommandResult"/> sa njegovim <see cref="AgentRequest.RequestId"/>.</summary>
	public class RecipientPickupResult
	{
		/// <summary><see cref="RecipientPickupOutcomes"/>; <c>null</c> dok tok traje (<see cref="Phase"/> pre <see cref="RecipientPickupPhases.Completed"/>).</summary>
		public string? Outcome { get; set; }

		/// <summary>Vrata koja server dodeljuje; poznata od <see cref="RecipientPickupPhases.AwaitingAnswer"/>.</summary>
		public int? DoorNumber { get; set; }

		/// <summary>Iznos za uplatu, uz <see cref="RecipientPickupOutcomes.PaymentRequired"/>.</summary>
		public decimal? Amount { get; set; }

		/// <summary>Poruka TopPos servera, uz <see cref="RecipientPickupOutcomes.ServerRejected"/>.</summary>
		public string? ServerMessage { get; set; }

		/// <summary><see cref="PickupConfirmationStates"/>: potvrda preuzimanja serveru, uz ishode posle otvaranja.</summary>
		public string? Confirmation { get; set; }

		/// <summary>
		/// Broj pogrešnih unosa koda prisustva; poznat od <see cref="RecipientPickupPhases.AwaitingAnswer"/>. Svaki pogrešan unos
		/// podiže <see cref="KioskStatus.OperationSequence"/>, pa operater vidi broj i dok tok traje.
		/// </summary>
		public int? FailedCodeAttempts { get; set; }

		/// <summary><see cref="RecipientPickupPhases"/>.</summary>
		public string Phase { get; set; } = "";
	}

	/// <summary>
	/// Faze toka (<see cref="RecipientPickupResult.Phase"/>); svaka faza, i svaki pogrešan unos koda, podiže
	/// <see cref="KioskStatus.OperationSequence"/>.
	/// </summary>
	public static class RecipientPickupPhases
	{
		/// <summary>Kiosk proverava šifru i telefon na serveru.</summary>
		public const string Validating = "Validating";
		/// <summary>
		/// Server je dodelio vrata <see cref="RecipientPickupResult.DoorNumber"/>; ekran čeka kod prisustva (broj vrata ne
		/// prikazuje). Pogrešni unosi su u <see cref="RecipientPickupResult.FailedCodeAttempts"/>.
		/// </summary>
		public const string AwaitingAnswer = "AwaitingAnswer";
		/// <summary>Korisnik je uneo tačan kod; kiosk otvara vrata i potvrđuje preuzimanje serveru.</summary>
		public const string Opening = "Opening";
		/// <summary>Tok je završen; <see cref="RecipientPickupResult.Outcome"/> je konačan.</summary>
		public const string Completed = "Completed";
	}

	/// <summary>Ishodi preuzimanja (<see cref="RecipientPickupResult.Outcome"/>).</summary>
	public static class RecipientPickupOutcomes
	{
		/// <summary>Kontroler je javio otvorena vrata.</summary>
		public const string OpenConfirmed = "OpenConfirmed";
		/// <summary>Kontroler je javio zatvorena vrata.</summary>
		public const string OpenNotConfirmed = "OpenNotConfirmed";
		/// <summary>Komanda otvaranja je poslata, a odgovor nije stigao: vrata su možda otvorena.</summary>
		public const string OutcomeUnknown = "OutcomeUnknown";
		/// <summary>Treba platiti (<see cref="RecipientPickupResult.Amount"/>); kiosk ne naplaćuje karticom i ništa ne otvara.</summary>
		public const string PaymentRequired = "PaymentRequired";
		/// <summary>Server je odbio zahtev (<see cref="RecipientPickupResult.ServerMessage"/>).</summary>
		public const string ServerRejected = "ServerRejected";
		/// <summary>Server nije odgovorio (istek roka ili greška veze).</summary>
		public const string ServerUnavailable = "ServerUnavailable";
		/// <summary>Korisnik je na ekranu odustao (Odustani ili Home).</summary>
		public const string Declined = "Declined";
		/// <summary>Korisnik nije uneo tačan kod prisustva u roku (<see cref="RecipientPickupLimits.CodeTimeoutSeconds"/>).</summary>
		public const string NoAnswer = "NoAnswer";
		/// <summary>Kod prisustva je <see cref="RecipientPickupLimits.MaxCodeAttempts"/> puta pogrešno unet; ništa nije otvoreno.</summary>
		public const string WrongCode = "WrongCode";
		/// <summary>Tok je prekinut pre otvaranja (npr. restart programa kioska): ništa nije otvoreno, potvrda nije poslata.</summary>
		public const string Interrupted = "Interrupted";
	}

	/// <summary>Potvrda preuzimanja serveru (<see cref="RecipientPickupResult.Confirmation"/>).</summary>
	public static class PickupConfirmationStates
	{
		public const string Confirmed = "Confirmed";
		/// <summary>Server nije odgovorio; potvrda je u redu potvrda kioska i šalje se kasnije.</summary>
		public const string Queued = "Queued";
		public const string Failed = "Failed";
	}
}
