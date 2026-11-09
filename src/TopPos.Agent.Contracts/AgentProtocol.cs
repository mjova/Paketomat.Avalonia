namespace TopPos.Agent.Contracts
{
	/// <summary>
	/// Konstante lokalnog kanala Agent↔kiosk. Kiosk (TopPos.Client.WPF) je server named pipe-a, Agent je klijent; obe
	/// strane rade na istom računaru (ODL-050). Poruka je frame: 4 bajta dužine (little-endian) + UTF-8 JSON.
	/// </summary>
	public static class AgentProtocol
	{
		/// <summary>Verzija protokola. Menja se samo uz nekompatibilnu izmenu poruka; nova polja ne menjaju verziju.</summary>
		public const int Version = 1;

		/// <summary>Podrazumevano ime pipe-a. Nije tajna niti dokaz identiteta; verzija u imenu odvaja nekompatibilne kanale.</summary>
		public const string DefaultPipeName = "TopPos.Kiosk.Agent.v1";

		/// <summary>Najveća dužina JSON tela jedne poruke (bez 4 bajta zaglavlja).</summary>
		public const int MaxMessageBytes = 64 * 1024;

		/// <summary>Najveća dubina JSON-a.</summary>
		public const int MaxJsonDepth = 16;

		/// <summary>Najveća dužina prve poruke klijenta (<see cref="ClientHello"/>), pre provere identiteta.</summary>
		public const int MaxClientHelloBytes = 4 * 1024;

		/// <summary>Najduži rok zahteva koji menja stanje kioska: <see cref="AgentRequest.ExpiresAtUtc"/> − <see cref="AgentRequest.IssuedAtUtc"/>.</summary>
		public const int MaxRequestTtlSeconds = 30;

		/// <summary>Dozvoljeno odstupanje sata pri proveri <see cref="AgentRequest.IssuedAtUtc"/> i <see cref="AgentRequest.ExpiresAtUtc"/>.</summary>
		public const int MaxClockSkewSeconds = 5;
	}

	/// <summary>Vrednosti polja <see cref="AgentMessage.Type"/>.</summary>
	public static class AgentMessageTypes
	{
		/// <summary>Prva poruka klijenta. Server je čita pre provere identiteta (Windows dozvoljava proveru tek posle prvog upisa).</summary>
		public const string ClientHello = "ClientHello";
		/// <summary>Odgovor servera na <see cref="ClientHello"/>, posle uspešne provere identiteta klijenta.</summary>
		public const string Hello = "Hello";
		public const string Request = "Request";
		public const string Response = "Response";
		public const string Event = "Event";
	}

	/// <summary>Komande (<see cref="AgentRequest.Command"/>).</summary>
	public static class AgentCommands
	{
		/// <summary>Trenutni snapshot statusa. Ne rezerviše kiosk.</summary>
		public const string GetStatus = "GetStatus";
		/// <summary>Pretplata na <see cref="AgentEvents.StatusChanged"/>; odgovor nosi početni snapshot.</summary>
		public const string SubscribeStatus = "SubscribeStatus";
		/// <summary>
		/// Otvaranje jednih vrata. Zamenjeno servisnom sesijom (<see cref="BeginRemoteService"/>, <see cref="OpenDoors"/>);
		/// kiosk ga ne podržava.
		/// </summary>
		public const string OpenDoor = "OpenDoor";
		/// <summary>
		/// Ishod ranijeg zahteva po ID-u (<see cref="GetCommandResultPayload"/>), iz trajnog dnevnika kioska. Samo čita i nikad ne
		/// ponavlja izvršavanje; radi i posle restarta kioska.
		/// </summary>
		public const string GetCommandResult = "GetCommandResult";
		/// <summary>
		/// Početak servisne sesije (<see cref="BeginRemoteServicePayload"/>): kiosk prikazuje servisni ekran i čeka prijavu
		/// servisera; odgovor nosi <see cref="RemoteServiceResult"/>.
		/// </summary>
		public const string BeginRemoteService = "BeginRemoteService";
		/// <summary>
		/// Otvaranje vrata u servisnoj sesiji (<see cref="OpenDoorsPayload"/>). Odgovor je <see cref="AgentResultCodes.InProgress"/>;
		/// ishod se čita sa <see cref="GetCommandResult"/>.
		/// </summary>
		public const string OpenDoors = "OpenDoors";
		/// <summary>Kraj servisne sesije (<see cref="EndRemoteServicePayload"/>); odgovor nosi konačan <see cref="RemoteServiceResult"/>.</summary>
		public const string EndRemoteService = "EndRemoteService";
		/// <summary>
		/// Preuzimanje paketa umesto primaoca (<see cref="RecipientPickupPayload"/>): kiosk proverava podatke na serveru i otvara
		/// vrata tek kad korisnik na ekranu potvrdi. Odgovor je <see cref="AgentResultCodes.InProgress"/>; faze i ishod
		/// (<see cref="RecipientPickupResult"/>) se čitaju sa <see cref="GetCommandResult"/>.
		/// </summary>
		public const string RecipientPickup = "RecipientPickup";
		/// <summary>
		/// Uredan izlazak iz programa kioska (<see cref="ShutdownApplicationPayload"/>). Menja stanje kioska: prihvata se samo
		/// kad je kiosk slobodan, a <see cref="AgentResultCodes.Completed"/> stiže pre zatvaranja veze.
		/// </summary>
		public const string ShutdownApplication = "ShutdownApplication";
	}

	/// <summary>Vrste događaja (<see cref="AgentEvent.Event"/>). Oba nose kompletan snapshot.</summary>
	public static class AgentEvents
	{
		/// <summary>Promena statusa (nova revizija).</summary>
		public const string StatusChanged = "StatusChanged";
		/// <summary>Potvrda da je kiosk živ: ista revizija, novo <see cref="KioskStatus.ObservedAtUtc"/>.</summary>
		public const string Heartbeat = "Heartbeat";
	}

	/// <summary>Stabilni kodovi rezultata (<see cref="AgentResponse.ResultCode"/>).</summary>
	public static class AgentResultCodes
	{
		public const string Completed = "Completed";
		public const string InProgress = "InProgress";
		public const string Busy = "Busy";
		public const string NotReady = "NotReady";
		public const string NotOperational = "NotOperational";
		public const string Unauthorized = "Unauthorized";
		public const string InvalidRequest = "InvalidRequest";
		public const string InvalidDoor = "InvalidDoor";
		public const string UnsupportedVersion = "UnsupportedVersion";
		public const string UnsupportedCommand = "UnsupportedCommand";
		public const string Expired = "Expired";
		public const string WrongInstance = "WrongInstance";
		public const string DuplicateConflict = "DuplicateConflict";
		public const string OpenNotConfirmed = "OpenNotConfirmed";
		public const string OutcomeUnknown = "OutcomeUnknown";
		/// <summary>Kiosk čeka oporavak (<see cref="KioskLifecycles.RecoveryRequired"/>) i ne prima komande koje menjaju stanje.</summary>
		public const string RecoveryRequired = "RecoveryRequired";
		public const string ShuttingDown = "ShuttingDown";
		public const string InternalError = "InternalError";
		public const string ResultNotFound = "ResultNotFound";
		/// <summary>Servisna sesija sa zadatim <c>sessionId</c> ne postoji ili je završena.</summary>
		public const string NoServiceSession = "NoServiceSession";
		/// <summary>Serviser se još nije prijavio kodom na ekranu kioska (<see cref="RemoteServicePhases.AwaitingTechnician"/>).</summary>
		public const string TechnicianNotPresent = "TechnicianNotPresent";
	}

	/// <summary>Životni ciklus kiosk procesa (<see cref="KioskStatus.Lifecycle"/>).</summary>
	public static class KioskLifecycles
	{
		public const string Initializing = "Initializing";
		public const string Ready = "Ready";
		public const string ShuttingDown = "ShuttingDown";
		public const string RecoveryRequired = "RecoveryRequired";
	}

	/// <summary>Razlog zašto kiosk trenutno ne može da primi OpenDoor (<see cref="KioskStatus.CannotAcceptReason"/>).</summary>
	public static class CannotAcceptReasons
	{
		public const string Busy = "Busy";
		public const string NotReady = "NotReady";
		public const string NotOperational = "NotOperational";
		public const string ShuttingDown = "ShuttingDown";
		public const string RecoveryRequired = "RecoveryRequired";
		/// <summary>Kiosk ove verzije ne prima fizičke komande (OpenDoor još nije implementiran).</summary>
		public const string NotSupported = "NotSupported";
	}
}
