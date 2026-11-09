using System.Text.Json.Serialization;
using Flatsoft.Common;
using TopPos.PenPosService;
using TopPos.Service;

namespace TopPos.Kiosk
{
	public class AppConfiguration
	{
		public GeneralConfiguration General { get; set; } = default!;

		public LockerConfiguration Locker { get; set; } = default!;

		public LockerStatusUpdaterConfiguration LockerStatusUpdater { get; set; } = default!;

		public PaymentConfirmationSenderConfiguration PaymentConfirmationSender { get; set; } = default!;

		public ServerConfirmationSenderConfiguration ServerConfirmationSender { get; set; } = default!;

		public TopPosServiceConfiguration TopPosService { get; set; } = default!;

		public PosTerminalConfiguration PosTerminal { get; set; } = default!;

		public BarcodeScannerConfiguration BarcodeScanner { get; set; } = default!;

		public PenPosServiceConfiguration PenPosService { get; set; } = default!;

		public AlarmConfiguration Alarm { get; set; } = default!;

		public AgentCommunicationConfiguration AgentCommunication { get; set; } = default!;

		public ScreenResolutionConfiguration? ScreenResolution { get; set; } = null;

		/// <summary>
		/// Priprema učitanu konfiguraciju za korišćenje:
		/// <list type="bullet">
		/// <item><b>Obavezne, uređaj-specifične sekcije</b> (General, Locker, TopPosService, PosTerminal,
		/// BarcodeScanner) — ako nedostaju, baca izuzetak sa jasnom porukom. Pogrešna podrazumevana
		/// vrednost ovde (npr. tuđi LockerId ili COM port) je opasna, pa se NE izmišlja.</item>
		/// <item><b>Opcione sekcije sa isključivo konstantnim sadržajem</b> (LockerStatusUpdater,
		/// PaymentConfirmationSender, ServerConfirmationSender, PenPosService, Alarm, AgentCommunication, PosTerminal.Logging,
		/// PosTerminal.ProtocolV1) — ako
		/// nedostaju ili su <c>null</c> (starije konfiguracije ih nemaju), popunjavaju se podrazumevanim vrednostima.</item>
		/// <item><b>PosTerminal.InputV2.AmountCurrency</b> — šifra valute zavisi od tržišta, pa se ne izmišlja: kad je
		/// terminal instaliran, mora postojati (inače izuzetak).</item>
		/// </list>
		/// Vraća imena sekcija koje su popunjene podrazumevanim vrednostima, da bi pozivalac mogao da
		/// upiše upozorenje u log (drift se vidi, ne skriva se).
		/// </summary>
		public IReadOnlyList<string> NormalizeAndValidate()
		{
			var missingRequired = new List<string>();
			if (General is null) missingRequired.Add(nameof(General));
			if (Locker is null) missingRequired.Add(nameof(Locker));
			if (TopPosService is null) missingRequired.Add(nameof(TopPosService));
			if (PosTerminal is null) missingRequired.Add(nameof(PosTerminal));
			if (BarcodeScanner is null) missingRequired.Add(nameof(BarcodeScanner));
			if (missingRequired.Count > 0)
				throw new Exception($"Konfiguracija nije ispravna. Nedostaju obavezne sekcije: {string.Join(", ", missingRequired)}. Dodajte ih u app-configuration.json.");
			if (PosTerminal.TerminalInstalled && string.IsNullOrWhiteSpace(PosTerminal.InputV2?.AmountCurrency))
				throw new Exception($"Konfiguracija nije ispravna. POS terminal je instaliran, a nedostaje {nameof(PosTerminal)}.{nameof(PosTerminalConfiguration.InputV2)}.{nameof(InputV2Configuration.AmountCurrency)} (šifra valute, npr. \"941\" za RSD). Dodajte je u app-configuration.json.");

			var defaulted = new List<string>();
			if (LockerStatusUpdater is null) { LockerStatusUpdater = new(); defaulted.Add(nameof(LockerStatusUpdater)); }
			if (PaymentConfirmationSender is null) { PaymentConfirmationSender = new(); defaulted.Add(nameof(PaymentConfirmationSender)); }
			if (ServerConfirmationSender is null) { ServerConfirmationSender = new(); defaulted.Add(nameof(ServerConfirmationSender)); }
			if (PenPosService is null) { PenPosService = new(); defaulted.Add(nameof(PenPosService)); }
			if (Alarm is null) { Alarm = new(); defaulted.Add(nameof(Alarm)); }
			// Podrazumevano isključeno. Vrednosti uključene sekcije se proveravaju posle loggera (GetValidationErrors) i ne
			// obaraju start — neispravna sekcija samo ne pokreće kanal ka Agentu.
			if (AgentCommunication is null) { AgentCommunication = new(); defaulted.Add(nameof(AgentCommunication)); }
			// POS podsekcije čita PosClient bez provere; podrazumevano: ECR logovanje isključeno, bez poruke 13.
			if (PosTerminal.Logging is null) { PosTerminal.Logging = new(); defaulted.Add($"{nameof(PosTerminal)}.{nameof(PosTerminalConfiguration.Logging)}"); }
			if (PosTerminal.ProtocolV1 is null) { PosTerminal.ProtocolV1 = new(); defaulted.Add($"{nameof(PosTerminal)}.{nameof(PosTerminalConfiguration.ProtocolV1)}"); }
			return defaulted;
		}
	}

	public class ScreenResolutionConfiguration
	{
		public int Width { get; set; }

		public int Height { get; set; }
	}

	public class GeneralConfiguration
	{
		public int OperationTimeout { get; set; }

		// Uklonjeno: rok plaćanja određuje POS (FunctionTimeout u ECR-u), a raniji Start(PaymentTimeout) je odmah zaustavljala
		// promena stranice. Ključ koji je ostao u app-configuration.json na paketomatima se pri čitanju preskače.
		//public int PaymentTimeout { get; set; }

		public int UserMessageDisplayTimeInSeconds { get; set; } = 5;

		public int CourierMessageDisplayTimeInSeconds { get; set; } = 3;

		public int MinimumUnlockCodeLength { get; set; } = default;
		public int MaximumUnlockCodeLength { get; set; } = 10;
		public string CurrencyDisplayName { get; set; } = default!;	
		public int CurrencyDecimalPlaces { get; set; } = 2;

		public bool PlayButtonPressedSound { get; set; } = true;

		public string ExternalTerminalPaymentMessageId { get; set; } = default!;
		public string Brand {  get; set; } = default!;

		public bool ConfirmPackagePickup {  get; set; }
		public bool ConfirmPackageDeposition {  get; set; }

		// Sender (pošiljalac) deposit
		public bool EnablePackageDeposition { get; set; }                       // prikazuje "Ostavi paket" dugme na Start ekranu
		public string SenderDepositCourierCode { get; set; } = default!;        // šifra koja se šalje serveru za sender deposit pozive

		public int DoorStatusResponseTimeout { get; set; } = 1000;

		public bool EnableCourierManualLogin { get; set; } 

		public bool EnableLockerBoxAvailabilityPage { get; set; }

		public string? DefaultRecipientPhoneNumber { get; set; }
		public bool ShowLockerIdQrCode { get; set; } = false;							// Da li treba prikazati QR kod na StartPage?


		// Run-time settings
		[JsonIgnore]
		public bool IsBarcodeScannerAvailable { get; set; }
		
		[JsonIgnore]
		public bool IsPOSTerminalAvailable { get; set; }

        [JsonIgnore]
        public bool IsPenPOSDeviceAvailable { get; set; }
    }

    public class LockerConfiguration
	{
		public string LockerId { get; set; } = default!;

		public int LockerBoxCount { get; set; }

		public int ServiceDoorNumber { get; set; }

		public string? ServiceDoorCode { get; set; }

		public int ReadWriteTimeout { get; set; }
		public int DoorWatcherFrequency { get; set; }
		public int GetMultipleDoorStatusCooldown { get; set; }

		// Lock Controller Configuration
		public string ControllerType { get; set; } = "Serial";
		public ComPortConfiguration ComPort { get; set; } = default!;
		public TcpControllerConfiguration TcpController { get; set; } = default!;
	}

	public class LockerStatusUpdaterConfiguration
	{
        // Podrazumevana vrednost — sadrži samo konstante (nema ničeg specifičnog za uređaj),
        // pa je bezbedno koristiti je kad starija konfiguracija nema ovu sekciju.
        public string ServerStatusFilename { get; set; } = "ServerStatus.json";
		public int? ProcessingDelayInMilliseconds { get; set; }
    }

	public class PaymentConfirmationSenderConfiguration
	{
        public string PaymentConfirmationFilename { get; set; } = "PaymentConfirmation.json";
		public int? ProcessingDelayInMilliseconds { get; set; }
    }

	// Trajni red potvrda preuzimanja i ostavljanja koje nisu stigle do servera zbog prolazne greške (ServerConfirmationSender).
	public class ServerConfirmationSenderConfiguration
	{
		public string ServerConfirmationFilename { get; set; } = "ServerConfirmations.json";
		public int? ProcessingDelayInMilliseconds { get; set; }
	}

	public class PosTerminalConfiguration
	{
		public bool TerminalInstalled { get; set; }

		public ProductKeyConfiguration ProductKey { get; set; } = default!;

		public ComPortConfiguration ComPort { get; set; } = default!;

		public string IpAddress { get; set; } = default!;

		public int RetryTimeout { get; set; } 

		public int WaitForAckTimeout { get; set; }

		public int FunctionTimeout { get; set; }

		public LoggingConfiguration? Logging { get; set; }

		public ProtocolV1Configuration? ProtocolV1 { get; set; }

		public InputV2Configuration? InputV2 { get; set; }

		public string Encoding { get; set; } = default!;
	}

	public class BarcodeScannerConfiguration
	{
		public ComPortConfiguration ComPort { get; set; } = default!;
		public int MinimumBarcodeLength { get; set; } 
		public bool CarriageReturnBarcodeTermination { get; set; } = true;
		public int ReadThresholdInMilliseconds { get; set; } = 300;
        public int ScanTrackerTimeoutInMilliseconds { get; set; } = 60000;
    }

	public class ProductKeyConfiguration
	{
		public string KeyE { get; set; } = default!;

		public string KeyW { get; set; } = default!;
	}

	public class LoggingConfiguration
	{
		public bool Enabled { get; set; }

		public bool Detailed { get; set; }
	}

	public class ProtocolV1Configuration
	{
		public bool SendRespMessage13 { get; set; }
	}

	public class InputV2Configuration
	{
		public string AmountCurrency { get; set; } = default!;
	}

	/// <summary>
	/// Lokalni kanal ka Agentu (named pipe; doc/notes/agent-client-komunikacija-i-komande-plan.md, §8). Podrazumevano
	/// isključen; uključivanje je deployment promena. Dozvoljeni klijent je uvek LocalSystem i nije podešavanje.
	/// Tip mora ostati ovde: Configurator linkuje ovaj fajl i pri snimanju briše sekcije koje ne poznaje.
	/// </summary>
	public class AgentCommunicationConfiguration
	{
		public bool Enabled { get; set; }

		/// <summary>Ime pipe-a; mora se poklapati sa Agentom (podrazumevano <c>TopPos.Kiosk.Agent.v1</c>).</summary>
		public string PipeName { get; set; } = "TopPos.Kiosk.Agent.v1";

		public int HeartbeatIntervalInMilliseconds { get; set; } = 5000;

		/// <summary>Rok za potvrdu UI niti pre heartbeat-a; mora biti kraći od intervala.</summary>
		public int UiAckTimeoutInMilliseconds { get; set; } = 2000;

		public int HandshakeTimeoutInMilliseconds { get; set; } = 5000;

		public int WriteTimeoutInMilliseconds { get; set; } = 5000;

		public int MaxMessageBytes { get; set; } = 65536;

		public int MaxPendingResponses { get; set; } = 16;

		/// <summary>Folder trajnog dnevnika operacija Agenta (ishodi, oporavak posle pada); relativan prema radnom folderu.</summary>
		public string OperationJournalFolder { get; set; } = "AgentOperations";

		/// <summary>Greške vrednosti (prazno = ispravno). Proverava se samo kad je kanal uključen.</summary>
		public IReadOnlyList<string> GetValidationErrors()
		{
			var errors = new List<string>();
			if (string.IsNullOrWhiteSpace(PipeName) || PipeName.Length > 200 || PipeName.IndexOfAny(['\\', '/', ':']) >= 0)
				errors.Add($"AgentCommunication.{nameof(PipeName)} mora biti neprazno ime do 200 znakova, bez \\, / i :.");
			if (HeartbeatIntervalInMilliseconds is < 1000 or > 60000)
				errors.Add($"AgentCommunication.{nameof(HeartbeatIntervalInMilliseconds)} mora biti 1000..60000.");
			if (UiAckTimeoutInMilliseconds < 100 || UiAckTimeoutInMilliseconds >= HeartbeatIntervalInMilliseconds)
				errors.Add($"AgentCommunication.{nameof(UiAckTimeoutInMilliseconds)} mora biti najmanje 100 i manji od {nameof(HeartbeatIntervalInMilliseconds)}.");
			if (HandshakeTimeoutInMilliseconds is < 1000 or > 60000)
				errors.Add($"AgentCommunication.{nameof(HandshakeTimeoutInMilliseconds)} mora biti 1000..60000.");
			if (WriteTimeoutInMilliseconds is < 1000 or > 60000)
				errors.Add($"AgentCommunication.{nameof(WriteTimeoutInMilliseconds)} mora biti 1000..60000.");
			if (MaxMessageBytes is < 4096 or > 1048576)
				errors.Add($"AgentCommunication.{nameof(MaxMessageBytes)} mora biti 4096..1048576.");
			if (MaxPendingResponses is < 1 or > 1000)
				errors.Add($"AgentCommunication.{nameof(MaxPendingResponses)} mora biti 1..1000.");
			if (string.IsNullOrWhiteSpace(OperationJournalFolder) || OperationJournalFolder.IndexOfAny(System.IO.Path.GetInvalidPathChars()) >= 0)
				errors.Add($"AgentCommunication.{nameof(OperationJournalFolder)} mora biti ispravna putanja foldera.");
			return errors;
		}

	}

	public class AlarmConfiguration
	{
		public bool Active { get; set; }

		public string DeactivationCode { get; set; } = default!;

		public string DeactivationCodeWithLocker { get; set; } = default!;
	}
}


