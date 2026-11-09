using System.Runtime.Versioning;
using TopPos.Platform;
using TopPos.Platform.Linux;
using TopPos.Platform.Windows;
using Xunit;

namespace TopPos.Kiosk.Tests
{
	/// <summary>v4: platformski sloj (putanje, jedna instanca, zvuk).</summary>
	public class PlatformTests : IDisposable
	{
		private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("toppos-platform-");

		public void Dispose() => _dir.Delete(recursive: true);

		[Fact]
		public void Linux_paths_without_systemd_are_next_to_the_program()
		{
			var paths = new LinuxAppPaths(_ => null);

			Assert.Equal(AppContext.BaseDirectory, paths.ProgramDirectory);
			Assert.Equal(AppContext.BaseDirectory, paths.ConfigurationDirectory);
			Assert.Equal(AppContext.BaseDirectory, paths.StateDirectory);
			Assert.Equal(Path.Combine(AppContext.BaseDirectory, "Logs"), paths.LogDirectory);
		}

		[Fact]
		public void Linux_paths_under_systemd_use_its_directories_first_one_wins()
		{
			var environment = new Dictionary<string, string> {
				["CONFIGURATION_DIRECTORY"] = "/etc/paketomat",
				["STATE_DIRECTORY"] = "/var/lib/paketomat:/var/lib/other",
				["LOGS_DIRECTORY"] = "/var/log/paketomat",
			};
			var paths = new LinuxAppPaths(name => environment.GetValueOrDefault(name));

			Assert.Equal(AppContext.BaseDirectory, paths.ProgramDirectory);
			Assert.Equal("/etc/paketomat", paths.ConfigurationDirectory);
			Assert.Equal("/var/lib/paketomat", paths.StateDirectory);
			Assert.Equal("/var/log/paketomat", paths.LogDirectory);
		}

		[Fact]
		public void Windows_paths_keep_v2_layout()
		{
			var paths = new WindowsAppPaths();

			Assert.Equal(AppContext.BaseDirectory, paths.ConfigurationDirectory);
			Assert.Equal(AppContext.BaseDirectory, paths.StateDirectory);     // Agent čita status.lock uz program
			Assert.Equal(Path.Combine(AppContext.BaseDirectory, "Logs"), paths.LogDirectory);
		}

		/// <summary>Zaključan fajl: druga instanca ne dobija mesto dok ga prva drži (radi i na Windows-u, FileShare.None).</summary>
		[Fact]
		public void Lock_file_guard_allows_one_instance_at_a_time()
		{
			var guard = new LinuxSingleInstanceGuard(Path.Combine(_dir.FullName, LinuxSingleInstanceGuard.LockFileName));

			using (var first = guard.TryAcquire()) {
				Assert.NotNull(first);
				Assert.Null(guard.TryAcquire());
			}
			using var again = guard.TryAcquire();
			Assert.NotNull(again);
		}

		/// <summary>
		/// Fajl koji je ostavio drugi korisnik nema pravo upisa za ovog: otvara se samo za čitanje i zaključava. Nov fajl dobija
		/// prava za sve (umask ih skida).
		/// </summary>
		[LinuxFact]
		[SupportedOSPlatform("linux")]
		public void Lock_file_without_write_permission_is_still_locked_and_new_file_is_shared()
		{
			var path = Path.Combine(_dir.FullName, LinuxSingleInstanceGuard.LockFileName);
			var guard = new LinuxSingleInstanceGuard(path);

			using (var created = guard.TryAcquire()) {
				Assert.NotNull(created);
			}
			Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite
						 | UnixFileMode.OtherRead | UnixFileMode.OtherWrite, File.GetUnixFileMode(path));

			File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
			using var first = guard.TryAcquire();
			Assert.NotNull(first);
			Assert.Null(guard.TryAcquire());
		}

		/// <summary>Bez prava na folder lock fajla: greška sa putanjom, ne „druga instanca radi“.</summary>
		[LinuxFact]
		[SupportedOSPlatform("linux")]
		public void Lock_file_without_access_is_an_error_not_a_running_instance()
		{
			var locked = Directory.CreateDirectory(Path.Combine(_dir.FullName, "locked"));
			File.SetUnixFileMode(locked.FullName, UnixFileMode.UserRead | UnixFileMode.UserExecute);
			try {
				if (Environment.UserName == "root") {
					return;     // root ima pristup svemu
				}
				var guard = new LinuxSingleInstanceGuard(Path.Combine(locked.FullName, LinuxSingleInstanceGuard.LockFileName));
				var ex = Assert.Throws<InvalidOperationException>(guard.TryAcquire);
				Assert.Contains(locked.FullName, ex.Message);
			}
			finally {
				File.SetUnixFileMode(locked.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
			}
		}

		[WindowsFact("imenovani mutex")]
		[SupportedOSPlatform("windows")]
		public void Mutex_guard_allows_one_instance_at_a_time()
		{
			var guard = new WindowsSingleInstanceGuard($"TopPos.Kiosk.Tests.{Guid.NewGuid():N}");

			var first = guard.TryAcquire();
			Assert.NotNull(first);
			var second = Task.Run(guard.TryAcquire).Result;     // druga nit, kao drugi proces u istoj sesiji
			Assert.Null(second);
			first.Dispose();
		}

		/// <summary>SIGTERM (systemd stop) stiže kao zahtev za gašenje, a proces ne prekida runtime — uredan izlazak radi kiosk.</summary>
		[LinuxFact]
		public void Sigterm_is_reported_and_does_not_end_the_process()
		{
			using var received = new ManualResetEventSlim();
			System.Runtime.InteropServices.PosixSignal? signal = null;
			using (new ShutdownSignal(s => { signal = s; received.Set(); })) {
				using var kill = System.Diagnostics.Process.Start("kill", $"-TERM {Environment.ProcessId}");
				kill.WaitForExit();
				Assert.True(received.Wait(TimeSpan.FromSeconds(10)), "SIGTERM nije stigao");
			}
			Assert.Equal(System.Runtime.InteropServices.PosixSignal.SIGTERM, signal);
		}

		[Fact]
		public void Single_instance_mutex_name_is_the_one_v2_uses()
			=> Assert.Equal("TopPos.Client.WPF.SingleInstance", WindowsSingleInstanceGuard.V2MutexName);

		/// <summary>
		/// Pušta stvaran zvuk, pa radi samo uz <c>TOPPOS_AUDIO_TEST_FILE</c> (putanja do mp3): na Windows-u LibVLC dolazi iz paketa,
		/// na Linuxu mora biti instaliran (<c>libvlc5</c>, <c>vlc-plugin-base</c>).
		/// </summary>
		[Fact]
		public void Audio_player_plays_queued_files_in_order_and_becomes_idle()
		{
			var file = Environment.GetEnvironmentVariable("TOPPOS_AUDIO_TEST_FILE");
			if (string.IsNullOrEmpty(file)) {
				return;
			}
			var log = new KioskOperationCoordinatorTests.ListLogger();
			using var player = new VlcAudioPlayer(Path.GetDirectoryName(file)!, log);

			player.Play(Path.GetFileName(file));
			player.Play(Path.GetFileName(file));
			Assert.False(player.IsIdle);

			Assert.True(SpinWait.SpinUntil(() => player.IsIdle, TimeSpan.FromSeconds(30)), "zvuk se nije završio");
			Assert.DoesNotContain(log.Entries, e => e.Level >= Microsoft.Extensions.Logging.LogLevel.Error);
		}

		[Fact]
		public void Audio_player_reset_empties_the_queue()
		{
			var file = Environment.GetEnvironmentVariable("TOPPOS_AUDIO_TEST_FILE");
			if (string.IsNullOrEmpty(file)) {
				return;
			}
			using var player = new VlcAudioPlayer(Path.GetDirectoryName(file)!, logger: null);

			player.Play(Path.GetFileName(file));
			player.Play(Path.GetFileName(file));
			player.Reset();

			Assert.True(player.IsIdle);
		}
	}
}