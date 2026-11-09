using System.Net;
using System.Net.Sockets;
using TopPos.Service;
using Xunit;
using R = TopPos.Service.RecipientPickupLookupResult;

namespace TopPos.Workflow.Tests
{
	/// <summary>
	/// Provera preuzimanja (<see cref="PrimaocPraznjenjePozicije"/>): <see cref="TopPosServiceClient.RecipientRequestPackagePickupDetailed"/>
	/// razlikuje odbijanje (samo 400) od izostanka ispravnog odgovora, a <see cref="TopPosServiceClient.RecipientRequestPackagePickup"/>
	/// za lokalni tok vraća iste vrednosti kao pre te podele. Server je lokalni stub; klijent ide kroz ceo HTTP pipeline.
	/// </summary>
	public class TopPosServiceClientRecipientPickupTests : IDisposable
	{
		private const string HtmlBody = "<html><body>503 Service Unavailable</body></html>";

		private readonly StubHttpServer _server = new();

		public void Dispose() => _server.Dispose();

		private static TopPosServiceClient Client(string baseUrl, int requestTimeout = 1000) => new(
			new TopPosServiceConfiguration { BaseUrl = baseUrl, PathPrefix = "api", RequestTimeout = requestTimeout, DisableHeartbeats = true },
			"111", null, CancellationToken.None);

		private static async Task<(R, int?, decimal?, string?)> Lookup(TopPosServiceClient client)
			=> await client.RecipientRequestPackagePickupDetailed("0641234567", 123456, CancellationToken.None);

		private static async Task<(int?, decimal?, string?)> LocalLookup(TopPosServiceClient client)
			=> await client.RecipientRequestPackagePickup("0641234567", 123456, CancellationToken.None);

		private static string Error(string message) => $"{{\"responseStatus\":{{\"errorCode\":\"ArgumentException\",\"message\":\"{message}\"}}}}";

		[Fact]
		public async Task Ok_with_door_is_found()
		{
			_server.Respond(HttpStatusCode.OK, "{\"brojPozicije\":5,\"iznosZaUplatu\":0}");
			Assert.Equal((R.Found, 5, 0m, null), await Lookup(Client(_server.BaseUrl)));
			Assert.Contains($"GET /api/{nameof(PrimaocPraznjenjePozicije)}/?SifraBenzinskeStanice=111&TelefonPrimaoca=0641234567&KodZaOtvaranje=123456", _server.LastRequestLine);
		}

		[Fact]
		public async Task Ok_with_amount_is_found_with_door_zero()
		{
			_server.Respond(HttpStatusCode.OK, "{\"brojPozicije\":0,\"iznosZaUplatu\":350.50}");
			Assert.Equal((R.Found, 0, 350.50m, null), await Lookup(Client(_server.BaseUrl)));
		}

		[Fact]
		public async Task Bad_request_is_rejected_with_server_message()
		{
			_server.Respond(HttpStatusCode.BadRequest, Error("Pogre\\u0161an kod"));
			Assert.Equal((R.Rejected, null, null, "Pogrešan kod"), await Lookup(Client(_server.BaseUrl)));
		}

		[Fact]
		public async Task Bad_request_without_message_is_rejected()
		{
			_server.Respond(HttpStatusCode.BadRequest, "", "text/plain");
			Assert.Equal((R.Rejected, null, null, null), await Lookup(Client(_server.BaseUrl)));
		}

		[Theory]
		[InlineData(HttpStatusCode.InternalServerError)]
		[InlineData(HttpStatusCode.ServiceUnavailable)]
		[InlineData(HttpStatusCode.NotFound)]
		[InlineData(HttpStatusCode.Unauthorized)]
		public async Task Other_status_with_html_body_is_no_response(HttpStatusCode status)
		{
			_server.Respond(status, HtmlBody, "text/html");
			Assert.Equal((R.NoResponse, null, null, null), await Lookup(Client(_server.BaseUrl)));
		}

		[Fact]
		public async Task Other_status_keeps_server_message()
		{
			_server.Respond(HttpStatusCode.NotFound, Error("Nema paketa"));
			Assert.Equal((R.NoResponse, null, null, "Nema paketa"), await Lookup(Client(_server.BaseUrl)));
		}

		[Theory]
		[InlineData("", "application/json")]
		[InlineData("null", "application/json")]
		[InlineData(HtmlBody, "text/html")]
		public async Task Ok_with_invalid_body_is_no_response(string body, string contentType)
		{
			_server.Respond(HttpStatusCode.OK, body, contentType);
			Assert.Equal((R.NoResponse, null, null, null), await Lookup(Client(_server.BaseUrl)));
		}

		[Fact]
		public async Task Response_later_than_request_timeout_is_no_response()
		{
			_server.ResponseDelay = TimeSpan.FromSeconds(3);
			_server.Respond(HttpStatusCode.OK, "{\"brojPozicije\":5,\"iznosZaUplatu\":0}");
			Assert.Equal((R.NoResponse, null, null, null), await Lookup(Client(_server.BaseUrl)));
		}

		[Fact]
		public async Task Unreachable_server_is_no_response()
			=> Assert.Equal((R.NoResponse, null, null, null), await Lookup(Client($"http://127.0.0.1:{ClosedPort()}/")));

		// ─── Lokalni tok: iste vrednosti kao pre podele ─────────────────────

		[Fact]
		public async Task Local_ok_returns_door_and_amount()
		{
			_server.Respond(HttpStatusCode.OK, "{\"brojPozicije\":0,\"iznosZaUplatu\":350.50}");
			Assert.Equal((0, 350.50m, null), await LocalLookup(Client(_server.BaseUrl)));
		}

		[Theory]
		[InlineData(HttpStatusCode.BadRequest)]
		[InlineData(HttpStatusCode.NotFound)]
		public async Task Local_error_status_returns_server_message(HttpStatusCode status)
		{
			_server.Respond(status, Error("Pogre\\u0161an kod"));
			Assert.Equal((null, null, "Pogrešan kod"), await LocalLookup(Client(_server.BaseUrl)));
		}

		[Fact]
		public async Task Local_no_response_returns_nothing()
			=> Assert.Equal((null, null, null), await LocalLookup(Client($"http://127.0.0.1:{ClosedPort()}/")));

		private static int ClosedPort()
		{
			var listener = new TcpListener(IPAddress.Loopback, 0);
			listener.Start();
			var port = ((IPEndPoint)listener.LocalEndpoint).Port;
			listener.Stop();
			return port;
		}
	}
}
