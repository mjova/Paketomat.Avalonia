namespace TopPos.Agent.Contracts
{
	/// <summary>Parametri komande <see cref="AgentCommands.ShutdownApplication"/> (<see cref="AgentRequest.Payload"/>).</summary>
	public class ShutdownApplicationPayload
	{
		/// <summary>Zašto Agent gasi program (<see cref="ShutdownPurposes"/>); kiosk ga upisuje u log.</summary>
		public string Purpose { get; set; } = "";
	}

	/// <summary>Vrednosti <see cref="ShutdownApplicationPayload.Purpose"/>.</summary>
	public static class ShutdownPurposes
	{
		public const string Restart = "Restart";
		public const string Stop = "Stop";
		public const string Install = "Install";
		public const string Update = "Update";

		public static readonly IReadOnlyList<string> All = [Restart, Stop, Install, Update];
	}
}
