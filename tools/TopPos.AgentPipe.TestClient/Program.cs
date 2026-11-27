using System.Diagnostics;
using System.Text;
using System.Text.Json;
using TopPos.Agent.Contracts;

namespace TopPos.AgentPipe.TestClient
{
	/// <summary>
	/// Test klijent kanala ka kiosku: povezuje se na pipe, ispisuje ko je server (PID, exe, vlasnik), šalje ClientHello i
	/// jednu komandu ili prati pretplatu. Služi za ručnu proveru na VM-u pod različitim nalozima:
	/// <list type="bullet">
	/// <item>LocalSystem (npr. <c>psexec -s</c>): očekuje se Hello i status;</item>
	/// <item>kiosk nalog (msi): veza se zatvara pre Hello (provera identiteta);</item>
	/// <item>treći nalog: Windows odbija otvaranje (DACL).</item>
	/// </list>
	/// v4: na Linuxu je kanal Unix socket (<c>--socket</c>, ugovor „Transport na Linuxu“): root (npr. <c>sudo</c>) dobija Hello,
	/// kiosk nalog dobija zatvorenu vezu pre Hello, ostali ne mogu ni da otvore socket (folder <c>0700</c>). Sa
	/// <c>--status-lock</c> klijent pre ClientHello proverava server kao Agent (tačka 4: PID, <c>boot_id</c> i <c>starttime</c> iz
	/// izjave, uz <c>--expect-uid</c> i <c>--expect-exe</c> i nalog i izvršni fajl) i bez poklapanja ne šalje ništa.
	/// Sa <c>--squat</c> radi kao lažni server koji zauzme ime pipe-a pre kioska (Linux: drži <c>agent.v1.lock</c>). Sa <c>--shutdown</c> šalje komandu
	/// ShutdownApplication (kao Agent: pretplata, pa zahtev sa instancom iz Hello) i meri vreme do izlaska procesa kioska.
	/// Komande servisne sesije (<c>--remote-service-begin</c>, <c>--open-doors</c>, <c>--remote-service-end</c>,
	/// <c>--command-result</c>) i preuzimanja (<c>--recipient-pickup</c>) ispisuju odgovor i sažetak rezultata; sa <c>--watch</c>
	/// klijent prati status i posle svake promene <c>operationSequence</c> pita <c>GetCommandResult</c>, kao Agent.
	/// Izlazni kod: 0 uspeh, 2 pipe nije otvoren (ili ime nije zauzeto), 3 server je zatvorio vezu pre Hello, 4 greška protokola,
	/// 5 komanda odbijena, 6 proces kioska nije izašao u roku posle Completed, 7 server nije prošao proveru (<c>--status-lock</c>).
	/// </summary>
	internal static class Program
	{
		private static async Task<int> Main(string[] args)
		{
			Console.OutputEncoding = Encoding.UTF8;     // poruke kioska i ispis su na srpskom
			var options = Options.Parse(args);
			if (options == null) {
				Console.WriteLine("""
					Upotreba: TopPos.AgentPipe.TestClient [--pipe <ime> | --socket <putanja>] [--command <komanda>] [--subscribe <sekundi>] [--locker <id>]
					          TopPos.AgentPipe.TestClient [--pipe <ime> | --socket <putanja>] [--locker <id>] --shutdown <Restart|Stop|Install|Update>
					          TopPos.AgentPipe.TestClient [--pipe <ime> | --socket <putanja>] --squat <sekundi>
					          TopPos.AgentPipe.TestClient [--pipe <ime> | --socket <putanja>] --remote-service-begin <kod> --layout <csv> [--idle <sekundi>] [--watch <sekundi>]
					          TopPos.AgentPipe.TestClient [--pipe <ime> | --socket <putanja>] --open-doors <sessionId> <vrata|all> [--watch <sekundi>]
					          TopPos.AgentPipe.TestClient [--pipe <ime> | --socket <putanja>] --remote-service-end <sessionId>
					          TopPos.AgentPipe.TestClient [--pipe <ime> | --socket <putanja>] --command-result <requestId> [--after <redni broj>]
					          TopPos.AgentPipe.TestClient [--pipe <ime> | --socket <putanja>] --recipient-pickup <telefon> <šifra> <kod prisustva> [--watch <sekundi>]
					  --pipe       ime pipe-a, Windows (podrazumevano TopPos.Kiosk.Agent.v1)
					  --socket     putanja Unix socket-a, Linux (podrazumevano /run/toppos-kiosk/agent.v1.sock)
					  --status-lock izjava kioska (status.lock): pre ClientHello proveri server kao Agent (Linux)
					  --expect-uid  uz --status-lock: uid kiosk naloga (podrazumevano nalog toppos)
					  --expect-exe  uz --status-lock: izvršni fajl kioska (podrazumevano /opt/toppos-kiosk/current/TopPos.Kiosk)
					  --command    GetStatus (podrazumevano), OpenDoor, GetCommandResult ili bilo šta drugo
					  --subscribe  pretplata na status i ispis događaja zadati broj sekundi
					  --locker     LockerId u zahtevu (podrazumevano onaj iz Hello)
					  --shutdown   ShutdownApplication sa zadatim Purpose; ispisuje odgovor, događaje, kraj veze i vreme do izlaska procesa
					  --squat      lažni server: zauzme ime pipe-a (Linux: lock pored socket-a) zadati broj sekundi
					  --remote-service-begin  BeginRemoteService sa kodom prisustva; sessionId je RequestId iz ispisa
					  --layout     raspored u CSV formatu Technician-a (DoorNumber;Size;Column;Row, zaglavlje se preskače)
					  --idle       rok neaktivnosti posle prijave servisera (podrazumevano 600 s)
					  --open-doors OpenDoors: vrata odvojena zarezom (npr. 1,2,9) ili all
					  --remote-service-end    EndRemoteService (serviser je u portalu potvrdio da je zatvorio vrata)
					  --command-result        GetCommandResult, bez ClientInstanceId i roka (radi i posle restarta kioska)
					  --after      GetCommandResult: samo upisi posle ovog rednog broja
					  --recipient-pickup      RecipientPickup: podrška preuzima paket; kiosk otvara vrata kad korisnik unese kod prisustva
					  --watch      posle komande prati status zadati broj sekundi i čita GetCommandResult na svaku promenu
					  --request-id RequestId komande servisne sesije ili preuzimanja (podrazumevano nov GUID); za Begin je to i sessionId
					  --result-file odgovor na komandu servisne sesije ili preuzimanja kao JSON u zadati fajl (E2E)
					""");
				return 1;
			}

			var endpoint = options.Endpoint;
			Console.WriteLine($"Proces: {endpoint.DescribeClient()}, PID {Environment.ProcessId}");

			if (options.SquatSeconds > 0) {
				return await endpoint.SquatAsync(options.SquatSeconds);
			}

			await using var pipe = await endpoint.ConnectAsync();
			if (pipe == null) {
				return 2;
			}
			var serverProcessId = endpoint.DescribeServer();
			// v4: Agent pre ičega osim ClientHello proverava server (ugovor tačka 4); ovde i pre ClientHello.
			if (options.StatusLock != null && OperatingSystem.IsLinux() && endpoint is LinuxSocketEndpoint socket) {
				var (expected, errors) = socket.VerifyServer(options.StatusLock, options.ExpectedUid, options.ExpectedExecutable);
				if (errors.Count > 0) {
					Console.WriteLine($"Server NIJE proveren: {string.Join("; ", errors)}.");
					return 7;
				}
				Console.WriteLine($"Server proveren po izjavi {options.StatusLock} ({expected}).");
			}

			bool helloReceived = false;
			try {
				await Send(pipe, new ClientHello { AgentVersion = "TestClient" });
				var helloBody = await Read(pipe, TimeSpan.FromSeconds(10));
				if (helloBody == null) {
					Console.WriteLine("Server je zatvorio vezu pre Hello (identitet klijenta nije prihvaćen).");
					return 3;
				}
				Print("Hello", helloBody);
				helloReceived = true;
				var hello = AgentJson.Deserialize<ServerHello>(helloBody);
				var lockerId = options.LockerId ?? hello.LockerId;
				if (options.ShutdownPurpose != null) {
					return await Shutdown(pipe, hello, lockerId, options.ShutdownPurpose, serverProcessId);
				}
				if (options.RemoteService != null) {
					return await RemoteService(pipe, hello, lockerId, options.RemoteService);
				}

				if (options.SubscribeSeconds <= 0) {
					// v4: izlazni kod nosi ishod komande (v2: 0 i bez odgovora) — 4 bez odgovora, 5 odbijena.
					var single = NewRequest(hello, lockerId, options.Command);
					await Send(pipe, single);
					var answer = await ReadUntilResponse(pipe, single.RequestId);
					if (answer == null) {
						return 4;
					}
					if (answer.ResultCode != AgentResultCodes.Completed) {
						Console.WriteLine($"Komanda odbijena: {answer.ResultCode} — {answer.Message}");
						return 5;
					}
					return 0;
				}
				await Send(pipe, NewRequest(hello, lockerId, AgentCommands.SubscribeStatus));
				var until = DateTime.UtcNow.AddSeconds(options.SubscribeSeconds);
				while (until - DateTime.UtcNow is var remaining && remaining > TimeSpan.Zero) {
					var body = await Read(pipe, remaining);
					if (body == null) {
						Console.WriteLine("(kraj praćenja ili veza zatvorena)");
						break;
					}
					Print(AgentJson.ReadEnvelope(body).Type, body);
				}
				return 0;
			}
			catch (AgentProtocolException ex) {
				Console.WriteLine($"Greška protokola: {ex.Message}");
				return 4;
			}
			catch (IOException ex) {
				// v4: kod 3 samo pre Hello (identitet); posle Hello prekid znači da odgovor nije stigao (4).
				Console.WriteLine($"Veza prekinuta: {ex.Message}");
				return helloReceived ? 4 : 3;
			}
		}

		// ShutdownApplication kao Agent: pretplata, zahtev (rok 30 s), ispis do kraja veze, pa čekanje izlaska procesa kioska.
		// Proces se otvara pre slanja, da bi se izlazak video i kad se desi pre čekanja.
		private static async Task<int> Shutdown(Stream pipe, ServerHello hello, string lockerId, string purpose, int? serverProcessId)
		{
			using var server = serverProcessId is { } pid ? TryOpenProcess(pid) : null;

			var subscribe = NewRequest(hello, lockerId, AgentCommands.SubscribeStatus);
			await Send(pipe, subscribe);
			if (await ReadUntilResponse(pipe, subscribe.RequestId) == null) {
				return 4;
			}

			var request = NewRequest(hello, lockerId, AgentCommands.ShutdownApplication);
			request.Payload = JsonSerializer.SerializeToElement(new ShutdownApplicationPayload { Purpose = purpose }, AgentJson.Options);
			request.Reason = "TopPos.AgentPipe.TestClient --shutdown";
			await Send(pipe, request);
			var response = await ReadUntilResponse(pipe, request.RequestId);
			if (response == null) {
				return 4;
			}
			var completedAt = Stopwatch.StartNew();
			if (response.ResultCode != AgentResultCodes.Completed) {
				Console.WriteLine($"Komanda odbijena: {response.ResultCode} — {response.Message}");
				return 5;
			}

			// Posle Completed: ostatak poruka do kraja veze. v4: jedan rok od 60 s za poruke i izlazak procesa — heartbeat ga ne
			// produžava (v2: 30 s od svake poruke, pa proces koji ne izlazi, a šalje heartbeat, nije dao kod 6).
			var deadline = TimeSpan.FromSeconds(60);
			using var exitWait = new CancellationTokenSource(deadline);
			try {
				while (deadline - completedAt.Elapsed is var remaining && remaining > TimeSpan.Zero && await Read(pipe, remaining) is { } body) {
					Print(AgentJson.ReadEnvelope(body).Type, body);
				}
			}
			catch (IOException) { }
			Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} Veza zatvorena {completedAt.ElapsedMilliseconds} ms posle Completed.");

			if (server == null) {
				Console.WriteLine("Proces kioska nije dostupan; izlazak se ne meri.");
				return 0;
			}
			try {
				await server.WaitForExitAsync(exitWait.Token);
			}
			catch (OperationCanceledException) {
				Console.WriteLine($"Proces kioska (PID {server.Id}) NIJE izašao ni 60 s posle Completed.");
				return 6;
			}
			Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} Proces kioska (PID {server.Id}) izašao {completedAt.ElapsedMilliseconds} ms posle Completed (izlazni kod {ExitCodeOf(server)}).");
			return 0;
		}

		// Komanda servisne sesije ili preuzimanja: (pretplata ako se prati), zahtev, ispis rezultata, pa praćenje operacije.
		private static async Task<int> RemoteService(Stream pipe, ServerHello hello, string lockerId, RemoteServiceOptions options)
		{
			if (options.WatchSeconds > 0) {
				var subscribe = NewRequest(hello, lockerId, AgentCommands.SubscribeStatus);
				await Send(pipe, subscribe);
				if (await ReadUntilResponse(pipe, subscribe.RequestId) == null) {
					return 4;
				}
			}

			AgentRequest request;
			string? tracked = null;    // sesija čiji se rezultat prati (status nosi sessionId i za otvaranje u njoj)
			switch (options.Command) {
				case AgentCommands.BeginRemoteService:
					request = NewRequest(hello, lockerId, options.Command);
					request.Payload = JsonSerializer.SerializeToElement(new BeginRemoteServicePayload {
											IdleTimeoutSeconds = options.IdleSeconds,
											Layout = options.Layout,
											PresenceCode = options.Argument,
										}, AgentJson.Options);
					break;
				case AgentCommands.OpenDoors:
					request = NewRequest(hello, lockerId, options.Command);
					request.Payload = JsonSerializer.SerializeToElement(new OpenDoorsPayload {
											SessionId = options.Argument,
											All = options.Doors == null,
											DoorNumbers = options.Doors,
										}, AgentJson.Options);
					tracked = options.Argument;
					break;
				case AgentCommands.EndRemoteService:
					request = NewRequest(hello, lockerId, options.Command);
					request.Payload = JsonSerializer.SerializeToElement(new EndRemoteServicePayload { SessionId = options.Argument }, AgentJson.Options);
					break;
				case AgentCommands.RecipientPickup:
					request = NewRequest(hello, lockerId, options.Command);
					request.Payload = JsonSerializer.SerializeToElement(options.Pickup, AgentJson.Options);
					break;
				default:
					request = NewQuery(lockerId, options.Argument, options.AfterSequence);
					break;
			}
			if (options.RequestId != null && options.Command != AgentCommands.GetCommandResult) {
				request.RequestId = options.RequestId;
			}
			// v4: Begin i preuzimanje se prate po konačnom RequestId-u (v2 ga je uzimao pre --request-id).
			if (options.Command is AgentCommands.BeginRemoteService or AgentCommands.RecipientPickup) {
				tracked = request.RequestId;
			}
			request.Reason ??= $"TopPos.AgentPipe.TestClient {options.Command}";
			Console.WriteLine($"{options.Command} RequestId={request.RequestId}");
			await Send(pipe, request);
			var response = await ReadUntilResponse(pipe, request.RequestId);
			if (response == null) {
				return 4;
			}
			if (options.ResultFile != null) {
				await File.WriteAllBytesAsync(options.ResultFile, AgentJson.Serialize(response));
			}
			PrintResult(response);
			if (response.ResultCode is not (AgentResultCodes.Completed or AgentResultCodes.InProgress)) {
				Console.WriteLine($"Komanda odbijena: {response.ResultCode} — {response.Message}");
				return 5;
			}
			if (options.WatchSeconds > 0 && tracked != null) {
				await Watch(pipe, lockerId, tracked, TimeSpan.FromSeconds(options.WatchSeconds));
			}
			return 0;
		}

		// Kao Agent: promena operationSequence → GetCommandResult sa upisima posle poslednjeg viđenog. Statusni događaji se
		// spajaju, pa se posle svakog odgovora proverava da li je u međuvremenu stigao veći broj; kad operacija nestane iz
		// statusa, čita se još jednom (konačan rezultat). Rezultat preuzimanja nema redne brojeve upisa: odgovor pokriva broj
		// viđen pri slanju upita.
		private static async Task Watch(Stream pipe, string lockerId, string operationId, TimeSpan duration)
		{
			Console.WriteLine($"Praćenje operacije {operationId} {duration.TotalSeconds:0} s.");
			var until = DateTime.UtcNow + duration;
			int seenSequence = 0, fetchedSequence = 0, lastEntry = 0, queriedSequence = 0;
			string? pending = null;
			bool operationSeen = false, ended = false;
			while (DateTime.UtcNow < until) {
				var body = await Read(pipe, until - DateTime.UtcNow);
				if (body == null) {
					break;
				}
				var type = AgentJson.ReadEnvelope(body).Type;
				if (type == AgentMessageTypes.Event) {
					if (AgentJson.Deserialize<AgentEvent>(body).Status is not { } status) {
						continue;
					}
					Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} Status: {status.WorkflowState}, Busy={status.Busy}, Role={status.Role}, " +
									  $"operacija={status.ActiveOperationId ?? "-"} {status.Activity}, operationSequence={status.OperationSequence?.ToString() ?? "-"}");
					if (status.ActiveOperationId == operationId) {
						operationSeen = true;
						seenSequence = Math.Max(seenSequence, status.OperationSequence ?? 0);
					}
					else if (operationSeen && !ended) {
						ended = true;
						seenSequence = int.MaxValue;
					}
				}
				else if (type == AgentMessageTypes.Response) {
					var response = AgentJson.Deserialize<AgentResponse>(body);
					if (response.RequestId != pending) {
						Print(type, body);
						continue;
					}
					pending = null;
					PrintResult(response);
					var (last, entry, hasMore) = SequencesOf(response);
					lastEntry = Math.Max(lastEntry, entry);
					fetchedSequence = hasMore ? lastEntry : Math.Max(fetchedSequence, last ?? queriedSequence);
					// Preuzimanje: konačan je tek odgovor na upit poslat posle kraja.
					if (ended && !hasMore && (last != null || fetchedSequence == int.MaxValue)) {
						Console.WriteLine("Operacija je završena.");
						return;
					}
				}
				if (pending == null && seenSequence > fetchedSequence) {
					var query = NewQuery(lockerId, operationId, lastEntry);
					pending = query.RequestId;
					queriedSequence = seenSequence;
					await Send(pipe, query);
				}
			}
			Console.WriteLine("(kraj praćenja ili veza zatvorena)");
		}

		// Redni brojevi iz rezultata: poslednji upis operacije (null kad ga rezultat nema), poslednji vraćen upis, ima li još.
		private static (int? Last, int Entry, bool HasMore) SequencesOf(AgentResponse response)
		{
			if (response.Result is not { } result) {
				return (0, 0, false);
			}
			if (IsRecipientPickup(result)) {
				return (null, 0, false);
			}
			if (result.TryGetProperty("phase", out _)) {
				var session = AgentJson.ReadResult<RemoteServiceResult>(response)!;
				var entry = session.Openings.Select(o => o.Sequence).Concat(session.ModuleErrors.Select(e => e.Sequence)).DefaultIfEmpty(0).Max();
				return (session.LastSequence, entry, session.HasMore);
			}
			var open = AgentJson.ReadResult<OpenDoorsResult>(response)!;
			var last = open.Openings.Select(o => o.Sequence).DefaultIfEmpty(0).Max();
			return (last, last, false);
		}

		// Rezultat preuzimanja ima fazu kao servisna sesija, ali bez sessionId; otvaranje ima sessionId bez faze.
		private static bool IsRecipientPickup(JsonElement result)
			=> result.TryGetProperty("phase", out _) && !result.TryGetProperty("sessionId", out _);

		// Sažetak rezultata servisne sesije, otvaranja ili preuzimanja (sirova poruka je već ispisana).
		private static void PrintResult(AgentResponse response)
		{
			Console.WriteLine($"  → {response.ResultCode}: {response.Message}");
			if (response.Result is not { } result) {
				return;
			}
			if (IsRecipientPickup(result)) {
				var pickup = AgentJson.ReadResult<RecipientPickupResult>(response)!;
				Console.WriteLine($"    preuzimanje: faza {pickup.Phase}, ishod {pickup.Outcome ?? "-"}, vrata {pickup.DoorNumber?.ToString() ?? "-"}, " +
								  $"pogrešnih kodova {pickup.FailedCodeAttempts?.ToString() ?? "-"}, potvrda {pickup.Confirmation ?? "-"}");
				if (pickup.Amount != null || pickup.ServerMessage != null) {
					Console.WriteLine($"    iznos {pickup.Amount?.ToString() ?? "-"}, poruka servera {pickup.ServerMessage ?? "-"}");
				}
			}
			else if (result.TryGetProperty("phase", out _)) {
				var session = AgentJson.ReadResult<RemoteServiceResult>(response)!;
				Console.WriteLine($"    sesija {session.SessionId}: faza {session.Phase}, kraj {session.EndMode ?? "-"}, pogrešnih kodova {session.FailedAuthAttempts}, " +
								  $"LastSequence {session.LastSequence}, HasMore {session.HasMore}");
				if (session.IgnoredDoors.Count > 0 || session.UnavailableModulesAtStart.Count > 0) {
					Console.WriteLine($"    izbačena vrata [{string.Join(", ", session.IgnoredDoors)}], nedostupni moduli pri početku [{string.Join(", ", session.UnavailableModulesAtStart)}]");
				}
				PrintOpenings(session.Openings);
				foreach (var error in session.ModuleErrors) {
					Console.WriteLine($"    #{error.Sequence} modul {error.ModuleNumber} ne odgovara ({error.AtUtc.ToLocalTime():HH:mm:ss})");
				}
				if (session.DoorsNotClosed != null) {
					Console.WriteLine($"    vrata bez „zatvoreno“ na kraju: [{string.Join(", ", session.DoorsNotClosed)}]");
				}
			}
			else {
				var open = AgentJson.ReadResult<OpenDoorsResult>(response)!;
				Console.WriteLine($"    otvaranje u sesiji {open.SessionId}: završeno {open.Finished}");
				PrintOpenings(open.Openings);
			}
		}

		private static void PrintOpenings(IEnumerable<DoorOpening> openings)
		{
			foreach (var opening in openings) {
				Console.WriteLine($"    #{opening.Sequence} vrata {opening.DoorNumber} ({opening.Source}) → {opening.Outcome} ({opening.AtUtc.ToLocalTime():HH:mm:ss})");
			}
		}

		// GetCommandResult samo čita: bez ClientInstanceId i roka, pa radi i posle restarta kioska.
		private static AgentRequest NewQuery(string lockerId, string requestId, int? afterSequence) => new() {
			RequestId = Guid.NewGuid().ToString("N"),
			LockerId = lockerId,
			Command = AgentCommands.GetCommandResult,
			Payload = JsonSerializer.SerializeToElement(new GetCommandResultPayload { RequestId = requestId, AfterSequence = afterSequence }, AgentJson.Options),
		};

		// Rok iz jednog očitavanja sata: dva očitavanja bi dala rok malo duži od dozvoljenog, a kiosk ga strogo odbija.
		private static AgentRequest NewRequest(ServerHello hello, string lockerId, string command)
		{
			var now = DateTimeOffset.UtcNow;
			return new() {
				RequestId = Guid.NewGuid().ToString("N"),
				LockerId = lockerId,
				ClientInstanceId = hello.ClientInstanceId,
				Command = command,
				IssuedAtUtc = now,
				ExpiresAtUtc = now.AddSeconds(AgentProtocol.MaxRequestTtlSeconds),
			};
		}

		// Ispisuje poruke do odgovora na zahtev; null ako veza pukne ili odgovor ne stigne u roku. v4: jedan rok od 10 s za ceo
		// zahtev — druge poruke (događaji, heartbeat) ga ne produžavaju.
		private static async Task<AgentResponse?> ReadUntilResponse(Stream pipe, string requestId)
		{
			var timeout = TimeSpan.FromSeconds(10);
			var waited = Stopwatch.StartNew();
			while (timeout - waited.Elapsed is var remaining && remaining > TimeSpan.Zero && await Read(pipe, remaining) is { } body) {
				var type = AgentJson.ReadEnvelope(body).Type;
				Print(type, body);
				if (type == AgentMessageTypes.Response && AgentJson.Deserialize<AgentResponse>(body) is { } response && response.RequestId == requestId) {
					return response;
				}
			}
			Console.WriteLine("Nema odgovora u roku ili je veza zatvorena.");
			return null;
		}

		// v4: na Linuxu izlazni kod procesa koji nije dete ovog procesa nije poznat.
		private static string ExitCodeOf(Process process)
		{
			try {
				return process.ExitCode.ToString();
			}
			catch (InvalidOperationException) {
				return "nepoznat";
			}
		}

		private static Process? TryOpenProcess(int processId)
		{
			try {
				return Process.GetProcessById(processId);
			}
			catch (Exception ex) {
				Console.WriteLine($"Proces {processId} nije dostupan: {ex.Message}");
				return null;
			}
		}

		private static Task Send<T>(Stream pipe, T message) where T : AgentMessage
			=> AgentFrameCodec.WriteFrameAsync(pipe, AgentJson.Serialize(message), AgentProtocol.MaxMessageBytes, CancellationToken.None);

		// Poruka u roku, ili null (rok ili zatvorena veza).
		private static async Task<byte[]?> Read(Stream pipe, TimeSpan timeout)
		{
			using var cts = new CancellationTokenSource(timeout);
			try {
				return await AgentFrameCodec.ReadFrameAsync(pipe, AgentProtocol.MaxMessageBytes, cts.Token);
			}
			catch (OperationCanceledException) {
				return null;
			}
		}

		private static void Print(string type, byte[] body)
			=> Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {type}: {Encoding.UTF8.GetString(body)}");

		/// <summary>
		/// Komanda servisne sesije ili preuzimanja; <see cref="Argument"/> je kod (Begin), sessionId (OpenDoors, End), requestId
		/// (GetCommandResult) ili telefon (RecipientPickup).
		/// </summary>
		private sealed record RemoteServiceOptions(string Command, string Argument)
		{
			public List<LayoutBox>? Layout { get; set; }
			/// <summary>Parametri RecipientPickup.</summary>
			public RecipientPickupPayload? Pickup { get; set; }
			public int IdleSeconds { get; set; } = 600;
			/// <summary>null = sva vrata.</summary>
			public List<int>? Doors { get; set; }
			public int? AfterSequence { get; set; }
			public int WatchSeconds { get; set; }
			/// <summary>RequestId komande (Begin, OpenDoors, End, RecipientPickup); null = nov GUID.</summary>
			public string? RequestId { get; set; }
			/// <summary>Fajl u koji se upisuje odgovor na komandu (JSON poruke <see cref="AgentResponse"/>).</summary>
			public string? ResultFile { get; set; }
		}

		private sealed record Options(ChannelEndpoint Endpoint, string Command, int SubscribeSeconds, string? LockerId, int SquatSeconds, string? ShutdownPurpose,
									  RemoteServiceOptions? RemoteService)
		{
			/// <summary>v4: izjava kioska za proveru servera (Linux).</summary>
			public string? StatusLock { get; init; }

			public uint? ExpectedUid { get; init; }

			public string? ExpectedExecutable { get; init; }

			public static Options? Parse(string[] args)
			{
				string command = AgentCommands.GetStatus;
				string? pipe = null, socket = null, statusLock = null, expectedExecutable = null;
				string? locker = null, shutdown = null, layoutFile = null, requestId = null, resultFile = null;
				int subscribe = 0, squat = 0, idle = 600, watch = 0, after = -1;
				uint? expectedUid = null;
				RemoteServiceOptions? remote = null;
				List<int>? doors = null;
				for (int i = 0; i < args.Length; i++) {
					string? next = i + 1 < args.Length ? args[i + 1] : null;
					string? second = i + 2 < args.Length ? args[i + 2] : null;
					string? third = i + 3 < args.Length ? args[i + 3] : null;
					switch (args[i]) {
						case "--pipe" when next != null: pipe = next; i++; break;
						case "--socket" when next != null: socket = next; i++; break;
						case "--status-lock" when next != null: statusLock = next; i++; break;
						case "--expect-uid" when next != null && uint.TryParse(next, out var uid): expectedUid = uid; i++; break;
						case "--expect-exe" when next != null: expectedExecutable = next; i++; break;
						case "--command" when next != null: command = next; i++; break;
						case "--locker" when next != null: locker = next; i++; break;
						case "--subscribe" when next != null && int.TryParse(next, out subscribe): i++; break;
						case "--squat" when next != null && int.TryParse(next, out squat): i++; break;
						// Purpose se šalje kakav je zadat; nepoznatu vrednost odbija kiosk (InvalidRequest).
						case "--shutdown" when next != null: shutdown = next; i++; break;
						// Kod, vrata i raspored se šalju kakvi su zadati; proverava ih kiosk.
						case "--remote-service-begin" when next != null: remote = new(AgentCommands.BeginRemoteService, next); i++; break;
						case "--open-doors" when next != null && second != null:
							remote = new(AgentCommands.OpenDoors, next);
							doors = ParseDoors(second);
							if (doors == null && !second.Equals("all", StringComparison.OrdinalIgnoreCase)) {
								return null;
							}
							i += 2;
							break;
						case "--remote-service-end" when next != null: remote = new(AgentCommands.EndRemoteService, next); i++; break;
						case "--command-result" when next != null: remote = new(AgentCommands.GetCommandResult, next); i++; break;
						case "--recipient-pickup" when next != null && second != null && third != null:
							remote = new(AgentCommands.RecipientPickup, next) { Pickup = new() { Phone = next, Code = second, PresenceCode = third } };
							i += 3;
							break;
						case "--layout" when next != null: layoutFile = next; i++; break;
						case "--idle" when next != null && int.TryParse(next, out idle): i++; break;
						case "--after" when next != null && int.TryParse(next, out after): i++; break;
						case "--watch" when next != null && int.TryParse(next, out watch): i++; break;
						case "--request-id" when next != null: requestId = next; i++; break;
						case "--result-file" when next != null: resultFile = next; i++; break;
						default: return null;
					}
				}
				if (remote != null) {
					remote.IdleSeconds = idle;
					remote.Doors = doors;
					remote.AfterSequence = after >= 0 ? after : null;
					remote.WatchSeconds = watch;
					remote.RequestId = requestId;
					remote.ResultFile = resultFile;
					if (remote.Command == AgentCommands.BeginRemoteService) {
						if (layoutFile == null) {
							Console.WriteLine("--remote-service-begin traži --layout <csv>.");
							return null;
						}
						remote.Layout = ReadLayout(layoutFile);
						if (remote.Layout == null) {
							return null;
						}
					}
				}
				var endpoint = CreateEndpoint(pipe, socket);
				if (endpoint == null) {
					return null;
				}
				// Provera servera postoji samo na Linuxu; na Windows-u bi tražena provera prećutno izostala.
				if (statusLock != null && endpoint is not LinuxSocketEndpoint) {
					Console.WriteLine("--status-lock postoji samo na Linuxu.");
					return null;
				}
				if ((expectedUid != null || expectedExecutable != null) && statusLock == null) {
					Console.WriteLine("--expect-uid i --expect-exe traže --status-lock.");
					return null;
				}
				return new Options(endpoint, command, subscribe, locker, squat, shutdown, remote) {
					StatusLock = statusLock,
					ExpectedUid = expectedUid,
					ExpectedExecutable = expectedExecutable,
				};
			}

			// v4: transport platforme kioska — pipe na Windows-u, socket na Linuxu; drugi izbor nema server.
			private static ChannelEndpoint? CreateEndpoint(string? pipe, string? socket)
			{
				if (OperatingSystem.IsWindows() && socket == null) {
					return new WindowsPipeEndpoint(pipe ?? AgentProtocol.DefaultPipeName);
				}
				if (OperatingSystem.IsLinux() && pipe == null) {
					return new LinuxSocketEndpoint(socket ?? AgentProtocol.DefaultSocketPath);
				}
				Console.WriteLine(OperatingSystem.IsWindows() ? "--socket postoji samo na Linuxu." : "--pipe postoji samo na Windows-u.");
				return null;
			}

			// "1,2,9" → [1, 2, 9]; null za "all" i za neispravan spisak.
			private static List<int>? ParseDoors(string text)
			{
				var doors = new List<int>();
				foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
					if (!int.TryParse(part, out var door)) {
						return null;
					}
					doors.Add(door);
				}
				return doors;
			}

			// CSV rasporeda iz Technician-a: DoorNumber;Size;Column;Row; zaglavlje i prazni redovi se preskaču.
			private static List<LayoutBox>? ReadLayout(string file)
			{
				var layout = new List<LayoutBox>();
				try {
					foreach (var line in File.ReadLines(file)) {
						var row = line.Trim().TrimStart('﻿');
						if (row.Length == 0 || row.StartsWith("DoorNumber", StringComparison.OrdinalIgnoreCase)) {
							continue;
						}
						var parts = row.Split(';', StringSplitOptions.TrimEntries);
						if (parts.Length < 4 || !int.TryParse(parts[0], out var door) || !int.TryParse(parts[2], out var column)
							|| !int.TryParse(parts[3], out var boxRow)) {
							Console.WriteLine($"Neispravan red rasporeda: '{row}'.");
							return null;
						}
						layout.Add(new LayoutBox { DoorNumber = door, Size = parts[1], Column = column, Row = boxRow });
					}
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
					Console.WriteLine($"Raspored {file} nije pročitan: {ex.Message}");
					return null;
				}
				return layout;
			}
		}
	}
}
