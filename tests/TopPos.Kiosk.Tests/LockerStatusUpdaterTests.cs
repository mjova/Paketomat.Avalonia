using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using TopPos.Kiosk;
using TopPos.LockController;
using TopPos.Service;
using Xunit;

namespace TopPos.Kiosk.Tests
{
	/// <summary>
	/// <see cref="LockerStatusUpdater"/>: greška vrata koju server još nije primio i redovno očitavanje vrata.
	/// Server je namerno nedostupan (zatvoren port, kratak rok), pa ništa iz liste za slanje ne odlazi. Polazno stanje u
	/// ServerStatus.json: server zna da su vrata 3 zatvorena i ispravna (kao posle prvog očitavanja pri startu).
	/// Izuzetak: test pozicije koju server ne poznaje ima svoj lokalni server (<see cref="StubHttpServer"/>).
	/// Vault: backlog/LockerStatusUpdater — greška vrata izgubljena pre slanja.
	/// </summary>
	public class LockerStatusUpdaterTests : IDisposable
	{
		private const int Door = 3;
		private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("toppos-status-");
		private readonly TopPosServiceClient _serviceClient = new(
			new TopPosServiceConfiguration { BaseUrl = "http://127.0.0.1:9/", PathPrefix = "api", RequestTimeout = 200, DisableHeartbeats = true },
			"TEST", logger: null, CancellationToken.None);

		private readonly ListLogger _log = new();

		private string StatusFile =>Path.Combine(_dir.FullName, "ServerStatus.json");

		public void Dispose() => _dir.Delete(recursive: true);

		/// <summary>
		/// Greška „N“ (vrata se nisu otvorila, ostala su zatvorena) čeka slanje, a očitavanje javi vrata zatvorena (prvo posle
		/// restarta klijenta javlja sva vrata; inače DoorWatcher javlja samo promene, npr. modul koji se ponovo javi) — isto
		/// kao poslednje stanje na serveru. Zatvorena vrata ne dokazuju popravku (zaglavljena vrata se tako i vide), pa greška
		/// mora ostati na čekanju, kao što ostaje kad je server već primi.
		/// </summary>
		[Fact]
		public async Task Unsent_closed_door_error_survives_closed_door_reading()
		{
			var updater = await StartAsync();
			try {
				await updater.SetFailedStatuses(true, [new DoorWatcher.DoorStatus(Door, DoorStatusCode.Closed, isInFailedState: true)], CancellationToken.None);
				Assert.True(IsFailed(PendingDoor()), "Preduslov: greška vrata je na čekanju.");

				await updater.UpdateStatuses(true, [new DoorWatcher.DoorStatus(Door, DoorStatusCode.Closed)], CancellationToken.None);

				var pending = PendingDoor();
				Assert.True(pending != null && IsFailed(pending), "Greška vrata koju server nije primio izgubljena je posle očitavanja zatvorenih vrata.");
			}
			finally {
				await updater.Stop();
			}
		}

		/// <summary>
		/// Kontrola: greška otvorenih vrata čeka slanje, a vrata se u međuvremenu zatvore. Server ionako zna da su zatvorena i
		/// ispravna, pa nema šta da se šalje — zapis sa čekanja se briše (isto kao automatsko brisanje greške otvorenih vrata
		/// koju je server primio).
		/// </summary>
		[Fact]
		public async Task Unsent_open_door_error_is_dropped_when_door_closes()
		{
			var updater = await StartAsync();
			try {
				await updater.SetFailedStatuses(true, [new DoorWatcher.DoorStatus(Door, DoorStatusCode.Open, isInFailedState: true)], CancellationToken.None);
				Assert.True(IsFailed(PendingDoor()), "Preduslov: greška vrata je na čekanju.");

				await updater.UpdateStatuses(true, [new DoorWatcher.DoorStatus(Door, DoorStatusCode.Closed)], CancellationToken.None);

				Assert.Null(PendingDoor());
			}
			finally {
				await updater.Stop();
			}
		}

		/// <summary>
		/// Server ne prima novi status paketomata (nedostupan), pa ga petlja šalje ponovo u svakom prolazu. Aplikaciji se
		/// promena javlja jednom: OnLockerStatusChanged(true) radi Reset, pa bi ga ponavljanje svake sekunde vraćalo na Start i
		/// prekidalo svaku sesiju dok server ne primi status (npr. oporavak paketomata tokom prekida interneta).
		/// </summary>
		[Theory]
		[InlineData(true)]
		[InlineData(false)]
		public async Task Locker_status_change_is_reported_once_while_server_is_unreachable(bool isLockerOperational)
		{
			var updater = await StartAsync(serverIsLockerOperational: !isLockerOperational);
			var reported = ReportTo(updater);
			try {
				await updater.UpdateStatuses(!isLockerOperational, [], CancellationToken.None);	// prvo očitavanje: isto kao na serveru
				await updater.UpdateStatuses(isLockerOperational, [], CancellationToken.None);
				await WaitForFailedLockerStatusSendsAsync(3);

				Assert.Equal(!isLockerOperational, updater.ServerIsLockerOperational);
				Assert.Equal([!isLockerOperational, isLockerOperational], reported);
			}
			finally {
				await updater.Stop();
			}
		}

		/// <summary>
		/// Server je nedostupan, a status paketomata se promeni pa vrati na ono što server zna (npr. kratak prekid kontrolera
		/// tokom prekida interneta). Aplikaciji se javlja i povratak: bez toga bi kiosk ostao „van funkcije“ (ili bi radio iako
		/// nije operativan) dok server ne primi status.
		/// </summary>
		[Theory]
		[InlineData(true)]
		[InlineData(false)]
		public async Task Locker_status_return_to_server_value_is_reported_while_server_is_unreachable(bool serverIsLockerOperational)
		{
			var updater = await StartAsync(serverIsLockerOperational);
			var reported = ReportTo(updater);
			try {
				await updater.UpdateStatuses(serverIsLockerOperational, [], CancellationToken.None);	// prvo očitavanje: isto kao na serveru
				await updater.UpdateStatuses(!serverIsLockerOperational, [], CancellationToken.None);
				await WaitForReportsAsync(reported, 2);

				await updater.UpdateStatuses(serverIsLockerOperational, [], CancellationToken.None);
				await WaitForReportsAsync(reported, 3);

				Assert.Equal([serverIsLockerOperational, !serverIsLockerOperational, serverIsLockerOperational], reported);
			}
			finally {
				await updater.Stop();
			}
		}

		/// <summary>
		/// Paketomat padne pa se oporavi pre nego što server primi odloženu prijavu „van funkcije“ (PrijavaGreskePaketomata).
		/// Zastarela prijava se više ne šalje: kad se veza vrati, poslednji poznati status (heartbeat) briše pending, pa bi
		/// odložena prijava ostavila server na false iako paketomat radi.
		/// </summary>
		[Fact]
		public async Task Deferred_offline_report_is_dropped_when_locker_recovers_before_sending()
		{
			var updater = await StartAsync(serverIsLockerOperational: true);
			var reported = ReportTo(updater);
			try {
				await updater.UpdateStatuses(true, [], CancellationToken.None);	// prvo očitavanje: isto kao na serveru
				await updater.UpdateStatuses(false, [], CancellationToken.None);
				await WaitForFailedLockerStatusSendsAsync(2);	// odložena prijava ne uspeva, server je nedostupan

				await updater.UpdateStatuses(true, [], CancellationToken.None);
				await WaitForLogAsync("TrySendDeferredLockerOfflineStatus: paketomat je ponovo operativan");
				int failedSends = FailedLockerStatusSends();
				await Task.Delay(300);	// nekoliko prolaza petlje (50 ms)

				Assert.Equal(failedSends, FailedLockerStatusSends());
				Assert.Equal([true, false, true], reported);
			}
			finally {
				await updater.Stop();
			}
		}

		/// <summary>
		/// Restart dok u ServerStatus.json čeka status paketomata koji server nije primio, a server je i dalje nedostupan.
		/// Petlja kreće iz konstruktora, pre nego što aplikacija dodeli handler (MainWindowViewModel: object initializer; Start
		/// dobija OnEntry još kasnije, u WireWorkflowMachine). Petlja zato aplikaciji ne javlja ništa — javljanje bez handlera
		/// bi bilo izgubljeno, a ponavljanja nema. Status javlja prvo očitavanje vrata (DoorWatcher kreće kad je aplikacija
		/// spremna), jednom.
		/// </summary>
		[Theory]
		[InlineData(true)]
		[InlineData(false)]
		public async Task Unsent_locker_status_after_restart_is_reported_once_by_first_reading(bool isLockerOperational)
		{
			var updater = await StartAsync(serverIsLockerOperational: !isLockerOperational, pendingIsLockerOperational: isLockerOperational);
			try {
				await WaitForFailedLockerStatusSendsAsync(3);	// petlja šalje status iz fajla, handler još nije dodeljen
				var reported = ReportTo(updater);
				await WaitForFailedLockerStatusSendsAsync(2);
				Assert.Empty(reported);

				await updater.UpdateStatuses(isLockerOperational, [], CancellationToken.None);
				await WaitForFailedLockerStatusSendsAsync(2);

				Assert.Equal(!isLockerOperational, updater.ServerIsLockerOperational);
				Assert.Equal([isLockerOperational], reported);
			}
			finally {
				await updater.Stop();
			}
		}

		/// <summary>
		/// Incident 99129 (3.11.2026): kontroler ne odgovara, a server ne poznaje poziciju (LockerBoxCount veći od broja
		/// pozicija na serveru) i odbija njeno stanje. Prijava kvara paketomata čeka da stanje svih vrata ode serveru — ta vrata
		/// nisu smela da je zadrže zauvek, jer bi server i dalje slao korisnike na paketomat koji ne radi.
		/// </summary>
		[Fact]
		public async Task Door_unknown_to_server_does_not_hold_back_locker_error_report()
		{
			const int unknownDoor = 8;
			using var server = new StubHttpServer { Handler = (requestLine, body) => ServerWithoutDoor(unknownDoor, requestLine, body) };
			var client = new TopPosServiceClient(
				new TopPosServiceConfiguration { BaseUrl = server.BaseUrl, PathPrefix = "api", RequestTimeout = 1000, DisableHeartbeats = true },
				"TEST", logger: null, CancellationToken.None);
			var updater = await StartAsync(client: client);	// server: paketomat u funkciji
			try {
				// Kontroler ne odgovara: sva vrata nepoznata, paketomat van funkcije.
				await updater.UpdateStatuses(false, Enumerable.Range(1, unknownDoor).Select(door => new DoorWatcher.DoorStatus(door, null)), CancellationToken.None);

				var deadline = DateTime.UtcNow.AddSeconds(10);
				while (!server.Requests.Any(r => r.RequestLine.Contains($"/{nameof(PrijavaGreskePaketomata)}/"))) {
					Assert.True(DateTime.UtcNow < deadline, "Prijava kvara paketomata nije stigla na server.");
					await Task.Delay(20);
				}
				await Task.Delay(300);	// nekoliko prolaza petlje (50 ms)

				Assert.Single(server.Requests, r => r.RequestLine.Contains($"/{nameof(PrijavaGreskePozicije)}/") && DoorNumberOf(r.Body) == unknownDoor);
				Assert.False(updater.ServerIsLockerOperational);
			}
			finally {
				await updater.Stop();
			}
		}

		/// <summary>Server koji ne poznaje poziciju <paramref name="unknownDoor"/> (odgovori kao de-test, 3.11.2026), a ostalo prihvata.</summary>
		private static (HttpStatusCode, string) ServerWithoutDoor(int unknownDoor, string requestLine, string body)
		{
			int? door = DoorNumberOf(body);
			if (requestLine.Contains($"/{nameof(PrijavaGreskePozicije)}/")) {
				return door == unknownDoor ? (HttpStatusCode.BadRequest, ArgumentError($"Na paketomatu \\\"TEST\\\" ne postoji broj pozicije \\\"{door}\\\""))
											: (HttpStatusCode.OK, $$"""{"brojPozicije":{{door}}}""");
			}
			if (requestLine.Contains($"/{nameof(PonistavanjeGreskePozicije)}")) {
				return door == unknownDoor ? (HttpStatusCode.BadRequest, ArgumentError($"Na paketomatu \\\"TEST\\\" ne postoji navedeni broj pozicije \\\"{door}\\\""))
											: (HttpStatusCode.OK, $$"""{"brojPozicije":{{door}}}""");
			}
			if (requestLine.Contains($"/{nameof(PrijavaGreskePaketomata)}/") || requestLine.Contains($"/{nameof(PonistavanjeGreskePaketomata)}")) {
				return (HttpStatusCode.OK, """{"sifraBenzinskeStanice":"TEST"}""");
			}
			return (HttpStatusCode.NotFound, "{}");

			static string ArgumentError(string message)
				=> $$"""{"responseStatus":{"errorCode":"ArgumentException","message":"{{message}}","errors":[{"errorCode":"ArgumentException","fieldName":"BrojPozicije","message":"{{message}}"}]},"brojPozicije":0}""";
		}

		private static int? DoorNumberOf(string body)
			=> Regex.Match(body, "\"brojPozicije\"\\s*:\\s*(\\d+)", RegexOptions.IgnoreCase) is { Success: true } match ? int.Parse(match.Groups[1].Value) : null;

		private async Task<LockerStatusUpdater> StartAsync(bool serverIsLockerOperational = true, bool? pendingIsLockerOperational = null, TopPosServiceClient? client = null)
		{
			await File.WriteAllTextAsync(StatusFile,
				$$"""{"PendingIsLockerOperational":{{Json(pendingIsLockerOperational)}},"PendingDoorStatuses":[],"ServerIsLockerOperational":{{Json(serverIsLockerOperational)}},"ServerDoorStatuses":[{"DoorNumber":{{Door}},"Status":0,"IsInFailedState":false}]}""");
			return new LockerStatusUpdater(new LockerStatusUpdaterConfiguration { ServerStatusFilename = StatusFile, ProcessingDelayInMilliseconds = 50 },
											client ?? _serviceClient, numberOfDoors: 8, _log);

			static string Json(bool? value) => value switch { true => "true", false => "false", null => "null" };
		}

		/// <summary>Statusi paketomata koje updater javlja aplikaciji (LockerStatusChanged), redom.</summary>
		private static ConcurrentQueue<bool> ReportTo(LockerStatusUpdater updater)
		{
			var reported = new ConcurrentQueue<bool>();
			updater.LockerStatusChanged = value => { reported.Enqueue(value); return ValueTask.CompletedTask; };
			return reported;
		}

		/// <summary>Čeka još <paramref name="count"/> neuspelih slanja statusa paketomata (server je nedostupan) — prolaza petlje.</summary>
		private async Task WaitForFailedLockerStatusSendsAsync(int count)
		{
			int target = FailedLockerStatusSends() + count;
			var deadline = DateTime.UtcNow.AddSeconds(10);
			while (FailedLockerStatusSends() < target) {
				Assert.True(DateTime.UtcNow < deadline, $"Petlja nije {count} puta pokušala da pošalje status paketomata.");
				await Task.Delay(20);
			}
		}

		/// <summary>Čeka da aplikaciji bude javljeno ukupno <paramref name="count"/> statusa paketomata.</summary>
		private static async Task WaitForReportsAsync(ConcurrentQueue<bool> reported, int count)
		{
			var deadline = DateTime.UtcNow.AddSeconds(10);
			while (reported.Count < count) {
				Assert.True(DateTime.UtcNow < deadline, $"Aplikaciji nije javljeno {count} statusa paketomata.");
				await Task.Delay(20);
			}
		}

		private async Task WaitForLogAsync(string prefix)
		{
			var deadline = DateTime.UtcNow.AddSeconds(10);
			while (!_log.Messages.Any(m => m.StartsWith(prefix))) {
				Assert.True(DateTime.UtcNow < deadline, $"U logu nema poruke „{prefix}“.");
				await Task.Delay(20);
			}
		}

		private int FailedLockerStatusSends() => _log.Messages.Count(m => m.StartsWith("SendLockerStatus:"));

		/// <summary>Zapis vrata sa čekanja iz ServerStatus.json (upis je završen kad Update/SetFailedStatuses vrate).</summary>
		private JsonNode? PendingDoor()
		{
			using var stream = new FileStream(StatusFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			return JsonNode.Parse(stream)?["PendingDoorStatuses"]?.AsArray().FirstOrDefault(d => d?["DoorNumber"]?.GetValue<int>() == Door);
		}

		private static bool IsFailed(JsonNode? door) => door?["IsInFailedState"]?.GetValue<bool>() == true;

		private sealed class ListLogger : ILogger
		{
			private readonly ConcurrentQueue<string> _messages = new();

			public IEnumerable<string> Messages => _messages;

			public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
			public bool IsEnabled(LogLevel logLevel) => true;
			public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
				=> _messages.Enqueue(formatter(state, exception));
		}
	}
}
