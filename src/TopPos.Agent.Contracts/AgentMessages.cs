using System.Text.Json;

namespace TopPos.Agent.Contracts
{
	/// <summary>Zajednička polja svake poruke. <see cref="Type"/> određuje konkretan tip (<see cref="AgentMessageTypes"/>).</summary>
	public class AgentMessage
	{
		public int ProtocolVersion { get; set; } = AgentProtocol.Version;

		public string Type { get; set; } = "";
	}

	/// <summary>Prva poruka klijenta (Agent → kiosk). Kiosk tek posle nje proverava identitet klijenta i odgovara sa <see cref="ServerHello"/>.</summary>
	public class ClientHello : AgentMessage
	{
		public ClientHello() => Type = AgentMessageTypes.ClientHello;

		/// <summary>Verzija Agenta, za log kioska.</summary>
		public string? AgentVersion { get; set; }
	}

	/// <summary>Odgovor kioska na <see cref="ClientHello"/> posle uspešne provere identiteta klijenta.</summary>
	public class ServerHello : AgentMessage
	{
		public ServerHello() => Type = AgentMessageTypes.Hello;

		public string LockerId { get; set; } = "";

		/// <summary>Slučajni identitet tekućeg kiosk procesa; nov pri svakom startu.</summary>
		public string ClientInstanceId { get; set; } = "";

		public string? ApplicationVersion { get; set; }

		/// <summary>Koliko često kiosk šalje <see cref="AgentEvents.Heartbeat"/> pretplatniku; Agent iz njega izvodi prag zastarelosti.</summary>
		public int HeartbeatIntervalMs { get; set; }

		/// <summary>Komande koje ova instanca kioska prihvata (<see cref="AgentCommands"/>).</summary>
		public List<string> SupportedCommands { get; set; } = [];
	}

	/// <summary>Zahtev Agenta.</summary>
	public class AgentRequest : AgentMessage
	{
		public AgentRequest() => Type = AgentMessageTypes.Request;

		/// <summary>ID zahteva (UUID); odgovor nosi isti ID. Za komande sa portala je to <c>CommandId</c> sa servera.</summary>
		public string RequestId { get; set; } = "";

		public string LockerId { get; set; } = "";

		/// <summary>Očekivani identitet kiosk procesa iz <see cref="ServerHello"/>; obavezan za mutirajuće komande.</summary>
		public string? ClientInstanceId { get; set; }

		public string Command { get; set; } = "";

		public DateTimeOffset? IssuedAtUtc { get; set; }

		public DateTimeOffset? ExpiresAtUtc { get; set; }

		/// <summary>Parametri komande (npr. <see cref="OpenDoorsPayload"/> za <see cref="AgentCommands.OpenDoors"/>).</summary>
		public JsonElement? Payload { get; set; }

		/// <summary>Razlog (audit), bez tajni.</summary>
		public string? Reason { get; set; }
	}

	/// <summary>Odgovor kioska na <see cref="AgentRequest"/>.</summary>
	public class AgentResponse : AgentMessage
	{
		public AgentResponse() => Type = AgentMessageTypes.Response;

		public string RequestId { get; set; } = "";

		public string LockerId { get; set; } = "";

		public string ClientInstanceId { get; set; } = "";

		public string Command { get; set; } = "";

		/// <summary><see cref="AgentResultCodes"/>.</summary>
		public string ResultCode { get; set; } = "";

		/// <summary>Bezbedna poruka za log/prikaz; bez tajni.</summary>
		public string? Message { get; set; }

		public DateTimeOffset ServerTimeUtc { get; set; }

		/// <summary>ID pretplate (samo za <see cref="AgentCommands.SubscribeStatus"/>).</summary>
		public string? SubscriptionId { get; set; }

		/// <summary>Snapshot statusa (GetStatus, SubscribeStatus).</summary>
		public KioskStatus? Status { get; set; }

		/// <summary>
		/// Rezultat komande, uvek jedan od tipova opisanih uz komandu (npr. <see cref="RemoteServiceResult"/>); za
		/// <see cref="AgentCommands.GetCommandResult"/> tip zavisi od komande traženog zahteva. Čita se sa
		/// <see cref="AgentJson.ReadResult{T}"/>.
		/// </summary>
		public JsonElement? Result { get; set; }
	}

	/// <summary>Nezatraženo obaveštenje kioska (<see cref="AgentEvents"/>). Nosi kompletan snapshot, ne razliku.</summary>
	public class AgentEvent : AgentMessage
	{
		public AgentEvent() => Type = AgentMessageTypes.Event;

		public string EventId { get; set; } = "";

		public string Event { get; set; } = "";

		public string LockerId { get; set; } = "";

		public string ClientInstanceId { get; set; } = "";

		public string? SubscriptionId { get; set; }

		public KioskStatus? Status { get; set; }
	}

	/// <summary>
	/// Snapshot statusa kioska. <see cref="Busy"/>=false znači samo da kiosk niko ne koristi — ne i da je ispravan ili
	/// spreman; za odluku o otvaranju važi <see cref="CanAcceptOpenDoor"/> (i svežina veze na strani Agenta).
	/// </summary>
	public class KioskStatus
	{
		public string LockerId { get; set; } = "";

		public string ClientInstanceId { get; set; } = "";

		/// <summary>Monotono rastući broj promene unutar instance. Heartbeat ga ne menja.</summary>
		public long StatusRevision { get; set; }

		/// <summary>Vreme poslednje promene sadržaja.</summary>
		public DateTimeOffset ChangedAtUtc { get; set; }

		/// <summary>Vreme ovog očitavanja.</summary>
		public DateTimeOffset ObservedAtUtc { get; set; }

		public bool Busy { get; set; }

		/// <summary>Uloga vlasnika dok je kiosk zauzet: <c>Client</c>, <c>Courier</c>, <c>Service</c>, <c>Agent</c>; prazno kad je slobodan.</summary>
		public string Role { get; set; } = "";

		public string WorkflowState { get; set; } = "";

		/// <summary>ID aktivne operacije Agenta (za servisnu sesiju <c>sessionId</c>); <c>null</c> kad je nema.</summary>
		public string? ActiveOperationId { get; set; }

		/// <summary>Vrsta aktivne operacije, npr. <c>RemoteService</c>; bez korisničkih kodova i drugih tajni.</summary>
		public string? Activity { get; set; }

		/// <summary>
		/// Redni broj poslednjeg upisa u dnevnik aktivne operacije. Kad se promeni, detalji se čitaju sa
		/// <see cref="AgentCommands.GetCommandResult"/>; statusni događaji se spajaju, pa sam snapshot ne nosi svaki upis.
		/// </summary>
		public int? OperationSequence { get; set; }

		/// <summary>Operativnost hardvera: <c>true</c>, <c>false</c>, <c>null</c> = još nepoznato.</summary>
		public bool? Operational { get; set; }

		/// <summary><see cref="KioskLifecycles"/>.</summary>
		public string Lifecycle { get; set; } = "";

		public bool CanAcceptOpenDoor { get; set; }

		/// <summary><see cref="CannotAcceptReasons"/>; <c>null</c> kad je <see cref="CanAcceptOpenDoor"/>=true.</summary>
		public string? CannotAcceptReason { get; set; }
	}
}
