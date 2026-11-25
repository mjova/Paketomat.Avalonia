using System.Net;
using Flatsoft.Common;
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

		/// <summary>Prijava kurira prema stub serveru; vraća početnu liniju zahteva koju je server dobio i sve poruke loga.</summary>
		private static async Task<(string RequestLine, List<string> Log)> LogCourierLogin(string code, HttpStatusCode status, string body)
		{
			using var server = new StubHttpServer();
			server.Respond(status, body);
			var logger = new KioskOperationCoordinatorTests.ListLogger();
			var client = new TopPosServiceClient(
				new TopPosServiceConfiguration { BaseUrl = server.BaseUrl, PathPrefix = "api", RequestTimeout = 2000, DisableHeartbeats = true },
				"111", logger, CancellationToken.None);

			await client.CourierLogin(code, CancellationToken.None);

			return (server.LastRequestLine, [.. logger.Entries.Select(e => e.Message)]);
		}

		// Ručni deo koda je bez cifara: port stub servera (u istom logu) ne može slučajno da ga sadrži.
		[Theory]
		[InlineData(HttpStatusCode.OK, "{\"sifraKurira\":\"K7\"}")]
		[InlineData(HttpStatusCode.BadRequest, "{\"responseStatus\":{\"errorCode\":\"ArgumentException\",\"message\":\"Nepoznat kurir\"}}")]
		public async Task Courier_login_code_is_masked_in_service_and_http_log(HttpStatusCode status, string body)
		{
			var (requestLine, log) = await LogCourierLogin("KARTICA-998877;WXYZ", status, body);

			Assert.Contains("QrKod=KARTICA-998877%3BWXYZ", requestLine);    // server dobija ceo kod (kodiran)
			Assert.DoesNotContain(log, m => m.Contains("KARTICA-998877") || m.Contains("WXYZ"));
			Assert.Contains(log, m => m.Contains("CourierLogin: GET") && m.Contains("QrKod=*****CA-998877;*****YZ"));
			Assert.Contains(log, m => m.Contains("HTTP request") && m.Contains("QrKod=*****CA-998877;*****YZ"));
		}

		/// <summary>Kod sa '&amp;': server dobija ceo kod (kodiran), a u logu je maskiran do kraja adrese.</summary>
		[Fact]
		public async Task Courier_login_code_with_ampersand_is_masked_to_the_end()
		{
			var (_, log) = await LogCourierLogin("KAR&TICA-77;WXYZ", HttpStatusCode.OK, "{\"sifraKurira\":\"K7\"}");

			Assert.DoesNotContain(log, m => m.Contains("KAR&") || m.Contains("WXYZ"));     // bez maske do kraja adrese ručni kod bi ostao
			Assert.Contains(log, m => m.Contains("CourierLogin: GET") && m.Contains("QrKod=*****TICA-77;*****YZ"));
			Assert.Contains(log, m => m.Contains("HTTP request") && m.Contains("QrKod=*****"));
		}

		/// <summary>Skener sklanja samo CR, pa LF unutar QR koda stiže do prijave; maska ga obuhvata (regex Singleline, nalaz iz v2).</summary>
		[Fact]
		public async Task Courier_login_code_with_line_feed_is_masked()
		{
			var (_, log) = await LogCourierLogin("KART\nICA-998877;WXYZ", HttpStatusCode.OK, "{\"sifraKurira\":\"K7\"}");

			Assert.DoesNotContain(log, m => m.Contains("KART") || m.Contains("WXYZ"));
			Assert.Contains(log, m => m.Contains("CourierLogin: GET") && m.Contains("QrKod=*****ICA-998877;*****YZ"));
			Assert.Contains(log, m => m.Contains("HTTP request") && m.Contains("QrKod=*****"));
		}
	}
}