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
	}
}