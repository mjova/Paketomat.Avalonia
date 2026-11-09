using System.IO.Ports;

namespace Flatsoft.Common
{
	public class ComPortConfiguration
	{
		public string PortName { get; set; } = default!;

		public int BaudRate { get; set; }

		public int DataBits { get; set; }

		public StopBits? StopBits { get; set; }

		public string Parity { get; set; } = default!;

		public int? ReceivedBytesThreshold { get; set; } = 2;

		public int? RetryCount { get; set; } = 1;

		/// <summary>
		/// Maximum delay in milliseconds between reconnection attempts when the port is unavailable.
		/// Default: 30000 (30 seconds).
		/// </summary>
		public int ReconnectMaxDelayMs { get; set; } = 30000;

		public override string ToString()
			=> $"{this.PortName}, BaudRate:{this.BaudRate}, DataBits:{this.DataBits}, StopBits:{this.StopBits}, Parity:{this.Parity}";
	}
}
