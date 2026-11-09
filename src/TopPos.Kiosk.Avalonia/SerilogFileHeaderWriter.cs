using System.IO;
using System.Text;
using Serilog.Sinks.File;
using TopPos.Client.WPF;
using TopPos.LockController;

namespace TopPos.Client.Logging
{
	public static class SerilogHooks
    {
		// Full name of this static instance (TopPos.Client.Logging.SerilogHooks.RollingFileHeaderWriter) is referenced from serilog.json
        public static FileHeaderWriter RollingFileHeaderWriter { get; } = new();
    }

	public class FileHeaderWriter : FileLifecycleHooks
	{
		private static bool _headerWrittenThisSession = false;

		public ApplicationContext? ApplicationContext { get; set; }

		public override Stream OnFileOpened(Stream underlyingStream, Encoding encoding)
		{
			bool isNewFile = underlyingStream.Length == 0;
			bool isFirstOpenInSession = !_headerWrittenThisSession;

			if (this.ApplicationContext is not null && (isNewFile || isFirstOpenInSession)) {
				// Make sure the format used to write header lines always matches the format specified in the serilog.json file
				// [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] {Message:lj} {Properties}{NewLine}
				using var writer = new StreamWriter(underlyingStream, encoding, bufferSize: -1, leaveOpen: true);
				writer.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} INF] -------------------------------------------------------------------------------------------------------- {{  }}");
				string versionLabel = string.IsNullOrWhiteSpace(this.ApplicationContext.VersionLabel) ? "" : $" [{this.ApplicationContext.VersionLabel}]";
				writer.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} INF] {this.ApplicationContext.ApplicationName}{versionLabel} v{this.ApplicationContext.ApplicationVersion} {this.ApplicationContext.GitHash} {{  }}");
				writer.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} INF] .NET runtime: {this.ApplicationContext.NetFrameworkDescription} {{  }}");

				string controller = this.ApplicationContext.LockerConfiguration.ControllerType;
				if (controller == LockControllerTypes.Serial) {
					controller += $" (COM port: {this.ApplicationContext.LockerConfiguration.ComPort})";
				}
				else if (controller == LockControllerTypes.NCU16L) {
					controller += $" (Addr: {this.ApplicationContext.LockerConfiguration.TcpController})";
				}
				else {
					controller += $" (UNKNOWN)";
				}

				writer.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} INF] Paketomat: {this.ApplicationContext.LockerConfiguration.LockerId}, Broj pozicija: {this.ApplicationContext.LockerConfiguration.LockerBoxCount}, {controller} {{  }}");
				writer.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} INF] Barkod skener: COM port: [{this.ApplicationContext.BarcodeScannerConfiguration.ComPort}], CRLF terminacija: {this.ApplicationContext.BarcodeScannerConfiguration.CarriageReturnBarcodeTermination} {{  }}");
				writer.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} INF] POS: Instaliran: {this.ApplicationContext.PosClientConfiguration.TerminalInstalled}, COM port: [{this.ApplicationContext.PosClientConfiguration.ComPort}], IP: {this.ApplicationContext.PosClientConfiguration.IpAddress} {{  }}");
				writer.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} INF] TopPos servis: {this.ApplicationContext.TopPosServiceConfiguration.BaseUrl}, Heartbeat: {this.ApplicationContext.TopPosServiceConfiguration.HeartbeatFrequency} {{  }}");
				writer.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} INF] -------------------------------------------------------------------------------------------------------- {{  }}");

				_headerWrittenThisSession = true;
			}

			return base.OnFileOpened(underlyingStream, encoding);
		}
	}
}
