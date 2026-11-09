using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace TopPos.Client.WPF.Workflow
{
	/// <summary>
	/// Izjava procesa u <c>status.lock</c> (doc/StatusSemaphore.md, „Izjava procesa i kanala“): ko piše fajl
	/// (<see cref="ProcessId"/>, <see cref="ProcessStartUtc"/>) i da li Agentu nudi kanal (<see cref="AgentPipeName"/>).
	/// Agent za proces sa izjavom kanala status čita samo preko pipe-a, nikad <c>Busy</c> iz fajla.
	/// </summary>
	/// <param name="ProcessStartUtc">
	/// Vreme kreiranja procesa (UTC); <c>null</c> = nije pročitano. Agent tada ne može da poveže pipe sa procesom i vidi
	/// nepoznato stanje, a kiosk radi dalje.
	/// </param>
	/// <param name="AgentPipeName">
	/// Ime pipe-a po nameri (<c>AgentCommunication.Enabled</c>), tačno kako je u konfiguraciji — i kad konfiguracija nije
	/// ispravna ili server ne proradi; <c>null</c> = kanal isključen.
	/// </param>
	public sealed record StatusDeclaration(int ProcessId, DateTime? ProcessStartUtc, string? AgentPipeName)
	{
		/// <summary>
		/// Tekući proces. Vreme starta je creation time iz <c>GetProcessTimes</c>, isti izvor koji čita Agent; neuspeh ne
		/// prekida start kioska (<see cref="ResolveProcessStartUtc"/>).
		/// </summary>
		public static StatusDeclaration ForCurrentProcess(string? agentPipeName, ILogger? logger)
			=> new(Environment.ProcessId, ResolveProcessStartUtc(GetCurrentProcessStartUtc, GetCurrentProcessStartUtcFromProcess, logger), agentPipeName);

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
				logger?.LogWarning(ex, "StatusSemaphore: vreme starta procesa nije pročitano iz GetProcessTimes; koristi se Process.StartTime.");
			}
			try {
				return fallback();
			}
			catch (Exception ex) {
				logger?.LogError(ex, "StatusSemaphore: vreme starta procesa nije poznato; ProcessStartUtc se upisuje kao null, pa Agent vidi nepoznato stanje kioska.");
				return null;
			}
		}

		private static DateTime GetCurrentProcessStartUtc()
		{
			if (!GetProcessTimes(GetCurrentProcess(), out long creationTime, out _, out _, out _)) {
				throw new Win32Exception(Marshal.GetLastWin32Error(), "GetProcessTimes za tekući proces nije uspeo.");
			}
			return DateTime.FromFileTimeUtc(creationTime);
		}

		// Process.StartTime je lokalno vreme iz istog FILETIME-a; povratak u UTC je tačan i u satu promene letnjeg vremena.
		private static DateTime GetCurrentProcessStartUtcFromProcess()
		{
			using var process = Process.GetCurrentProcess();
			return process.StartTime.ToUniversalTime();
		}

		// Pseudo-ručka tekućeg procesa; ne zatvara se.
		[DllImport("kernel32.dll")]
		private static extern IntPtr GetCurrentProcess();

		[DllImport("kernel32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool GetProcessTimes(IntPtr process, out long creationTime, out long exitTime, out long kernelTime, out long userTime);
	}
}
