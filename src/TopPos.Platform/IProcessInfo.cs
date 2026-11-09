namespace TopPos.Platform
{
	/// <summary>Podaci o tekućem procesu za izjavu u <c>status.lock</c>.</summary>
	public interface IProcessInfo
	{
		/// <summary>
		/// Vreme kreiranja tekućeg procesa (UTC), iz istog izvora iz kog ga čita Agent (Windows: <c>GetProcessTimes</c>,
		/// Linux: <c>/proc/self/stat</c>). Baca izuzetak ako ga ne pročita.
		/// </summary>
		DateTime GetCurrentProcessStartUtc();
	}
}
