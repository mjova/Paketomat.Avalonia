namespace TopPos.Platform.Linux
{
	/// <summary>
	/// Folderi koje daje systemd servis (<c>ConfigurationDirectory=</c>, <c>StateDirectory=</c>, <c>LogsDirectory=</c> →
	/// <c>/etc/...</c>, <c>/var/lib/...</c>, <c>/var/log/...</c>); bez systemd-a (razvoj, ručno pokretanje) sve je uz program,
	/// kao na Windows-u.
	/// </summary>
	internal sealed class LinuxAppPaths(Func<string, string?> environment) : IAppPaths
	{
		public LinuxAppPaths() : this(Environment.GetEnvironmentVariable) { }

		public string ProgramDirectory => AppContext.BaseDirectory;
		public string ConfigurationDirectory => FromSystemd("CONFIGURATION_DIRECTORY") ?? AppContext.BaseDirectory;
		public string StateDirectory => FromSystemd("STATE_DIRECTORY") ?? AppContext.BaseDirectory;
		public string LogDirectory => FromSystemd("LOGS_DIRECTORY") ?? Path.Combine(AppContext.BaseDirectory, "Logs");

		// systemd navodi više foldera razdvojenih sa ':' kad ih je u servisu više; kiosk koristi prvi.
		private string? FromSystemd(string name)
		{
			var value = environment(name);
			if (string.IsNullOrWhiteSpace(value)) {
				return null;
			}
			var first = value.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
			return string.IsNullOrEmpty(first) ? null : first;
		}
	}
}