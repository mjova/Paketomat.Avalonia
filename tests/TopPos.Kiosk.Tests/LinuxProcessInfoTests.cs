using System.Diagnostics;
using System.Runtime.Versioning;
using TopPos.Platform.Linux;
using Xunit;

namespace TopPos.Kiosk.Tests
{
	/// <summary>v4: vreme starta procesa na Linuxu iz /proc (izjava u status.lock; Agent ga računa istom formulom).</summary>
	public class LinuxProcessInfoTests
	{
		[Theory]
		[InlineData("1234 (kiosk) S 1 1234 1234 0 -1 4194560 100 0 0 0 5 3 0 0 20 0 9 0 98765 1000 200", 98765)]
		[InlineData("1234 (a) b (c d)) S 1 1234 1234 0 -1 4194560 100 0 0 0 5 3 0 0 20 0 9 0 42 1000 200", 42)]
		public void Start_time_is_field_22_counted_after_process_name(string stat, long expected)
			=> Assert.Equal(expected, LinuxProcessInfo.ParseStartTimeTicks(stat));

		[Fact]
		public void Boot_time_comes_from_btime_line()
			=> Assert.Equal(1762675200, LinuxProcessInfo.ParseBootTimeSeconds(["cpu  1 2 3", "intr 5", "btime 1762675200", "processes 9"]));

		[Fact]
		public void Missing_btime_is_an_error()
			=> Assert.Throws<FormatException>(() => LinuxProcessInfo.ParseBootTimeSeconds(["cpu  1 2 3"]));

		[Fact]
		public void Ticks_are_converted_without_rounding_error()
			=> Assert.Equal(new DateTime(2025, 11, 9, 8, 0, 12, 340, DateTimeKind.Utc),
							LinuxProcessInfo.ToUtc(new DateTimeOffset(2025, 11, 9, 8, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(), 1234, 100));

		/// <summary>Isti PID daje isto vreme pri svakom čitanju (drugi proces, Agent, računa isto) i blizu je Process.StartTime.</summary>
		[LinuxFact]
		[SupportedOSPlatform("linux")]
		public void Current_process_start_is_stable_and_close_to_Process_StartTime()
		{
			var first = LinuxProcessInfo.GetProcessStartUtc(Environment.ProcessId);
			Thread.Sleep(20);
			var second = new LinuxProcessInfo().GetCurrentProcessStartUtc();

			Assert.Equal(first, second);
			Assert.Equal(DateTimeKind.Utc, first.Kind);
			using var process = Process.GetCurrentProcess();
			Assert.True((first - process.StartTime.ToUniversalTime()).Duration() < TimeSpan.FromSeconds(2));
		}
	}
}