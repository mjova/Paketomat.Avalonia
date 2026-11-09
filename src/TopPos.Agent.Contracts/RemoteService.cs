namespace TopPos.Agent.Contracts
{
	/// <summary>
	/// Parametri komande <see cref="AgentCommands.BeginRemoteService"/>: servisna sesija u kojoj serviser pred paketomatom
	/// otvara vrata iz portala ili sa ekrana kioska.
	/// </summary>
	public class BeginRemoteServicePayload
	{
		/// <summary>Rok neaktivnosti posle prijave servisera, u sekundama (<see cref="RemoteServiceLimits"/>).</summary>
		public int IdleTimeoutSeconds { get; set; }

		/// <summary>
		/// Raspored vrata sa servera (obavezan), za crtež na ekranu kao u <c>TopPos.Technician</c>. Vrata kojih na kiosku nema
		/// se izbacuju i navode u <see cref="RemoteServiceResult.IgnoredDoors"/>.
		/// </summary>
		public List<LayoutBox>? Layout { get; set; }

		/// <summary>Jednokratni kod sesije (samo cifre) kojim se serviser prijavljuje na ekranu kioska.</summary>
		public string PresenceCode { get; set; } = "";
	}

	/// <summary>Jedna pozicija rasporeda (isti oblik kao CSV rasporeda u <c>TopPos.Technician</c>: DoorNumber;Size;Column;Row).</summary>
	public class LayoutBox
	{
		public int DoorNumber { get; set; }

		/// <summary><see cref="BoxSizes"/>; određuje visinu pozicije na crtežu.</summary>
		public string Size { get; set; } = "";

		/// <summary>Vertikala, od 1 (leva).</summary>
		public int Column { get; set; }

		/// <summary>Redni broj boksa u vertikali, od 1 (najviši); mesto na crtežu određuju visine boksova iznad.</summary>
		public int Row { get; set; }
	}

	/// <summary>Veličine boksa (<see cref="LayoutBox.Size"/>), visine 1, 2 i 3.</summary>
	public static class BoxSizes
	{
		public const string S = "S";
		public const string M = "M";
		public const string L = "L";

		public static readonly IReadOnlyList<string> All = [S, M, L];
	}

	/// <summary>Parametri komande <see cref="AgentCommands.OpenDoors"/>. Zadaje se ili <see cref="DoorNumbers"/>, ili <see cref="All"/>.</summary>
	public class OpenDoorsPayload
	{
		/// <summary><see cref="AgentRequest.RequestId"/> komande <see cref="AgentCommands.BeginRemoteService"/>.</summary>
		public string SessionId { get; set; } = "";

		public List<int>? DoorNumbers { get; set; }

		/// <summary>Sva vrata kioska, uključujući servisna.</summary>
		public bool All { get; set; }
	}

	/// <summary>Parametri komande <see cref="AgentCommands.EndRemoteService"/>: serviser je u portalu potvrdio da je zatvorio sva vrata.</summary>
	public class EndRemoteServicePayload
	{
		public string SessionId { get; set; } = "";
	}

	/// <summary>Parametri komande <see cref="AgentCommands.GetCommandResult"/>.</summary>
	public class GetCommandResultPayload
	{
		/// <summary>ID ranijeg zahteva (za servisnu sesiju: <c>sessionId</c>).</summary>
		public string RequestId { get; set; } = "";

		/// <summary>
		/// Vrati samo upise posle ovog rednog broja (<see cref="KioskStatus.OperationSequence"/>). Zbirna polja rezultata
		/// stižu uvek cela.
		/// </summary>
		public int? AfterSequence { get; set; }
	}

	/// <summary>Granice servisne sesije.</summary>
	public static class RemoteServiceLimits
	{
		public const int MinIdleTimeoutSeconds = 60;
		public const int MaxIdleTimeoutSeconds = 1800;
		public const int MinPresenceCodeLength = 4;
		public const int MaxPresenceCodeLength = 10;
		/// <summary>Koliko kiosk čeka prijavu servisera posle <see cref="AgentCommands.BeginRemoteService"/>.</summary>
		public const int AuthTimeoutSeconds = 120;
		public const int MaxAuthAttempts = 3;
		/// <summary>Najviše upisa (otvaranja i grešaka modula) u jednom rezultatu; ostatak se čita sa <see cref="GetCommandResultPayload.AfterSequence"/>.</summary>
		public const int MaxEntriesPerResult = 200;
	}

	/// <summary>Rezultat servisne sesije (<see cref="AgentResponse.Result"/>) za Begin, End i <see cref="AgentCommands.GetCommandResult"/> sa <c>sessionId</c>.</summary>
	public class RemoteServiceResult
	{
		public string SessionId { get; set; } = "";

		/// <summary><see cref="RemoteServicePhases"/>.</summary>
		public string Phase { get; set; } = "";

		/// <summary><see cref="RemoteServiceEndModes"/>; <c>null</c> dok sesija traje.</summary>
		public string? EndMode { get; set; }

		public DateTimeOffset StartedAtUtc { get; set; }

		public DateTimeOffset? AuthenticatedAtUtc { get; set; }

		public DateTimeOffset? EndedAtUtc { get; set; }

		public int FailedAuthAttempts { get; set; }

		/// <summary>Vrata iz <see cref="BeginRemoteServicePayload.Layout"/> kojih na kiosku nema (iznad <c>LockerBoxCount</c>).</summary>
		public List<int> IgnoredDoors { get; set; } = [];

		/// <summary>Moduli koje kiosk pri početku sesije vidi kao nedostupne.</summary>
		public List<int> UnavailableModulesAtStart { get; set; } = [];

		/// <summary>Vrata koja na kraju sesije nisu javila „zatvoreno" (i ona bez odgovora); <c>null</c> dok sesija traje.</summary>
		public List<int>? DoorsNotClosed { get; set; }

		/// <summary>Otvaranja posle <see cref="GetCommandResultPayload.AfterSequence"/>, redom.</summary>
		public List<DoorOpening> Openings { get; set; } = [];

		/// <summary>Greške modula posle <see cref="GetCommandResultPayload.AfterSequence"/>, redom.</summary>
		public List<ModuleError> ModuleErrors { get; set; } = [];

		/// <summary>Redni broj poslednjeg upisa sesije.</summary>
		public int LastSequence { get; set; }

		/// <summary>Ima još upisa posle poslednjeg vraćenog; čitaju se ponovnim upitom.</summary>
		public bool HasMore { get; set; }
	}

	/// <summary>Rezultat jednog <see cref="AgentCommands.OpenDoors"/> (<see cref="AgentCommands.GetCommandResult"/> sa njegovim <see cref="AgentRequest.RequestId"/>).</summary>
	public class OpenDoorsResult
	{
		public string SessionId { get; set; } = "";

		/// <summary>Otvaranje je završeno (sva tražena vrata imaju ishod).</summary>
		public bool Finished { get; set; }

		public List<DoorOpening> Openings { get; set; } = [];
	}

	/// <summary>Ishod otvaranja jednih vrata u servisnoj sesiji.</summary>
	public class DoorOpening
	{
		/// <summary>Redni broj upisa u sesiji.</summary>
		public int Sequence { get; set; }

		public int DoorNumber { get; set; }

		/// <summary><see cref="DoorOpenSources"/>.</summary>
		public string Source { get; set; } = "";

		/// <summary><see cref="AgentRequest.RequestId"/> komande <see cref="AgentCommands.OpenDoors"/>; <c>null</c> za otvaranje sa ekrana.</summary>
		public string? RequestId { get; set; }

		/// <summary><see cref="DoorOpenOutcomes"/>.</summary>
		public string Outcome { get; set; } = "";

		public DateTimeOffset AtUtc { get; set; }
	}

	/// <summary>Modul (kontroler) koji nije odgovorio tokom servisne sesije.</summary>
	public class ModuleError
	{
		public int Sequence { get; set; }

		public int ModuleNumber { get; set; }

		public DateTimeOffset AtUtc { get; set; }
	}

	/// <summary>Faze servisne sesije (<see cref="RemoteServiceResult.Phase"/>).</summary>
	public static class RemoteServicePhases
	{
		/// <summary>Kiosk čeka da se serviser prijavi kodom na ekranu; ništa se ne otvara.</summary>
		public const string AwaitingTechnician = "AwaitingTechnician";
		public const string Active = "Active";
		public const string Ended = "Ended";
	}

	/// <summary>Načini kraja servisne sesije (<see cref="RemoteServiceResult.EndMode"/>).</summary>
	public static class RemoteServiceEndModes
	{
		/// <summary>Serviser je na ekranu potvrdio da je zatvorio sva vrata.</summary>
		public const string ScreenConfirmed = "ScreenConfirmed";
		/// <summary><see cref="AgentCommands.EndRemoteService"/> posle prijave servisera.</summary>
		public const string PortalConfirmed = "PortalConfirmed";
		/// <summary><see cref="AgentCommands.EndRemoteService"/> pre prijave servisera.</summary>
		public const string PortalCancelled = "PortalCancelled";
		public const string IdleTimeout = "IdleTimeout";
		/// <summary>Serviser se nije prijavio u roku <see cref="RemoteServiceLimits.AuthTimeoutSeconds"/>.</summary>
		public const string AuthTimeout = "AuthTimeout";
		/// <summary>Pogrešan kod <see cref="RemoteServiceLimits.MaxAuthAttempts"/> puta.</summary>
		public const string AuthFailed = "AuthFailed";
		/// <summary>Program kioska je prekinut usred sesije; zaključeno pri sledećem startu.</summary>
		public const string ProcessRestart = "ProcessRestart";
	}

	/// <summary>Ishodi otvaranja jednih vrata (<see cref="DoorOpening.Outcome"/>).</summary>
	public static class DoorOpenOutcomes
	{
		/// <summary>Kontroler je javio otvorena vrata.</summary>
		public const string OpenConfirmed = "OpenConfirmed";
		/// <summary>Kontroler je javio zatvorena vrata.</summary>
		public const string OpenNotConfirmed = "OpenNotConfirmed";
		/// <summary>Komanda je poslata, a odgovor nije stigao: vrata su možda otvorena.</summary>
		public const string OutcomeUnknown = "OutcomeUnknown";
		/// <summary>Komanda nije poslata: modul ne odgovara.</summary>
		public const string ModuleUnavailable = "ModuleUnavailable";
		/// <summary>Komanda nije poslata: kiosk nije mogao da upiše nameru u dnevnik.</summary>
		public const string NotStarted = "NotStarted";
	}

	/// <summary>Odakle je otvaranje traženo (<see cref="DoorOpening.Source"/>).</summary>
	public static class DoorOpenSources
	{
		public const string Portal = "Portal";
		public const string Screen = "Screen";
	}
}
