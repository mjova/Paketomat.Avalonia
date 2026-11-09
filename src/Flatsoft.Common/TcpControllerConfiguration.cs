namespace Flatsoft.Common
{
	public class TcpControllerConfiguration
	{
		public string IpAddress { get; set; } = default!;

		public int Port { get; set; } = 4001;

		public override string ToString()
			=> $"{this.IpAddress}:{this.Port}";
	}
}
