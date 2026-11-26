using TopPos.Agent.Contracts;
using TopPos.Platform;

namespace TopPos.Kiosk.AgentCommunication
{
	/// <summary>
	/// Podešavanja servera kanala; u aplikaciji se prave iz <see cref="AgentCommunicationConfiguration"/>. v4: transport je
	/// named pipe (Windows) ili Unix socket (Linux), pa ima i <see cref="SocketPath"/>; dozvoljen klijent ide u transport.
	/// </summary>
	public sealed class AgentPipeServerOptions
	{
		public string PipeName { get; init; } = AgentProtocol.DefaultPipeName;

		/// <summary>v4: putanja Unix socket-a (Linux).</summary>
		public string SocketPath { get; init; } = AgentProtocol.DefaultSocketPath;

		/// <summary>
		/// Jedini dozvoljeni klijent je u aplikaciji uvek LocalSystem (Windows), odnosno root (Linux), i nije podešavanje;
		/// testovi koriste tekući nalog (v2: <c>AllowedClientSid</c>).
		/// </summary>
		public bool AllowCurrentUser { get; init; }
		/// <summary>Najviše istovremenih veza (Agent je jedan; druga je za reconnect dok se stara gasi).</summary>
		public int MaxConnections { get; init; } = 2;

		public int MaxMessageBytes { get; init; } = AgentProtocol.MaxMessageBytes;

		/// <summary>Rok za prvu poruku klijenta i za ostatak frame-a posle primljenog zaglavlja.</summary>
		public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(5);

		public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(5);

		/// <summary>Rok za potvrdu UI niti pre heartbeat-a; bez potvrde heartbeat se ne šalje.</summary>
		public TimeSpan UiAckTimeout { get; init; } = TimeSpan.FromSeconds(2);

		/// <summary>Rok za upis jednog frame-a; spor Agent posle njega gubi vezu.</summary>
		public TimeSpan WriteTimeout { get; init; } = TimeSpan.FromSeconds(5);

		/// <summary>Najviše neposlatih odgovora po vezi; statusni događaji se spajaju i ne broje se.</summary>
		public int MaxPendingResponses { get; init; } = 16;

		/// <summary>Koliko se pri gašenju čeka da poslednji događaji (ShuttingDown) odu Agentu.</summary>
		public TimeSpan ShutdownDrainTimeout { get; init; } = TimeSpan.FromMilliseconds(500);

		public string? ApplicationVersion { get; init; }

		public static AgentPipeServerOptions FromConfiguration(AgentCommunicationConfiguration configuration, string? applicationVersion) => new() {
			PipeName = configuration.PipeName,
			SocketPath = configuration.SocketPath,
			MaxMessageBytes = configuration.MaxMessageBytes,
			HandshakeTimeout = TimeSpan.FromMilliseconds(configuration.HandshakeTimeoutInMilliseconds),
			HeartbeatInterval = TimeSpan.FromMilliseconds(configuration.HeartbeatIntervalInMilliseconds),
			UiAckTimeout = TimeSpan.FromMilliseconds(configuration.UiAckTimeoutInMilliseconds),
			WriteTimeout = TimeSpan.FromMilliseconds(configuration.WriteTimeoutInMilliseconds),
			MaxPendingResponses = configuration.MaxPendingResponses,
			ApplicationVersion = applicationVersion,
		};

		/// <summary>v4: opcije transporta (bafer pipe-a za bar jednu celu poruku sa zaglavljem).</summary>
		public AgentChannelTransportOptions ToTransportOptions() => new() {
			PipeName = PipeName,
			SocketPath = SocketPath,
			MaxConnections = MaxConnections,
			MessageBufferBytes = MaxMessageBytes + AgentFrameCodec.HeaderBytes,
			AllowCurrentUser = AllowCurrentUser,
		};
	}
}
