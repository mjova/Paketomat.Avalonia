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

		/// <summary>
		/// Stabilan identitet tekućeg procesa za Agenta na Linuxu (ugovor „Transport na Linuxu“, tačka 5): <c>boot_id</c> i sirovi
		/// <c>starttime</c> (polje 22 iz <c>/proc/&lt;pid&gt;/stat</c>). <c>null</c> = platforma ga nema (Windows, gde Agent proces
		/// vezuje po vremenu kreiranja). Na Linuxu baca izuzetak ako ga ne pročita.
		/// </summary>
		ProcessBootIdentity? GetCurrentProcessBootIdentity();
	}

	/// <param name="BootId">Sadržaj <c>/proc/sys/kernel/random/boot_id</c> (bez novog reda).</param>
	/// <param name="StartTicks">Polje 22 (<c>starttime</c>) iz <c>/proc/&lt;pid&gt;/stat</c>, otkucaji od boot-a.</param>
	public sealed record ProcessBootIdentity(string BootId, long StartTicks);

	/// <summary>Adresa kanala ka Agentu u izjavi <c>status.lock</c>: ime pipe-a (Windows) ili putanja socket-a (Linux).</summary>
	public sealed record AgentChannelAddress(string? PipeName, string? SocketPath);
}
