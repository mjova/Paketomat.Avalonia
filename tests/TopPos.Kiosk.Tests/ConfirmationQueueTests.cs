using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TopPos.Client.WPF;
using Xunit;

namespace TopPos.Workflow.Tests
{
	/// <summary>
	/// <see cref="ConfirmationQueue{T}"/>: ishod slanja određuje da li zapis ostaje u redu. Slanje je zamenjeno skriptom
	/// ishoda; fajl reda je jedini dokaz šta je ostalo (posle restarta se šalje samo ono što je u njemu).
	/// Učitavanje i upis fajla pokrivaju <see cref="PaymentConfirmationSenderTests"/> (isti red ispod omotača).
	/// </summary>
	public class ConfirmationQueueTests : IDisposable
	{
		private sealed record TestConfirmation(string Id) : QueuedConfirmation;

		private static readonly TimeSpan __timeout = TimeSpan.FromSeconds(5);

		private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("toppos-queue-");
		private readonly List<string> _sent = [];
		private readonly Lock _sentLock = new();

		private string QueueFile => Path.Combine(_dir.FullName, "Queue.json");

		public void Dispose() => _dir.Delete(recursive: true);

		private ConfirmationQueue<TestConfirmation> Queue(Func<TestConfirmation, ConfirmationSendResult> outcome) => new(
			"Test", "test potvrda", QueueFile, TimeSpan.FromMilliseconds(20),
			(c, _) => {
				lock (_sentLock) {
					_sent.Add(c.Id);
				}
				return Task.FromResult(outcome(c));
			},
			c => $"[{c.Id}]", NullLogger.Instance);

		[Theory]
		[InlineData(ConfirmationSendResult.Delivered)]
		[InlineData(ConfirmationSendResult.AlreadyDelivered)]
		[InlineData(ConfirmationSendResult.Rejected)]
		public async Task Final_outcome_removes_confirmation_from_file(ConfirmationSendResult outcome)
		{
			using var queue = Queue(_ => outcome);
			Assert.True(await queue.Add(new TestConfirmation("A")));
			await WaitForFileAsync(ids => ids.Count == 0, "red ispražnjen");
			Assert.Equal(["A"], Sent());
		}

		[Fact]
		public async Task Retry_keeps_confirmation_and_does_not_block_the_next_one()
		{
			// A server stalno odbija prolaznom greškom, B prihvata: A ide na kraj reda, B se šalje i briše, A ostaje u fajlu.
			using var queue = Queue(c => c.Id == "A" ? ConfirmationSendResult.Retry : ConfirmationSendResult.Delivered);
			Assert.True(await queue.Add(new TestConfirmation("A")));
			Assert.True(await queue.Add(new TestConfirmation("B")));
			await WaitForFileAsync(ids => ids.SequenceEqual(["A"]), "ostala samo A");
			await WaitUntilAsync(() => Sent().Count(id => id == "A") >= 2, "A poslata ponovo");
			Assert.Contains("B", Sent());
		}

		[Fact]
		public async Task Expired_confirmation_is_removed_without_sending()
		{
			var old = new TestConfirmation("OLD") { CreatedUtc = DateTime.UtcNow - ConfirmationQueue<TestConfirmation>.MaxPendingAge - TimeSpan.FromMinutes(1) };
			await File.WriteAllTextAsync(QueueFile, JsonSerializer.Serialize(new[] { old }));
			using var queue = Queue(_ => ConfirmationSendResult.Retry);
			Assert.True(await queue.Add(new TestConfirmation("NEW")));
			await WaitForFileAsync(ids => ids.SequenceEqual(["NEW"]), "istekla uklonjena");
			Assert.DoesNotContain("OLD", Sent());
		}

		[Fact]
		public async Task Exception_from_send_keeps_confirmation_and_loop_running()
		{
			int calls = 0;
			using var queue = new ConfirmationQueue<TestConfirmation>("Test", "test potvrda", QueueFile, TimeSpan.FromMilliseconds(20),
				(_, _) => Interlocked.Increment(ref calls) == 1
							? throw new InvalidOperationException("E2E: slanje palo")
							: Task.FromResult(ConfirmationSendResult.Delivered),
				c => $"[{c.Id}]", NullLogger.Instance);
			Assert.True(await queue.Add(new TestConfirmation("A")));
			await WaitForFileAsync(ids => ids.Count == 0, "poslata posle izuzetka");
			Assert.True(Volatile.Read(ref calls) >= 2);
		}

		[Fact]
		public async Task Briefly_open_queue_file_does_not_fail_the_write()
		{
			// Drugi proces (antivirus, backup) kratko drži fajl reda otvorenim: zamena fajla tada pada, red je ponavlja.
			using var queue = Queue(_ => ConfirmationSendResult.Retry);
			Assert.True(await queue.Add(new TestConfirmation("A")));
			Task<bool> added;
			using (new FileStream(QueueFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
				added = queue.Add(new TestConfirmation("B"));
				await Task.Delay(120);
			}
			Assert.True(await added, "upis mora da uspe kad se fajl oslobodi");
			Assert.Equal(["A", "B"], ReadIds()!.Order());
		}

		[Theory]
		[InlineData(null)]
		[InlineData("")]
		public async Task Missing_path_blocks_file_writes_and_sends_from_memory(string? path)
		{
			// putanja bez fajla: ranije je upis išao u relativni ".tmp" u radnom folderu (prepisan, pa obrisan posle neuspelog Move)
			var strayTemp = Path.GetFullPath(".tmp");
			Assert.False(File.Exists(strayTemp), $"{strayTemp} vec postoji");
			await File.WriteAllTextAsync(strayTemp, "tudji fajl");
			try {
				using var queue = new ConfirmationQueue<TestConfirmation>("Test", "test potvrda", path!, TimeSpan.FromMilliseconds(20),
					(c, _) => {
						lock (_sentLock) {
							_sent.Add(c.Id);
						}
						return Task.FromResult(ConfirmationSendResult.Delivered);
					},
					c => $"[{c.Id}]", NullLogger.Instance);
				Assert.False(await queue.Add(new TestConfirmation("A")));
				await WaitUntilAsync(() => Sent().Contains("A"), "A poslata iz memorije");
				Assert.Equal("tudji fajl", await File.ReadAllTextAsync(strayTemp));
			}
			finally {
				File.Delete(strayTemp);
			}
		}

		[Theory]
		[InlineData(1820993, null, false, ConfirmationSendResult.Delivered)]
		[InlineData(null, null, true, ConfirmationSendResult.Retry)]
		[InlineData(null, "Za paket sa kodom \"11002813\" je već evidentirana uplata", false, ConfirmationSendResult.AlreadyDelivered)]
		[InlineData(null, "Iznos uplate 3,00 ne odgovara iznosu otkupa 4,00", false, ConfirmationSendResult.Rejected)]
		[InlineData(null, null, false, ConfirmationSendResult.Rejected)]
		public void Payment_confirmation_response_is_classified(int? paymentId, string? error, bool recoverable, ConfirmationSendResult expected)
			// „već evidentirana uplata" je odgovor pravog servera (de-test, 2026-10-09) na ponovljenu potvrdu: uplata postoji.
			=> Assert.Equal(expected, PaymentConfirmationSender.Classify(paymentId, error, recoverable));

		private List<string> Sent()
		{
			lock (_sentLock) {
				return [.. _sent];
			}
		}

		private async Task WaitForFileAsync(Func<List<string>, bool> condition, string what)
			=> await WaitUntilAsync(() => ReadIds() is { } ids && condition(ids), what);

		private List<string>? ReadIds()
		{
			try {
				// Čitanje se može poklopiti sa zamenom fajla u redu (File.Move(.tmp, overwrite)), koja tada pada i uz
				// FileShare.Delete; red zato zamenu ponavlja (Briefly_open_queue_file_does_not_fail_the_write).
				using var stream = new FileStream(QueueFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
				return JsonSerializer.Deserialize<List<TestConfirmation>>(stream)?.Select(c => c.Id).ToList();
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
				return null;    // fajl se upravo zamenjuje (File.Move)
			}
		}

		private static async Task WaitUntilAsync(Func<bool> condition, string what)
		{
			var deadline = DateTime.UtcNow + __timeout;
			while (!condition()) {
				if (DateTime.UtcNow > deadline) {
					throw new TimeoutException($"Isteklo {__timeout.TotalSeconds} s: {what}");
				}
				await Task.Delay(20);
			}
		}
	}
}
