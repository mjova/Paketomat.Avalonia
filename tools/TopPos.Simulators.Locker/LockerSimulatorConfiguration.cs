using Flatsoft.Common;

namespace TopPos.LockerSimulator
{
	public class LockerSimulatorConfiguration
	{
		public LockerConfiguration Locker { get; set; } = default!;

		public LockControllerConfiguration LockController { get; set; } = default!;

		/// <summary>v4: opciono — bez ove sekcije skener se ne simulira (<c>/_sim/barcode</c> vraća 404).</summary>
		public BarcodeScannerConfiguration? BarcodeScanner { get; set; }

		/// <summary>Opciono — bez ove sekcije POS simulator se ne pokreće.</summary>
		public PosTerminalSimulatorConfiguration? PosTerminal { get; set; }

		/// <summary>Opciono — kontrolni API za E2E testove (/_sim/*); bez ove sekcije je isključen.</summary>
		public ControlApiConfiguration? ControlApi { get; set; }
	}

	public class ControlApiConfiguration
	{
		/// <summary>HTTP port na localhost-u.</summary>
		public int Port { get; set; } = 5299;
	}

	public class PosTerminalSimulatorConfiguration
	{
		/// <summary>Druga strana virtuelnog para (npr. com0com COM20 ↔ COM21); kiosk koristi prvu.</summary>
		public ComPortConfiguration ComPort { get; set; } = default!;

		/// <summary>
		/// v4, Linux: putanja koju otvara kiosk (npr. /tmp/toppos-sim/ttyPOS). Simulator tada sam napravi virtuelni par
		/// (<see cref="TopPos.Simulators.Serial.PtySerialPair"/>, kao com0com) i otvara svoju stranu, pa se <c>ComPort.PortName</c>
		/// ne koristi. Na Windows-u par je u com0com-u, a ovo polje se odbija.
		/// </summary>
		public string? KioskPortName { get; set; }

		public string Pin { get; set; } = "1234";

		public string Tid { get; set; } = "SIMT0001";

		public int CardWaitTimeoutSeconds { get; set; } = 30;

		/// <summary>Množilac pauza između poruka terminala (1 = tajming iz snimljenih logova).</summary>
		public double SpeedFactor { get; set; } = 1.0;
	}

	public class LockerConfiguration
	{
		public int ModuleCount { get; set; }

		public int BoxCountPerModule { get; set; }
	}

	public class LockControllerConfiguration
	{
		public string Type { get; set; } = "Serial";  // "Serial" or "NCU24L"

		public ComPortConfiguration ComPort { get; set; } = default!;

		/// <summary>
		/// v4, Linux: putanja koju otvara kiosk (npr. /tmp/toppos-sim/ttyPOS). Simulator tada sam napravi virtuelni par
		/// (<see cref="TopPos.Simulators.Serial.PtySerialPair"/>, kao com0com) i otvara svoju stranu, pa se <c>ComPort.PortName</c>
		/// ne koristi. Na Windows-u par je u com0com-u, a ovo polje se odbija.
		/// </summary>
		public string? KioskPortName { get; set; }

		public TcpControllerConfiguration? TcpController { get; set; }

		/// <summary>
		/// Adresa na kojoj NCU24L TCP server sluša. Bez vrednosti: svi interfejsi (kiosk sa druge mašine može da se poveže).
		/// "127.0.0.1": samo lokalno — Windows Firewall tada ne pita za dozvolu (E2E pokreće simulator iz novog foldera
		/// za svaki test, pa bi pitao svaki put).
		/// </summary>
		public string? ListenAddress { get; set; }

		/// <summary>Ponašanje mrežnog modula ispred NCU24L; bez sekcije važe vrednosti izmerene na terenu.</summary>
		public NetworkModuleConfiguration NetworkModule { get; set; } = new();
	}

	/// <summary>
	/// ZLG mrežni modul ispred NCU24L kontrolera, kako se ponašao na terenu (paketomat 112, 2026-10-16,
	/// NCU.FieldTest). <see cref="Enabled"/> = false daje idealan kontroler (više veza, odgovor odmah).
	/// </summary>
	public class NetworkModuleConfiguration
	{
		public bool Enabled { get; set; } = true;

		/// <summary>Prima jednu vezu; dok ona traje, ostale se odbijaju („refused", ping i dalje radi).</summary>
		public bool SingleClient { get; set; } = true;

		/// <summary>Posle zatvaranja veze nova se prima tek posle ove pauze (teren: ~1,5–2 s).</summary>
		public int ReleaseDelayMs { get; set; } = 1500;

		/// <summary>Koliko posle komande stiže odgovor (teren: ~30 ms).</summary>
		public int ResponseDelayMs { get; set; } = 30;

		/// <summary>Komanda koja stigne dok se prethodna obrađuje se gubi (teren: dve komande bez pauze → jedan odgovor).</summary>
		public bool DropCommandWhileBusy { get; set; } = true;

		/// <summary>Ispred prvog odgovora na vezi modul pošalje bajt 0x00.</summary>
		public bool FirstResponsePrefix { get; set; } = true;

		/// <summary>
		/// Veza zatvorena dok odgovor čeka: modul toliko dugo odbija svaku vezu (teren: ≥2 min). 0 = bez blokade.
		/// Važi samo uz <see cref="SingleClient"/>.
		/// </summary>
		public int LockAfterAbandonedMs { get; set; } = 120000;
	}

	public class BarcodeScannerConfiguration
	{
		public ComPortConfiguration ComPort { get; set; } = default!;

		/// <summary>
		/// v4, Linux: putanja koju otvara kiosk (npr. /tmp/toppos-sim/ttyPOS). Simulator tada sam napravi virtuelni par
		/// (<see cref="TopPos.Simulators.Serial.PtySerialPair"/>, kao com0com) i otvara svoju stranu, pa se <c>ComPort.PortName</c>
		/// ne koristi. Na Windows-u par je u com0com-u, a ovo polje se odbija.
		/// </summary>
		public string? KioskPortName { get; set; }

		public bool CarriageReturnBarcodeTermination { get; set; } = true;
	}
}
