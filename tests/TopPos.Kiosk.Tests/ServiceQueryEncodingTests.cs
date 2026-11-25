using System.Net;
using System.Web;
using TopPos.Service;
using Xunit;

namespace TopPos.Kiosk.Tests
{
	/// <summary>
	/// v4: vrednosti u upitu (GET) se kodiraju. Ranije (v2) su išle sirove: sa '&amp;' ili '#' server je dobijao skraćen barkod (dodela
	/// vrata za drugi paket nego što potvrda ostavljanja navodi), a '+' u telefonu je stizao kao razmak.
	/// </summary>
	public class ServiceQueryEncodingTests : IDisposable
	{
		private readonly StubHttpServer _server = new();

		public void Dispose() => _server.Dispose();

		private TopPosServiceClient Client()
			=> new(new TopPosServiceConfiguration { BaseUrl = _server.BaseUrl, PathPrefix = "api", RequestTimeout = 2000, DisableHeartbeats = true },
				   "111", null, CancellationToken.None);

		/// <summary>Parametar upita kako ga server dekodira (kao HttpListener u simulatoru).</summary>
		private string? Query(string name)
		{
			var target = _server.LastRequestLine.Split(' ')[1];
			return HttpUtility.ParseQueryString(target[(target.IndexOf('?') + 1)..])[name];
		}

		[Theory]
		[InlineData("PKG&9")]
		[InlineData("PKG#9")]
		[InlineData("PKG 9+;/?")]
		public async Task Deposit_door_request_carries_whole_barcode(string barcode)
		{
			_server.Respond(HttpStatusCode.OK, "{\"brojPozicije\":7}");

			var (door, _) = await Client().CourierRequestPackageDeposit("K7", barcode, CancellationToken.None);

			Assert.Equal(7, door);
			Assert.Equal(barcode, Query("Barkod"));
			Assert.Equal("K7", Query("SifraKurira"));
		}

		[Fact]
		public async Task Courier_login_carries_whole_code()
		{
			_server.Respond(HttpStatusCode.OK, "{\"sifraKurira\":\"K7\"}");

			await Client().CourierLogin("KAR&TICA#1;4321", CancellationToken.None);

			Assert.Equal("KAR&TICA#1;4321", Query("QrKod"));
		}

		[Fact]
		public async Task Recipient_phone_with_plus_is_not_turned_into_space()
		{
			_server.Respond(HttpStatusCode.OK, "{\"brojPozicije\":5,\"iznosZaUplatu\":0}");

			await Client().RecipientRequestPackagePickupDetailed("+381641234567", 123456, CancellationToken.None);

			Assert.Equal("+381641234567", Query("TelefonPrimaoca"));
			Assert.Equal("123456", Query("KodZaOtvaranje"));
		}
	}
}
