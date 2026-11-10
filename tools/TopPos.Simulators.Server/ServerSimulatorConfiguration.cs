namespace TopPos.ServerSimulator
{
	public class ServerSimulatorConfiguration
	{
		public ServerConfiguration Server { get; set; } = new();
		public string DefaultCourierCode { get; set; } = "K001";
		// QR kodovi kurira koje KurirAutorizacija prihvata. Prazna lista = prihvata se svaki kod (ručni rad).
		public List<string> CourierQrCodes { get; set; } = [];
		public string PaketomatId { get; set; } = "111";
		public int DoorCount { get; set; } = 48;
		public PenPosSimulatorConfiguration PenPos { get; set; } = new();
	}

	// PenPOS (ESIR/LPFR) simulator: /pin i /invoice na istom portu (vidi HttpServer).
	public class PenPosSimulatorConfiguration
	{
		// PIN koji /pin prihvata. Null = prihvata se svaki PIN.
		public string? Pin { get; set; }
	}

	public class ServerConfiguration
	{
		public int Port { get; set; } = 5100;
		public string PathPrefix { get; set; } = "/Api";
		// Na kom hostu/IP-u HttpListener slusa. Primeri:
		//   "localhost" - samo lokalni pristup (ne zahteva admin/urlacl)
		//   "192.168.0.10" - konkretna LAN IP adresa (zahteva netsh urlacl rezervaciju ili admin)
		//   "+" ili "*" - svi interfejsi (zahteva netsh urlacl rezervaciju ili admin)
		public string BindAddress { get; set; } = "localhost";
		// Kontrolni API za E2E testove (/_sim/*): dodavanje paketa, evidencija primljenih poziva.
		// Podrazumevano isključen; uključuje ga samo e2e konfiguracija.
		public bool EnableControlApi { get; set; }
	}
}
