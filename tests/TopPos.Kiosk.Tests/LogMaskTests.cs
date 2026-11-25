using System.Net;
using Flatsoft.Common;
using Microsoft.Extensions.Logging;
using TopPos.Service;
using Xunit;

namespace TopPos.Kiosk.Tests
{
	/// <summary>
	/// v4: pristupni kod u logu (<see cref="LogMask"/>): prva trećina skrivena iza uvek istog broja zvezdica, ostatak vidljiv
	/// za proveru ko se prijavio. Prijava kurira ne ostavlja kod u logu servisa ni u ispisu HTTP zahteva.
	/// </summary>
	public class LogMaskTests
	{
		[Theory]
		[InlineData("ABCDEFGHI", "*****DEFGHI")]
		[InlineData("1234", "*****34")]
		[InlineData("AB", "*****B")]
		[InlineData("7", "*****")]
		[InlineData("", "*****")]
		[InlineData(null, "*****")]
		[InlineData("QR-123;4321", "*****-123;*****21")]
		public void Hides_first_third_behind_fixed_stars(string? value, string expected)
			=> Assert.Equal(expected, LogMask.Credential(value));

		[Fact]
		public void Hidden_length_is_not_visible()
			=> Assert.Equal(LogMask.Credential("ABCDEF")[..5], LogMask.Credential("ABCDEFGHIJKLMNO")[..5]);

		[Theory]
		[InlineData(HttpStatusCode.OK, "{\"sifraKurira\":\"K7\"}")]
		[InlineData(HttpStatusCode.BadRequest, "{\"responseStatus\":{\"errorCode\":\"ArgumentException\",\"message\":\"Nepoznat kurir\"}}")]
		public async Task Courier_login_code_is_masked_in_service_and_http_log(HttpStatusCode status, string body)
		{
			using var server = new StubHttpServer();
			server.Respond(status, body);
			var logger = new KioskOperationCoordinatorTests.ListLogger();
			var client = new TopPosServiceClient(
				new TopPosServiceConfiguration { BaseUrl = server.BaseUrl, PathPrefix = "api", RequestTimeout = 2000, DisableHeartbeats = true },
				"111", logger, CancellationToken.None);

			await client.CourierLogin("KARTICA-998877;4321", CancellationToken.None);

			Assert.Contains("QrKod=KARTICA-998877;4321", server.LastRequestLine);    // server dobija ceo kod
			var log = string.Join(Environment.NewLine, logger.Entries.Select(e => e.Message));
			Assert.DoesNotContain("KARTICA-998877", log);
			Assert.DoesNotContain("4321", log);
			Assert.Contains("QrKod=*****CA-998877;*****21", log);
			Assert.Contains("HttpLoggingHandler", log);     // ispis HTTP zahteva je bio uključen (Debug)
		}

		/// <summary>Kod sa '&amp;' se u URL ne kodira (kao v2), pa je deo posle njega za server drugi parametar — u logu ostaje maskiran.</summary>
		[Fact]
		public async Task Courier_login_code_with_ampersand_is_masked_to_the_end()
		{
			using var server = new StubHttpServer();
			server.Respond(HttpStatusCode.OK, "{\"sifraKurira\":\"K7\"}");
			var logger = new KioskOperationCoordinatorTests.ListLogger();
			var client = new TopPosServiceClient(
				new TopPosServiceConfiguration { BaseUrl = server.BaseUrl, PathPrefix = "api", RequestTimeout = 2000, DisableHeartbeats = true },
				"111", logger, CancellationToken.None);

			await client.CourierLogin("KAR&TICA-77;4321", CancellationToken.None);

			var log = string.Join(Environment.NewLine, logger.Entries.Select(e => e.Message));
			Assert.DoesNotContain("KAR&", log);
			Assert.DoesNotContain("4321", log);     // bez maske do kraja adrese ručni kod bi ostao vidljiv
			Assert.Contains("QrKod=*****TICA-77;*****21", log);
		}
	}
}