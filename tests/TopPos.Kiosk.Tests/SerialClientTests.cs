using System.Runtime.InteropServices;
using Flatsoft.Net.Serial;
using Microsoft.Extensions.Logging;
using Xunit;

namespace TopPos.Kiosk.Tests
{
	/// <summary>
	/// v4: petlje SerialClient-a kad port ne može da se otvori (Debian 9.11.2026: nepostojeći /dev port je vrteo petlju bez
	/// pauze — 94 MB loga za 4 s, kiosk zapeo na proveri skenera).
	/// </summary>
	public class SerialClientTests : IDisposable
	{
		private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("toppos-serial-");

		public void Dispose() => _dir.Delete(recursive: true);

		/// <summary>
		/// OpenAsync na drugoj niti, sa rokom: bez ispravke petlja prijema se vrti sinhrono (svaki await je već završen), pa se
		/// OpenAsync nikad ne vraća — test tada pada umesto da zaustavi ceo test run.
		/// </summary>
		private static async Task OpenWithin5s(SerialClient client)
		{
			var open = Task.Run(async () => await client.OpenAsync(CancellationToken.None));
			Assert.Same(open, await Task.WhenAny(open, Task.Delay(TimeSpan.FromSeconds(5))));
			await open;
		}

		/// <summary>Uređaj koji ne postoji se ponaša kao nepostojeći COM port na Windows-u: greška otvaranja, petlje se završavaju.</summary>
		[LinuxFact]
		public async Task Missing_device_path_ends_loops()
		{
			var log = new KioskOperationCoordinatorTests.ListLogger();
			await using var client = new SerialClient("/dev/ttyTOPPOS-NONE", log);

			await Assert.ThrowsAsync<FileNotFoundException>(() => OpenWithin5s(client));
			await Task.Delay(1500);

			Assert.Contains(log.Entries, e => e.Message.Contains("ReceiveLoop: port opening failed"));
			Assert.True(log.Entries.Count(e => e.Level >= LogLevel.Error) < 10, $"grešaka u logu: {log.Entries.Count(e => e.Level >= LogLevel.Error)}");
		}

		/// <summary>
		/// Port koji postoji, a ne otvara se (ovde običan fajl: Linux ga odbija kao serijski port, Windows odbija ime koje nije COM)
		/// se ponovo pokušava uz pauzu, bez vrtenja petlje.
		/// </summary>
		[Fact]
		public async Task Repeated_open_failure_is_retried_with_pause()
		{
			var notAPort = Path.Combine(_dir.FullName, "not-a-port");
			await File.WriteAllTextAsync(notAPort, "");
			var log = new KioskOperationCoordinatorTests.ListLogger();
			await using var client = new SerialClient(notAPort, log);

			await Assert.ThrowsAnyAsync<Exception>(() => OpenWithin5s(client));
			await Task.Delay(2500);

			int failures = log.Entries.Count(e => e.Message.Contains("receive operation failed"));
			Assert.InRange(failures, 1, 5);     // bez pauze: hiljade za 2.5 s
		}

		/// <summary>
		/// Upis van Windows-a stiže na port tačno jednom (v2 <c>SerialClientAsyncFixes.WriteAsync</c> je na toj grani zvao samog
		/// sebe — beskonačna rekurzija, pa dupli upis). Port je pseudo-terminal: <see cref="SerialClient"/> otvara njegov
		/// <c>/dev/pts/N</c>, a test čita sa druge strane.
		/// </summary>
		[LinuxFact]
		public async Task Write_reaches_the_port_exactly_once()
		{
			using var pty = PseudoTerminal.Open();
			var log = new KioskOperationCoordinatorTests.ListLogger();
			await using var client = new SerialClient(pty.PortName, log);
			await OpenWithin5s(client);

			byte[] data = [0x02, 0x10, 0x20, 0x30, 0x03];
			await client.Write(data, CancellationToken.None);

			Assert.Equal(data, pty.ReadUntilQuiet(data.Length + 1, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(500)));
			Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Error);
		}

		/// <summary>Linux pseudo-terminal (libc <c>posix_openpt</c>): strana uređaja je „serijski port“, test čita glavnu stranu.</summary>
		private sealed class PseudoTerminal : IDisposable
		{
			private const int __oRdwr = 2, __oNoctty = 0x100, __pollIn = 1;
			private readonly int _master;

			private PseudoTerminal(int master, string portName)
			{
				_master = master;
				PortName = portName;
			}

			public string PortName { get; }

			public static PseudoTerminal Open()
			{
				int master = posix_openpt(__oRdwr | __oNoctty);
				Assert.True(master >= 0, "posix_openpt");
				Assert.Equal(0, grantpt(master));
				Assert.Equal(0, unlockpt(master));
				return new PseudoTerminal(master, Marshal.PtrToStringAnsi(ptsname(master))!);
			}

			/// <summary>
			/// Čita dok prvi bajt ne stigne (najviše <paramref name="first"/>), pa dok <paramref name="quiet"/> nema novih. Staje
			/// i kad stigne <paramref name="maxBytes"/> bajtova (višestruki upis) ili istekne ukupan rok od 10 s (upis koji se
			/// stalno ponavlja), da test padne umesto da visi.
			/// </summary>
			public byte[] ReadUntilQuiet(int maxBytes, TimeSpan first, TimeSpan quiet)
			{
				var received = new List<byte>();
				var buffer = new byte[256];
				var fds = new PollFd[] { new() { Fd = _master, Events = __pollIn } };
				var total = System.Diagnostics.Stopwatch.StartNew();
				var limit = TimeSpan.FromSeconds(10);
				var wait = first;
				while (received.Count < maxBytes && total.Elapsed < limit) {
					int timeout = (int)Math.Max(0, Math.Min(wait.TotalMilliseconds, (limit - total.Elapsed).TotalMilliseconds));
					if (poll(fds, 1, timeout) <= 0) {
						break;
					}
					int count = (int)read(_master, buffer, buffer.Length);
					if (count <= 0) {
						break;
					}
					received.AddRange(buffer.AsSpan(0, count));
					wait = quiet;
				}
				return [.. received];
			}

			public void Dispose() => close(_master);

			[StructLayout(LayoutKind.Sequential)]
			private struct PollFd
			{
				public int Fd;
				public short Events;
				public short REvents;
			}

			[DllImport("libc", SetLastError = true)] private static extern int posix_openpt(int flags);
			[DllImport("libc", SetLastError = true)] private static extern int grantpt(int fd);
			[DllImport("libc", SetLastError = true)] private static extern int unlockpt(int fd);
			[DllImport("libc", SetLastError = true)] private static extern IntPtr ptsname(int fd);
			[DllImport("libc", SetLastError = true)] private static extern int poll([In, Out] PollFd[] fds, nuint count, int timeout);
			[DllImport("libc", SetLastError = true)] private static extern nint read(int fd, byte[] buffer, nint count);
			[DllImport("libc", SetLastError = true)] private static extern int close(int fd);
		}
	}
}