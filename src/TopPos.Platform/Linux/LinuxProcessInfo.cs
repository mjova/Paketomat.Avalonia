using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace TopPos.Platform.Linux
{
	/// <summary>
	/// Vreme starta procesa iz <c>/proc</c>: <c>btime</c> iz <c>/proc/stat</c> (boot, Unix sekunde) + <c>starttime</c> iz
	/// <c>/proc/&lt;pid&gt;/stat</c> (otkucaji od boot-a) / <c>sysconf(_SC_CLK_TCK)</c>. Linux Agent mora da računa istom formulom
	/// (<see cref="GetProcessStartUtc"/>) da bi vreme iz <c>status.lock</c> povezao sa procesom. <c>Process.StartTime</c> za to
	/// ne valja: .NET boot vreme uzorkuje iz satova i kešira po procesu, pa dva procesa za isti PID mogu dobiti različito vreme.
	/// Ograničenje: <c>btime</c> se menja kad se sistemski sat koriguje skokom (settimeofday), pa posle toga Agent za isti proces
	/// dobija drugo vreme. Zato izjava na Linuxu nosi i stabilan identitet (<see cref="GetCurrentProcessBootIdentity"/>:
	/// <c>boot_id</c> + sirovi <c>starttime</c>), po kome Agent vezuje proces (ugovor „Transport na Linuxu“, tačka 5).
	/// </summary>
	[SupportedOSPlatform("linux")]
	internal sealed partial class LinuxProcessInfo : IProcessInfo
	{
		private const int __scClkTck = 2;  // _SC_CLK_TCK (glibc, musl)

		public DateTime GetCurrentProcessStartUtc() => GetProcessStartUtc(Environment.ProcessId);

		/// <summary>
		/// <c>boot_id</c> + sirovi <c>starttime</c>: ne menjaju se kad se sat koriguje skokom, pa Agent na Linuxu proces vezuje
		/// samo po njima i <c>ProcessId</c> (ugovor „Transport na Linuxu“, tačka 5). Agent ih čita iz istih fajlova.
		/// </summary>
		public ProcessBootIdentity? GetCurrentProcessBootIdentity()
		{
			var bootId = File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim();
			if (bootId.Length == 0) {
				throw new FormatException("/proc/sys/kernel/random/boot_id je prazan.");
			}
			return new ProcessBootIdentity(bootId, ParseStartTimeTicks(File.ReadAllText($"/proc/{Environment.ProcessId}/stat")));
		}

		/// <summary>Vreme starta procesa <paramref name="processId"/> (UTC); baca izuzetak ako podaci nisu dostupni ili ispravni.</summary>
		internal static DateTime GetProcessStartUtc(int processId)
		{
			long startTicks = ParseStartTimeTicks(File.ReadAllText($"/proc/{processId}/stat"));
			long bootSeconds = ParseBootTimeSeconds(File.ReadLines("/proc/stat"));
			long ticksPerSecond = sysconf(__scClkTck);
			if (ticksPerSecond <= 0) {
				throw new InvalidOperationException($"sysconf(_SC_CLK_TCK) je vratio {ticksPerSecond}.");
			}
			return ToUtc(bootSeconds, startTicks, ticksPerSecond);
		}

		internal static DateTime ToUtc(long bootSeconds, long startTicks, long ticksPerSecond)
			=> DateTime.UnixEpoch.AddSeconds(bootSeconds)
								 .AddTicks((startTicks / ticksPerSecond * TimeSpan.TicksPerSecond)
										   + (startTicks % ticksPerSecond * TimeSpan.TicksPerSecond / ticksPerSecond));

		/// <summary>
		/// Polje 22 (<c>starttime</c>) iz <c>/proc/&lt;pid&gt;/stat</c>. Ime procesa (polje 2, u zagradama) sme da sadrži razmake
		/// i zagrade, pa se polja broje od poslednje zatvorene zagrade; posle nje je polje 3.
		/// </summary>
		internal static long ParseStartTimeTicks(string stat)
		{
			int end = stat.LastIndexOf(')');
			if (end < 0) {
				throw new FormatException("/proc/<pid>/stat bez imena procesa u zagradama.");
			}
			var fields = stat[(end + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
			const int startTimeIndex = 22 - 3;
			if (fields.Length <= startTimeIndex) {
				throw new FormatException($"/proc/<pid>/stat ima samo {fields.Length + 2} polja.");
			}
			return long.Parse(fields[startTimeIndex], CultureInfo.InvariantCulture);
		}

		/// <summary>Red <c>btime &lt;sekunde&gt;</c> iz <c>/proc/stat</c>.</summary>
		internal static long ParseBootTimeSeconds(IEnumerable<string> procStatLines)
		{
			foreach (var line in procStatLines) {
				if (line.StartsWith("btime ", StringComparison.Ordinal)) {
					return long.Parse(line.AsSpan(6).Trim(), CultureInfo.InvariantCulture);
				}
			}
			throw new FormatException("/proc/stat nema red btime.");
		}

		[LibraryImport("libc", SetLastError = true)]
		private static partial long sysconf(int name);
	}
}