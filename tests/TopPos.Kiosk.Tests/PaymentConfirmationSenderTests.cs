using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using TopPos.Kiosk;
using TopPos.Service;
using Xunit;

namespace TopPos.Kiosk.Tests
{
	/// <summary>
	/// <see cref="PaymentConfirmationSender.AddPaymentConfirmation"/> mora da javi da li je potvrda uplate upisana na disk:
	/// potvrda samo u memoriji ne preživljava restart, pa aktivator tada kupca upućuje na tehničku podršku.
	/// Server je namerno nedostupan (zatvoren port, kratak rok) — potvrda ostaje u listi kao kod prolazne greške.
	/// Fajl koji ne može da se pročita ne sme da se prepiše: u njemu mogu biti nepotvrđene uplate.
	/// </summary>
	public class PaymentConfirmationSenderTests : IDisposable
	{
		private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("toppos-payconf-");
		private readonly TopPosServiceClient _serviceClient = new(
			new TopPosServiceConfiguration { BaseUrl = "http://127.0.0.1:9/", PathPrefix = "api", RequestTimeout = 200, DisableHeartbeats = true },
			"TEST", logger: null, CancellationToken.None);

		private static readonly PaymentConfirmation Payment = new("123456", "000042", "SIMUL001", 250m);

		private PaymentConfirmationSender Sender(string filename) => new(
			new PaymentConfirmationSenderConfiguration { PaymentConfirmationFilename = filename, ProcessingDelayInMilliseconds = 60_000 },
			_serviceClient, NullLogger.Instance);

		public void Dispose() => _dir.Delete(recursive: true);

		[Fact]
		public async Task Saved_confirmation_returns_true_and_is_on_disk()
		{
			var file = Path.Combine(_dir.FullName, "PaymentConfirmation.json");
			using (var sender = Sender(file)) {
				Assert.True(await sender.AddPaymentConfirmation(Payment));
			}
			var json = await File.ReadAllTextAsync(file);
			Assert.Contains(Payment.TransactionNumber, json);   // preživljava restart: sledeće pokretanje ga učitava iz fajla
		}

		[Fact]
		public async Task Unwritable_file_returns_false()
		{
			// Putanja privremenog fajla je postojeći folder → upis baca izuzetak (kao pun disk ili zabrana upisa). Ranije se
			// to samo logovalo, a pozivalac je mislio da je potvrda sačuvana.
			var file = Path.Combine(_dir.FullName, "PaymentConfirmation.json");
			Directory.CreateDirectory(file + ".tmp");
			using var sender = Sender(file);
			Assert.False(await sender.AddPaymentConfirmation(Payment));
			Assert.False(File.Exists(file));
		}

		[Theory]
		[InlineData("")]                                   // prekinut upis starije verzije (WriteAllText skraćuje fajl pre upisa)
		[InlineData("[{\"UnlockCode\":\"777777\",\"Transac")]  // skraćen JSON
		[InlineData("null")]
		public async Task Unreadable_file_is_preserved_not_overwritten(string content)
		{
			// Ranije: greška čitanja se samo logovala, a prva sledeća potvrda je prepisivala fajl — nepotvrđene uplate iz
			// njega su nestajale. Sada se fajl premešta u stranu (za ručno spasavanje), a nova lista kreće od nule.
			var file = Path.Combine(_dir.FullName, "PaymentConfirmation.json");
			await File.WriteAllTextAsync(file, content);
			using (var sender = Sender(file)) {
				Assert.True(await sender.AddPaymentConfirmation(Payment));
			}
			var preserved = Assert.Single(_dir.GetFiles("PaymentConfirmation.json.corrupt-*"));
			Assert.Equal(content, await File.ReadAllTextAsync(preserved.FullName));
			var json = await File.ReadAllTextAsync(file);
			Assert.Contains(Payment.TransactionNumber, json);
		}

		[Fact]
		public async Task Locked_valid_file_is_neither_moved_nor_overwritten()
		{
			// Codex (ab4ef2b): fajl zaključan pri startu (npr. antivirus) nije oštećen — ne sme u .corrupt, a posle otključavanja
			// ga ne sme prepisati lista bez njegovih potvrda. Upis je do restarta zabranjen, pa kupac dobija poruku za podršku.
			var file = Path.Combine(_dir.FullName, "PaymentConfirmation.json");
			var original = await WriteEarlierConfirmationAsync(file);
			using (var sender = await WithLockedFileAsync(file, FileShare.None, releaseAfter: null, () => Sender(file), async s => {
				Assert.False(await s.AddPaymentConfirmation(Payment));
			})) {
				Assert.False(await sender.AddPaymentConfirmation(Payment));   // i posle otključavanja
			}
			Assert.Equal(original, await File.ReadAllTextAsync(file));
			Assert.Empty(_dir.GetFiles("*.corrupt-*"));
		}

		[Fact]
		public async Task File_unlocked_during_retries_is_loaded()
		{
			var file = Path.Combine(_dir.FullName, "PaymentConfirmation.json");
			await WriteEarlierConfirmationAsync(file);
			using (var sender = await WithLockedFileAsync(file, FileShare.None, releaseAfter: TimeSpan.FromMilliseconds(200), () => Sender(file), _ => Task.CompletedTask)) {
				Assert.True(await sender.AddPaymentConfirmation(Payment));
			}
			var json = await File.ReadAllTextAsync(file);
			Assert.Contains("000041", json);
			Assert.Contains(Payment.TransactionNumber, json);
		}

		[WindowsFact("fajl otvoren bez FileShare.Delete ne može da se premesti (na Linuxu rename uspeva)")]
		public async Task Corrupt_file_that_cannot_be_moved_is_not_overwritten()
		{
			// Oštećen fajl otvoren bez dozvole brisanja: čita se, ali ne može da se premesti → ostaje netaknut, upis zabranjen.
			var file = Path.Combine(_dir.FullName, "PaymentConfirmation.json");
			const string content = "[{\"UnlockCode\":\"777777\",\"Transac";
			await File.WriteAllTextAsync(file, content);
			using (var sender = await WithLockedFileAsync(file, FileShare.Read, releaseAfter: null, () => Sender(file), async s => {
				Assert.False(await s.AddPaymentConfirmation(Payment));
			})) {
				Assert.False(await sender.AddPaymentConfirmation(Payment));
			}
			Assert.Equal(content, await File.ReadAllTextAsync(file));
			Assert.Empty(_dir.GetFiles("*.corrupt-*"));
		}

		private static async Task<string> WriteEarlierConfirmationAsync(string file)
		{
			var earlier = new PaymentConfirmation("777777", "000041", "SIMUL001", 100m) { CreatedUtc = DateTime.UtcNow };
			var json = System.Text.Json.JsonSerializer.Serialize(new[] { earlier });
			await File.WriteAllTextAsync(file, json);
			return json;
		}

		/// <summary>
		/// Drži <paramref name="file"/> otvoren (deljenje <paramref name="share"/>) dok se pravi sender i izvršava
		/// <paramref name="whileLocked"/>; sa <paramref name="releaseAfter"/> se otključava posle tog vremena, nezavisno od toka.
		/// </summary>
		private static async Task<PaymentConfirmationSender> WithLockedFileAsync(string file, FileShare share, TimeSpan? releaseAfter,
			Func<PaymentConfirmationSender> create, Func<PaymentConfirmationSender, Task> whileLocked)
		{
			var handle = new FileStream(file, FileMode.Open, FileAccess.Read, share);
			try {
				var sender = create();
				if (releaseAfter.HasValue) {
					await Task.Delay(releaseAfter.Value);
					handle.Dispose();
				}
				await whileLocked(sender);
				return sender;
			}
			finally {
				handle.Dispose();
			}
		}

		[Fact]
		public async Task Readable_file_is_loaded_and_rewritten_without_temp_file()
		{
			var file = Path.Combine(_dir.FullName, "PaymentConfirmation.json");
			var earlier = new PaymentConfirmation("777777", "000041", "SIMUL001", 100m) { CreatedUtc = DateTime.UtcNow };
			await File.WriteAllTextAsync(file, System.Text.Json.JsonSerializer.Serialize(new[] { earlier }));
			using (var sender = Sender(file)) {
				Assert.True(await sender.AddPaymentConfirmation(Payment));
			}
			var json = await File.ReadAllTextAsync(file);
			Assert.Contains(earlier.TransactionNumber, json);
			Assert.Contains(Payment.TransactionNumber, json);
			Assert.Empty(_dir.GetFiles("*.tmp"));
			Assert.Empty(_dir.GetFiles("*.corrupt-*"));
		}
	}
}
