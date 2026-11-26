using System.Diagnostics;
using Microsoft.Extensions.Logging;
using TopPos.Platform;

namespace TopPos.Kiosk.Workflow
{
	/// <summary>
	/// Izjava procesa u <c>status.lock</c> (doc/StatusSemaphore.md, „Izjava procesa i kanala“): ko piše fajl
	/// (<see cref="ProcessId"/>, <see cref="ProcessStartUtc"/>) i da li Agentu nudi kanal (<see cref="AgentPipeName"/>).
	/// Agent za proces sa izjavom kanala status čita samo preko pipe-a, nikad <c>Busy</c> iz fajla.
	/// v4 (ugovor „Transport na Linuxu“, tačka 5): na Linuxu kanal je <see cref="AgentSocketPath"/>, a proces se vezuje po
	/// <see cref="BootIdentity"/> (<c>ProcessBootId</c>, <c>ProcessStartTicks</c>); na Windows-u ta polja izostaju.
	/// </summary>
	/// <param name="ProcessStartUtc">
	/// Vreme kreiranja procesa (UTC); <c>null</c> = nije pročitano. Agent tada ne može da poveže pipe sa procesom i vidi
	/// nepoznato stanje, a kiosk radi dalje.
	/// </param>
	/// <param name="AgentPipeName">
	/// Ime pipe-a po nameri (<c>AgentCommunication.Enabled</c>), tačno kako je u konfiguraciji — i kad konfiguracija nije
	/// ispravna ili server ne proradi; <c>null</c> = kanal isključen.
	/// </param>
	/// <param name="AgentSocketPath">v4: putanja Unix socket-a umesto imena pipe-a (Linux), isto po nameri.</param>
	/// <param name="BootIdentity">
	/// v4: <c>boot_id</c> i sirovi <c>starttime</c> (Linux); <c>null</c> na Windows-u ili kad nije pročitan — Agent tada na
	/// Linuxu ne može da poveže kanal sa procesom i vidi nepoznato stanje.
	/// </param>
	public sealed record StatusDeclaration(int ProcessId, DateTime? ProcessStartUtc, string? AgentPipeName, string? AgentSocketPath = null,
											ProcessBootIdentity? BootIdentity = null)
	{
		/// <summary>
		/// Tekući proces. Vreme starta je iz platformskog sloja, isti izvor koji čita Agent (Windows: creation time iz
		/// <c>GetProcessTimes</c>, Linux: <c>/proc/self/stat</c>); neuspeh ne prekida start kioska (<see cref="ResolveProcessStartUtc"/>).
		/// </summary>
		/// <param name="agentChannel">Kanal po nameri (<c>AgentCommunication.Enabled</c>); <c>null</c> = kanal isključen.</param>
		public static StatusDeclaration ForCurrentProcess(AgentChannelAddress? agentChannel, ILogger? logger)
			=> new(Environment.ProcessId,
					ResolveProcessStartUtc(PlatformServices.ProcessInfo.GetCurrentProcessStartUtc, GetCurrentProcessStartUtcFromProcess, logger),
					agentChannel?.PipeName,
					agentChannel?.SocketPath,
					ResolveBootIdentity(PlatformServices.ProcessInfo.GetCurrentProcessBootIdentity, logger));

		/// <summary>v4: <c>boot_id</c> i <c>starttime</c>; neuspeh ide u log (Error) i ne prekida start, kao vreme starta.</summary>
		public static ProcessBootIdentity? ResolveBootIdentity(Func<ProcessBootIdentity?> read, ILogger? logger)
		{
			try {
				return read();
			}
			catch (Exception ex) {
				logger?.LogError(ex, "StatusSemaphore: boot_id i starttime procesa nisu pročitani; ProcessBootId i ProcessStartTicks se ne upisuju, pa Agent vidi nepoznato stanje kioska.");
				return null;
			}
		}

		/// <summary>
		/// Vreme starta iz prvog izvora, pa iz rezervnog (Warning u log); ako ne uspe ni jedan, <c>null</c> (Error u log).
		/// Nikad ne baca — vreme starta je podatak za Agenta, ne uslov rada kioska.
		/// </summary>
		public static DateTime? ResolveProcessStartUtc(Func<DateTime> primary, Func<DateTime> fallback, ILogger? logger)
		{
			try {
				return primary();
			}
			catch (Exception ex) {
				logger?.LogWarning(ex, "StatusSemaphore: vreme starta procesa nije pročitano iz izvora platforme (GetProcessTimes, /proc/self/stat); koristi se Process.StartTime.");
			}
			try {
				return fallback();
			}
			catch (Exception ex) {
				logger?.LogError(ex, "StatusSemaphore: vreme starta procesa nije poznato; ProcessStartUtc se upisuje kao null, pa Agent vidi nepoznato stanje kioska.");
				return null;
			}
		}

		// v4: GetProcessTimes (kernel32) je premešten u TopPos.Platform (WindowsProcessInfo).

		// Process.StartTime je lokalno vreme iz istog FILETIME-a; povratak u UTC je tačan i u satu promene letnjeg vremena.
		private static DateTime GetCurrentProcessStartUtcFromProcess()
		{
			using var process = Process.GetCurrentProcess();
			return process.StartTime.ToUniversalTime();
		}
	}
}
