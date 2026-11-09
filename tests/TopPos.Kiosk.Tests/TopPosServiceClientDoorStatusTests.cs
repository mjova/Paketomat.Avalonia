using System.Net;
using TopPos.Service;
using Xunit;

namespace TopPos.Kiosk.Tests
{
	/// <summary>
	/// Prijava i poništavanje greške pozicije: koji odgovori 400 znače da je stanje vrata primljeno, a koji da ostaje na
	/// čekanju. Tela odgovora su odgovori pravog servera (de-test, 3.11.2026, paketomat 111, pozicija 999).
	/// Incident 99129 (3.11.2026): server ne poznaje pozicije 43–48, a stanje tih vrata je zauvek zadržavalo prijavu kvara.
	/// </summary>
	public class TopPosServiceClientDoorStatusTests : IDisposable
	{
		private const int DoorNumber = 999;

		private const string UnknownDoorReport = """{"responseStatus":{"errorCode":"ArgumentException","message":"Na paketomatu \"111\" ne postoji broj pozicije \"999\"","errors":[{"errorCode":"ArgumentException","fieldName":"BrojPozicije","message":"Na paketomatu \"111\" ne postoji broj pozicije \"999\""}]},"brojPozicije":0}""";
		private const string UnknownDoorClear = """{"responseStatus":{"errorCode":"ArgumentException","message":"Na paketomatu \"111\" ne postoji navedeni broj pozicije \"999\"","errors":[{"errorCode":"ArgumentException","fieldName":"BrojPozicije","message":"Na paketomatu \"111\" ne postoji navedeni broj pozicije \"999\""}]},"brojPozicije":0}""";
		private const string LockerInErrorMessage = "Paketomat na benzinskoj stanici \"111\" se nalazi u stanju greške";
		private const string LockerInError = """{"responseStatus":{"errorCode":"ArgumentException","message":"Paketomat na benzinskoj stanici \"111\" se nalazi u stanju greške","errors":[{"errorCode":"ArgumentException","fieldName":"SifraBenzinskeStanice","message":"Paketomat na benzinskoj stanici \"111\" se nalazi u stanju greške"}]},"brojPozicije":0}""";

		private readonly StubHttpServer _server = new();

		public void Dispose() => _server.Dispose();

		private TopPosServiceClient Client() => new(
			new TopPosServiceConfiguration { BaseUrl = _server.BaseUrl, PathPrefix = "api", RequestTimeout = 1000, DisableHeartbeats = true },
			"111", null, CancellationToken.None);

		[Fact]
		public async Task Report_for_door_unknown_to_server_is_accepted()
		{
			_server.Respond(HttpStatusCode.BadRequest, UnknownDoorReport);
			Assert.Equal((true, null), await Client().ReportDoorErrorState(DoorNumber, PrijavaGreskePozicije.DoorStatusUnknown, CancellationToken.None));
			Assert.Contains($"POST /api/{nameof(PrijavaGreskePozicije)}/", _server.LastRequestLine);
		}

		[Fact]
		public async Task Clear_for_door_unknown_to_server_is_accepted()
		{
			_server.Respond(HttpStatusCode.BadRequest, UnknownDoorClear);
			Assert.Equal((true, null), await Client().ClearDoorErrorState(DoorNumber, CancellationToken.None));
			Assert.Contains($"POST /api/{nameof(PonistavanjeGreskePozicije)}", _server.LastRequestLine);
		}

		/// <summary>
		/// Server stanje paketomata proverava pre broja pozicije: dok paketomat vodi u grešci, odbija stanje svih vrata. To
		/// stanje mora ostati na čekanju i otići kad paketomat ponovo radi.
		/// </summary>
		[Fact]
		public async Task Report_while_locker_in_error_is_not_accepted()
		{
			_server.Respond(HttpStatusCode.BadRequest, LockerInError);
			Assert.Equal((false, LockerInErrorMessage), await Client().ReportDoorErrorState(DoorNumber, PrijavaGreskePozicije.DoorStatusUnknown, CancellationToken.None));
		}

		[Fact]
		public async Task Clear_while_locker_in_error_is_not_accepted()
		{
			_server.Respond(HttpStatusCode.BadRequest, LockerInError);
			Assert.Equal((false, LockerInErrorMessage), await Client().ClearDoorErrorState(DoorNumber, CancellationToken.None));
		}
	}
}