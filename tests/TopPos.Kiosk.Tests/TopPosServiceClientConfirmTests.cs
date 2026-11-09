using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using TopPos.Service;
using Xunit;

namespace TopPos.Workflow.Tests
{
	/// <summary>
	/// Klasifikacija neuspeha potvrda prema serveru. Potvrda preuzimanja (<c>recoverable</c>): samo 400 je konačno
	/// odbijanje, sve ostalo (drugi status, telo koje nije JSON, mreža, isteklo vreme) je prolazno i potvrda se šalje ponovo.
	/// Potvrda ostavljanja (<see cref="DepositConfirmResult"/>) uz to razlikuje „sigurno nije upisano" od „ne zna se".
	/// Server je lokalni stub; klijent ide kroz ceo HTTP pipeline (Polly, RequestTimeout).
	/// </summary>
	public class TopPosServiceClientConfirmTests : IDisposable
	{
		private const int DoorNumber = 5;
		private const string HtmlBody = "<html><body>503 Service Unavailable</body></html>";

		private readonly StubHttpServer _server = new();

		public void Dispose() => _server.Dispose();

		private static TopPosServiceClient Client(string baseUrl, int requestTimeout = 1000) => new(
			new TopPosServiceConfiguration { BaseUrl = baseUrl, PathPrefix = "api", RequestTimeout = requestTimeout, DisableHeartbeats = true },
			"111", null, CancellationToken.None);

		private static async Task<(bool, string?, bool)> ConfirmPickup(TopPosServiceClient client)
			=> await client.RecipientPackagePickupConfirm(123456, "0641234567", DoorNumber, CancellationToken.None);

		private static async Task<(DepositConfirmResult, string?)> ConfirmDeposit(TopPosServiceClient client)
			=> await client.CourierPackageDepositConfirm("KURIR1", "BARKOD1", DoorNumber, CancellationToken.None);

		private static string Error(string message) => $"{{\"responseStatus\":{{\"errorCode\":\"ArgumentException\",\"message\":\"{message}\"}}}}";

		// ─── Potvrda preuzimanja ─────────────────────────────────────────────

		[Fact]
		public async Task Pickup_ok_with_same_door_is_confirmed()
		{
			_server.Respond(HttpStatusCode.OK, $"{{\"brojPozicije\":{DoorNumber}}}");
			Assert.Equal((true, null, false), await ConfirmPickup(Client(_server.BaseUrl)));
			Assert.Contains($"POST /api/{nameof(PrimaocPraznjenjePozicijePotvrda)}/", _server.LastRequestLine);
		}

		[Fact]
		public async Task Pickup_ok_with_other_door_is_not_confirmed_and_not_retried()
		{
			_server.Respond(HttpStatusCode.OK, $"{{\"brojPozicije\":{DoorNumber + 1}}}");
			Assert.Equal((false, null, false), await ConfirmPickup(Client(_server.BaseUrl)));
		}

		[Fact]
		public async Task Pickup_bad_request_is_final_with_server_message()
		{
			// poruka pravog servera (de-test, 2026-10-09)
			_server.Respond(HttpStatusCode.BadRequest, Error("Na poziciji se nalazi novi paket. Preuzimanje nije mogu\\u0107e."));
			Assert.Equal((false, "Na poziciji se nalazi novi paket. Preuzimanje nije moguće.", false), await ConfirmPickup(Client(_server.BaseUrl)));
		}

		[Theory]
		[InlineData("", "application/json")]
		[InlineData("null", "application/json")]
		[InlineData(HtmlBody, "text/html")]
		[InlineData("{}", "application/json")]
		[InlineData("{\"nekoDrugoPolje\":5}", "application/json")]
		public async Task Pickup_ok_with_invalid_body_is_recoverable(string body, string contentType)
		{
			// 200 bez ispravnog odgovora (npr. stranica proxy-ja, JSON bez broja pozicije): ne zna se da li je server primio
			// potvrdu. Pravi server broj pozicije vraća u svakom 200 (de-test, 2026-10-09).
			_server.Respond(HttpStatusCode.OK, body, contentType);
			Assert.Equal((false, null, true), await ConfirmPickup(Client(_server.BaseUrl)));
		}

		[Theory]
		[InlineData(HttpStatusCode.NotFound)]
		[InlineData(HttpStatusCode.Unauthorized)]
		public async Task Pickup_other_status_with_html_body_is_recoverable(HttpStatusCode status)
		{
			// statusi koje Polly ne ponavlja: klasifikuje ih IsRecoverable(HttpStatusCode)
			_server.Respond(status, HtmlBody, "text/html");
			Assert.Equal((false, null, true), await ConfirmPickup(Client(_server.BaseUrl)));
		}

		[Fact]
		public async Task Pickup_server_error_is_recoverable()
		{
			// Ishod se ovde ne može pripisati jednoj grani: 5xx Polly ponavlja (posle 2, 4, 8 s), a RequestTimeout (1 s) obično
			// prekine čekanje, pa ishod dolazi iz IsRecoverable(Exception); bez ponavljanja bi došao iz IsRecoverable(status).
			// Obe grane daju prolazno. Klasifikaciju samog statusa 5xx pokriva Status_classification.
			_server.Respond(HttpStatusCode.ServiceUnavailable, HtmlBody, "text/html");
			Assert.Equal((false, null, true), await ConfirmPickup(Client(_server.BaseUrl)));
		}

		[Fact]
		public async Task Pickup_unreachable_server_is_recoverable()
			=> Assert.Equal((false, null, true), await ConfirmPickup(Client($"http://127.0.0.1:{ClosedPort()}/")));

		[Theory]
		[InlineData(HttpStatusCode.BadRequest, false)]
		[InlineData(HttpStatusCode.Unauthorized, true)]
		[InlineData(HttpStatusCode.Forbidden, true)]
		[InlineData(HttpStatusCode.NotFound, true)]
		[InlineData(HttpStatusCode.RequestTimeout, true)]
		[InlineData(HttpStatusCode.TooManyRequests, true)]
		[InlineData(HttpStatusCode.InternalServerError, true)]
		[InlineData(HttpStatusCode.BadGateway, true)]
		[InlineData(HttpStatusCode.ServiceUnavailable, true)]
		[InlineData(HttpStatusCode.GatewayTimeout, true)]
		public void Status_classification(HttpStatusCode status, bool recoverable)
			=> Assert.Equal(recoverable, TopPosServiceClient.IsRecoverable(status));

		[Fact]
		public void Exception_classification()
		{
			Assert.All(new Exception[] { new TaskCanceledException(), new OperationCanceledException(), new HttpRequestException(),
										new SocketException(), new TimeoutException(), new IOException() },
						ex => Assert.True(TopPosServiceClient.IsRecoverable(ex), ex.GetType().Name));
			Assert.All(new Exception[] { new System.Text.Json.JsonException(), new InvalidOperationException(), new ArgumentException() },
						ex => Assert.False(TopPosServiceClient.IsRecoverable(ex), ex.GetType().Name));
		}

		// ─── Potvrda ostavljanja ─────────────────────────────────────────────

		[Fact]
		public async Task Deposit_ok_with_same_door_is_confirmed()
		{
			_server.Respond(HttpStatusCode.OK, $"{{\"popunjenBrojPozicije\":{DoorNumber}}}");
			Assert.Equal((DepositConfirmResult.Confirmed, null), await ConfirmDeposit(Client(_server.BaseUrl)));
			var (requestLine, body) = Assert.Single(_server.Requests);
			Assert.Contains($"POST /api/{nameof(KurirPunjenjePozicijePotvrda)}/", requestLine);
			// telo prolazi kroz omotač u ProcessingTrackingHandler-u (Content-Length, JSON)
			Assert.Contains("\"BARKOD1\"", body);
			Assert.Contains("\"KURIR1\"", body);
		}

		[Fact]
		public async Task Deposit_ok_with_other_door_is_rejected_and_not_retried()
		{
			// server naša vrata ne vodi kao zauzeta; ponavljanje ne menja odgovor
			_server.Respond(HttpStatusCode.OK, $"{{\"popunjenBrojPozicije\":{DoorNumber + 1}}}");
			Assert.Equal((DepositConfirmResult.Rejected, null), await ConfirmDeposit(Client(_server.BaseUrl)));
			Assert.Single(_server.Requests);
		}

		[Fact]
		public async Task Deposit_bad_request_is_rejected_with_server_message()
		{
			// poruka pravog servera (de-test, 2026-10-09)
			_server.Respond(HttpStatusCode.BadRequest, Error("Prijavljena pozicija \\\"5\\\" se ne poklapa sa slobodnom pozicijom iz sistema"));
			Assert.Equal((DepositConfirmResult.Rejected, "Prijavljena pozicija \"5\" se ne poklapa sa slobodnom pozicijom iz sistema"),
						await ConfirmDeposit(Client(_server.BaseUrl)));
		}

		[Theory]
		[InlineData("", "application/json")]
		[InlineData("null", "application/json")]
		[InlineData(HtmlBody, "text/html")]
		[InlineData("{}", "application/json")]
		[InlineData("{\"nekoDrugoPolje\":5}", "application/json")]
		public async Task Deposit_ok_with_invalid_body_is_unknown(string body, string contentType)
		{
			// server je odgovorio 200 (obradio zahtev), a iz odgovora se ne vidi da li je potvrdio
			_server.Respond(HttpStatusCode.OK, body, contentType);
			Assert.Equal((DepositConfirmResult.Unknown, null), await ConfirmDeposit(Client(_server.BaseUrl)));
		}

		[Theory]
		// Polly ih ne ponavlja: ishod je ishod jedinog zahteva
		[InlineData(HttpStatusCode.Unauthorized)]
		[InlineData(HttpStatusCode.Forbidden)]
		[InlineData(HttpStatusCode.NotFound)]
		[InlineData(HttpStatusCode.TooManyRequests)]
		// Polly ih ponavlja (posle 2 s), a RequestTimeout (1 s) prekine pauzu: zahtev nije bio u toku, ishod je poslednji odgovor
		[InlineData(HttpStatusCode.RequestTimeout)]
		[InlineData(HttpStatusCode.ServiceUnavailable)]
		public async Task Deposit_status_meaning_not_processed_is_not_delivered(HttpStatusCode status)
		{
			_server.Respond(status, HtmlBody, "text/html");
			Assert.Equal((DepositConfirmResult.NotDelivered, null), await ConfirmDeposit(Client(_server.BaseUrl)));
		}

		[Theory]
		[InlineData(HttpStatusCode.Conflict)]				// Polly ga ne ponavlja
		[InlineData(HttpStatusCode.InternalServerError)]	// 5xx Polly ponavlja; prekid u pauzi ne briše oznaku prvog odgovora
		[InlineData(HttpStatusCode.BadGateway)]				// proxy: server je možda obradio zahtev
		[InlineData(HttpStatusCode.GatewayTimeout)]
		public async Task Deposit_status_after_possible_processing_is_unknown(HttpStatusCode status)
		{
			_server.Respond(status, HtmlBody, "text/html");
			Assert.Equal((DepositConfirmResult.Unknown, null), await ConfirmDeposit(Client(_server.BaseUrl)));
		}

		[Fact]
		public async Task Deposit_unreachable_server_is_not_delivered()
			=> Assert.Equal((DepositConfirmResult.NotDelivered, null), await ConfirmDeposit(Client($"http://127.0.0.1:{ClosedPort()}/")));

		[Fact]
		public async Task Deposit_response_later_than_request_timeout_is_unknown()
		{
			_server.ResponseDelay = TimeSpan.FromSeconds(3);
			_server.Respond(HttpStatusCode.OK, $"{{\"popunjenBrojPozicije\":{DoorNumber}}}");
			Assert.Equal((DepositConfirmResult.Unknown, null), await ConfirmDeposit(Client(_server.BaseUrl)));
			Assert.Single(_server.Requests);
		}

		[Fact]
		public async Task Deposit_connection_closed_without_response_is_unknown()
		{
			_server.DropConnections();
			Assert.Equal((DepositConfirmResult.Unknown, null), await ConfirmDeposit(Client(_server.BaseUrl)));
		}

		[Fact]
		public async Task Deposit_unknown_attempt_is_not_cleared_by_later_not_processed_response()
		{
			// Polly ponavlja isti zahtev: prvi pokušaj ostane bez odgovora, drugi dobije 404 (sam po sebi „nije obrađeno")
			_server.DropNextConnections(1);
			_server.Respond(HttpStatusCode.NotFound, HtmlBody, "text/html");
			Assert.Equal((DepositConfirmResult.Unknown, null), await ConfirmDeposit(Client(_server.BaseUrl, requestTimeout: 5000)));
			Assert.Equal(2, _server.Requests.Count);
		}

		[Fact]
		public async Task Deposit_retry_after_unknown_attempt_can_still_be_confirmed()
		{
			// ponovljena potvrda za ista vrata je bezbedna: 200 posle izgubljenog odgovora znači da je ostavljanje upisano
			_server.DropNextConnections(1);
			_server.Respond(HttpStatusCode.OK, $"{{\"popunjenBrojPozicije\":{DoorNumber}}}");
			Assert.Equal((DepositConfirmResult.Confirmed, null), await ConfirmDeposit(Client(_server.BaseUrl, requestTimeout: 5000)));
		}

		// port na kome sigurno niko ne sluša: zauzme se slobodan, pa odmah oslobodi
		private static int ClosedPort()
		{
			var listener = new TcpListener(IPAddress.Loopback, 0);
			listener.Start();
			var port = ((IPEndPoint)listener.LocalEndpoint).Port;
			listener.Stop();
			return port;
		}

		[Fact]
		public async Task Payment_confirmation_classification_is_unchanged()
		{
			var client = Client(_server.BaseUrl);
			_server.Respond(HttpStatusCode.BadRequest, Error("Za paket sa kodom \\\"11002813\\\" je ve\\u0107 evidentirana uplata"));
			Assert.Equal((null, "Za paket sa kodom \"11002813\" je već evidentirana uplata", false),
						await client.PaymentConfirmation("11002813", "000041", 4m, "SIMUL001", CancellationToken.None));
			_server.Respond(HttpStatusCode.NotFound, HtmlBody, "text/html");
			Assert.Equal((null, null, true), await client.PaymentConfirmation("11002813", "000041", 4m, "SIMUL001", CancellationToken.None));
			_server.Respond(HttpStatusCode.OK, "{\"evidencioniBrojUplate\":1820993}");
			Assert.Equal((1820993, null, false), await client.PaymentConfirmation("11002813", "000041", 4m, "SIMUL001", CancellationToken.None));
		}
	}
}
