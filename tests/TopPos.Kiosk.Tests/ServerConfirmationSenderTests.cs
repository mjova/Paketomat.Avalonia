using System.IO;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TopPos.Kiosk;
using TopPos.Service;
using Xunit;

namespace TopPos.Kiosk.Tests
{
	/// <summary>
	/// <see cref="ServerConfirmationSender"/>: red potvrda preuzimanja koje nisu stigle do servera zbog prolazne greške.
	/// Server je lokalni stub; fajl reda je dokaz šta je ostalo za slanje (posle restarta se šalje samo ono što je u njemu).
	/// Učitavanje/upis fajla pokrivaju <see cref="ConfirmationQueueTests"/> i <see cref="PaymentConfirmationSenderTests"/>.
	/// </summary>
	public class ServerConfirmationSenderTests : IDisposable
	{
		private const int DoorNumber = 5;
		private static readonly TimeSpan __timeout = TimeSpan.FromSeconds(10);

		private static readonly ServerConfirmation Pickup = ServerConfirmation.ForRecipientPickup(123456, "0641234567", DoorNumber);
		private static readonly string OkBody = $"{{\"brojPozicije\":{DoorNumber}}}";

		private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("toppos-serverconf-");
		private readonly StubHttpServer _server = new();

		private string QueueFile => Path.Combine(_dir.FullName, "ServerConfirmations.json");

		public void Dispose()
		{
			_server.Dispose();
			_dir.Delete(recursive: true);
		}

		private ServerConfirmationSender Sender() => new(
			new ServerConfirmationSenderConfiguration { ServerConfirmationFilename = QueueFile, ProcessingDelayInMilliseconds = 20 },
			new TopPosServiceClient(new TopPosServiceConfiguration { BaseUrl = _server.BaseUrl, PathPrefix = "api", RequestTimeout = 1000, DisableHeartbeats = true },
									"111", null, CancellationToken.None),
			NullLogger.Instance);

		[Fact]
		public async Task Accepted_confirmation_is_sent_with_its_data_and_removed()
		{
			_server.Respond(HttpStatusCode.OK, OkBody);
			using var sender = Sender();
			Assert.True(await sender.Add(Pickup));
			await WaitForFileAsync(entries => entries.Count == 0, "red ispražnjen");

			var (requestLine, body) = Assert.Single(_server.Requests);
			// telo se poredi posle parsiranja: DEBUG build šalje uvučen JSON
			Assert.Contains("POST /api/PrimaocPraznjenjePozicijePotvrda/", requestLine);
			Assert.Equal("123456", RequestValue(body, "kodZaOtvaranje"));
			Assert.Equal("0641234567", RequestValue(body, "telefonPrimaoca"));
			Assert.Equal(DoorNumber.ToString(), RequestValue(body, "brojPozicije"));
		}

		[Fact]
		public async Task Transient_failure_keeps_confirmation_until_server_recovers()
		{
			_server.Respond(HttpStatusCode.NotFound, "<html>proxy</html>", "text/html");	// prolazno, Polly ga ne ponavlja
			using var sender = Sender();
			Assert.True(await sender.Add(Pickup));
			await WaitUntilAsync(() => _server.Requests.Count >= 2, "ponovljeno slanje");
			Assert.Contains($"\"Kind\":\"{Pickup.Kind}\"", await ReadFileAsync());	// ostaje u fajlu, čitljivo

			_server.Respond(HttpStatusCode.OK, OkBody);
			await WaitForFileAsync(entries => entries.Count == 0, "poslata kad je server proradio");
		}

		[Fact]
		public async Task Rejected_confirmation_is_removed()
		{
			// 400 (npr. „Na poziciji se nalazi novi paket. Preuzimanje nije moguće."): ponavljanje ne pomaže
			_server.Respond(HttpStatusCode.BadRequest, "{\"responseStatus\":{\"errorCode\":\"ArgumentException\",\"message\":\"odbijeno\"}}");
			using var sender = Sender();
			Assert.True(await sender.Add(Pickup));
			await WaitForFileAsync(entries => entries.Count == 0, "odbijena uklonjena");
			Assert.Single(_server.Requests);
		}

		[Theory]
		[InlineData("[{\"Kind\":\"RecipientPickup\",\"DoorNumber\":5,\"UnlockCode\":123456}]")]	// bez telefona
		[InlineData("[{\"Kind\":\"RecipientPickup\",\"UnlockCode\":123456,\"TelephoneNumber\":\"0641234567\"}]")]	// bez vrata (učita se 0)
		public async Task Incomplete_entry_from_file_is_removed_without_sending(string content)
		{
			await File.WriteAllTextAsync(QueueFile, content);
			_server.Respond(HttpStatusCode.OK, "{}");
			using var sender = Sender();
			await WaitForFileAsync(entries => entries.Count == 0, "nepotpun zapis uklonjen");
			Assert.Empty(_server.Requests);
		}

		[Fact]
		public async Task Confirmations_survive_restart()
		{
			_server.Respond(HttpStatusCode.NotFound, "<html>proxy</html>", "text/html");
			using (var sender = Sender()) {
				Assert.True(await sender.Add(Pickup));
			}
			_server.Respond(HttpStatusCode.OK, OkBody);
			Assert.Single(await ReadEntriesAsync());
			// prva instanca je potvrdu već slala (404): proveravaju se samo zahtevi posle restarta, sa podacima iz fajla
			var beforeRestart = _server.Requests.Count;
			using (var sender = Sender()) {
				await WaitForFileAsync(entries => entries.Count == 0, "posle restarta poslata");
			}
			var afterRestart = _server.Requests.Skip(beforeRestart).ToList();
			var pickupBody = Assert.Single(afterRestart, r => r.RequestLine.Contains("PrimaocPraznjenjePozicijePotvrda")).Body;
			Assert.Equal("123456", RequestValue(pickupBody, "kodZaOtvaranje"));
			Assert.Equal("0641234567", RequestValue(pickupBody, "telefonPrimaoca"));
			Assert.Equal(DoorNumber.ToString(), RequestValue(pickupBody, "brojPozicije"));
		}

		[Theory]
		[InlineData(true, false, ConfirmationSendResult.Delivered)]
		[InlineData(false, true, ConfirmationSendResult.Retry)]
		[InlineData(false, false, ConfirmationSendResult.Rejected)]
		public void Response_is_classified(bool confirmed, bool recoverable, ConfirmationSendResult expected)
			=> Assert.Equal(expected, ServerConfirmationSender.Classify(confirmed, recoverable));

		private static string? RequestValue(string body, string property)
		{
			using var document = JsonDocument.Parse(body);
			return document.RootElement.TryGetProperty(property, out var value)
					? (value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText())
					: null;
		}

		private async Task<string> ReadFileAsync()
		{
			// FileShare.Delete: bez toga otvoren fajl na Windows-u obara File.Move(.tmp, overwrite) u redu
			await using var stream = new FileStream(QueueFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			using var reader = new StreamReader(stream);
			return await reader.ReadToEndAsync();
		}

		private async Task<List<ServerConfirmation>> ReadEntriesAsync()
			=> JsonSerializer.Deserialize<List<ServerConfirmation>>(await ReadFileAsync()) ?? [];

		private async Task WaitForFileAsync(Func<List<ServerConfirmation>, bool> condition, string what)
		{
			var deadline = DateTime.UtcNow + __timeout;
			while (true) {
				try {
					if (condition(await ReadEntriesAsync())) {
						return;
					}
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
					// fajl se upravo zamenjuje ili još ne postoji
				}
				if (DateTime.UtcNow > deadline) {
					throw new TimeoutException($"Isteklo {__timeout.TotalSeconds} s: {what}");
				}
				await Task.Delay(20);
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
