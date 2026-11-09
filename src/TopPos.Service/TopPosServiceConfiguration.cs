using System.Text.Json.Serialization;

namespace TopPos.Service
{
	public class TopPosServiceConfiguration
	{
		public string BaseUrl { get; set; } = default!;

		public string PathPrefix { get; set; } = default!;

		public string ServiceUsername { get; set; } = default!;

		public string ServicePassword { get; set; } = default!;

		public string POSUsername { get; set; } = default!;

		public string POSPassword { get; set; } = default!;

		public string CourierServiceUsername { get; set; } = default!;

		public string CourierServicePassword { get; set; } = default!;

		public int RequestTimeout { get; set; }

		public int HeartbeatFrequency { get; set; } // When the frequency can't be obtained from the service

		[JsonIgnore]
		public bool DisableHeartbeats { get; set; }		// Set to true during development, and run TopPos.Debug.Heartbeat console application
	}
}
