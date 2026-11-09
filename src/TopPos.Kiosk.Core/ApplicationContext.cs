using TopPos.Service;

namespace TopPos.Kiosk
{
	// Used for writing header log entries into each rolling log file
	public class ApplicationContext
    {
		public string? ApplicationName { get; set; }
		public string? VersionLabel { get; set; }
		public string? ApplicationVersion { get; set; }
		public string? GitHash { get; set; }
		public string? NetFrameworkDescription { get; set; }
		public LockerConfiguration LockerConfiguration { get; internal set; }
		public TopPosServiceConfiguration TopPosServiceConfiguration { get; internal set; }
		public PosTerminalConfiguration PosClientConfiguration { get; internal set; }
		public BarcodeScannerConfiguration BarcodeScannerConfiguration { get; internal set; }
	}
}
