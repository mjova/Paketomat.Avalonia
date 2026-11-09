namespace TopPos.Platform.Windows
{
	/// <summary>
	/// Kao v2: sve uz program (instalacija u <c>C:\LockerStation\...</c>). Agent čita <c>status.lock</c> iz foldera programa,
	/// pa se raspored ne menja dok Windows flota ne pređe na v4.
	/// </summary>
	internal sealed class WindowsAppPaths : IAppPaths
	{
		public string ProgramDirectory => AppContext.BaseDirectory;
		public string ConfigurationDirectory => AppContext.BaseDirectory;
		public string StateDirectory => AppContext.BaseDirectory;
		public string LogDirectory => Path.Combine(AppContext.BaseDirectory, "Logs");
	}
}