using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using TopPos.LockController;

namespace TopPos.LockerSimulator
{
	/// <summary>
	/// Evidencija otključavanja koje je kiosk tražio (NCU24L Unlock / serijski OpenDoorSingle), za E2E proveru
	/// da su otvorena prava vrata i nijedna druga. Bezbedno za pozive iz više niti.
	/// </summary>
	public sealed class UnlockLog
	{
		private readonly List<UnlockEntry> _entries = [];
		private readonly Lock _lock = new();
		private long _seq;

		public void Record(int doorNumber, bool unlocked)
		{
			lock (_lock) {
				_entries.Add(new UnlockEntry(++_seq, DateTime.Now, doorNumber, unlocked));
			}
		}

		public List<UnlockEntry> Since(long seq)
		{
			lock (_lock) {
				return _entries.Where(e => e.Seq > seq).ToList();
			}
		}

		/// <summary>v4: za <c>POST /_sim/reset</c>; redni broj raste dalje, pa <c>since</c> iz pre brisanja i dalje važi.</summary>
		public void Clear()
		{
			lock (_lock) {
				_entries.Clear();
			}
		}
	}

	/// <param name="Unlocked">false = vrata su blokirana u simulatoru, pa su ostala zaključana.</param>
	public sealed record UnlockEntry(long Seq, DateTime Timestamp, int Door, bool Unlocked);

	public enum LockFault
	{
		/// <summary>
		/// Otvaranje jednih vrata se izvrši (vrata otključana, upis u <see cref="UnlockLog"/>), ali odgovor kiosku se ne pošalje
		/// — kao odgovor izgubljen na magistrali. Serial: kiosk ne zna ishod, a vrata su otvorena, pa ne sme da ih otvara
		/// ponovo. NCU24L: klijent potvrdu Unlock-a ne traži, stanje čita iz GetState.
		/// </summary>
		DropOpenResponse,
	}

	/// <summary>Kvarovi kontrolera vrata za E2E (<c>POST /_sim/faults</c>); svaki važi za zadat broj komandi. Bezbedno za više niti.</summary>
	public sealed class LockFaults
	{
		private readonly Dictionary<LockFault, int> _armed = [];
		private readonly Lock _lock = new();

		public void Arm(LockFault fault, int count)
		{
			lock (_lock) {
				_armed[fault] = count;
			}
		}

		public void Clear()
		{
			lock (_lock) {
				_armed.Clear();
			}
		}

		/// <summary>True ako je kvar naoružan; troši jedno ponavljanje.</summary>
		public bool Take(LockFault fault)
		{
			lock (_lock) {
				if (!_armed.TryGetValue(fault, out var count) || count <= 0) {
					return false;
				}
				_armed[fault] = count - 1;
				return true;
			}
		}

		public Dictionary<string, int> Snapshot()
		{
			lock (_lock) {
				return _armed.Where(f => f.Value > 0).ToDictionary(f => f.Key.ToString(), f => f.Value);
			}
		}
	}

	/// <summary>
	/// Kontrolni API za E2E testove (HTTP na localhost-u, uključuje se sekcijom ControlApi u konfiguraciji).
	///   GET  /_sim/health                       → v4: i portovi (TcpPort, ControlApiPort)
	///   POST /_sim/reset                        → v4: vrata zatvorena i bez blokade, moduli uključeni, bez kvarova, evidencije i ZLG blokade
	///   GET  /_sim/doors                        → stanje svih vrata
	///   POST /_sim/doors/{n}/open|close|block|unblock
	///   POST /_sim/doors/close-all
	///   POST /_sim/modules/{m}/on|off           → m od 1
	///   GET  /_sim/unlocks?since=&lt;seq&gt;          → otključavanja koja je tražio kiosk
	///   GET  /_sim/faults                       → naoružani kvarovi kontrolera vrata
	///   POST /_sim/faults                       → {"fault":"DropOpenResponse","count":1}
	///   POST /_sim/faults/clear
	/// v4 faza A: skener (<c>/_sim/barcode</c>) i POS (<c>/_sim/pos/*</c>) iz v2 još nisu preneti (serijski par, faza B) — 404.
	/// </summary>
	public sealed class ControlApiServer
	{
		private const string Prefix = "/_sim/";

		private static readonly JsonSerializerOptions __jsonOptions = new(JsonSerializerDefaults.Web) {
			Converters = { new JsonStringEnumConverter() },
		};

		private readonly int _requestedPort;
		private readonly int _tcpPort;
		private readonly Locker _locker;
		private readonly Lock _stateLock;
		private readonly UnlockLog _unlockLog;
		private readonly LockFaults _lockFaults;
		private readonly Action? _onReset;
		private readonly ILogger? _logger;
		private HttpListener? _listener;

		/// <param name="port">0 = slobodan port (<see cref="Port"/>).</param>
		/// <param name="tcpPort">Port NCU24L simulatora, za <c>/_sim/health</c>.</param>
		/// <param name="stateLock">Lock stanja vrata i modula (v2: WPF Dispatcher), isti kao za komande kontrolera.</param>
		/// <param name="onReset">Dodatno za <c>POST /_sim/reset</c> (skidanje ZLG blokade u kontroleru).</param>
		public ControlApiServer(int port, int tcpPort, Locker locker, Lock stateLock, UnlockLog unlockLog, LockFaults lockFaults, ILogger? logger, Action? onReset = null)
		{
			_requestedPort = port;
			_tcpPort = tcpPort;
			_locker = locker;
			_stateLock = stateLock;
			_unlockLog = unlockLog;
			_lockFaults = lockFaults;
			_onReset = onReset;
			_logger = logger;
		}

		public int Port { get; private set; }

		public void Start()
		{
			// v4: HttpListener ne prima port 0 — slobodan port se bira unapred; ako ga u međuvremenu zauzme neko drugi, bira se nov.
			for (int attempt = 1; ; attempt++) {
				var port = _requestedPort != 0 ? _requestedPort : FreeTcpPort();
				var listener = new HttpListener();
				listener.Prefixes.Add($"http://localhost:{port}/");
				try {
					listener.Start();
				}
				catch (HttpListenerException) when (_requestedPort == 0 && attempt < 5) {
					listener.Close();
					continue;
				}
				_listener = listener;
				Port = port;
				break;
			}
			_logger?.LogInformation($"Control API: http://localhost:{Port}{Prefix}");
			_ = Task.Run(ListenLoop);
		}

		public void Stop()
		{
			_listener?.Stop();
			_listener?.Close();
			_listener = null;
		}

		private static int FreeTcpPort()
		{
			var probe = new TcpListener(IPAddress.Loopback, 0);
			probe.Start();
			var port = ((IPEndPoint)probe.LocalEndpoint).Port;
			probe.Stop();
			return port;
		}

		private async Task ListenLoop()
		{
			while (_listener?.IsListening == true) {
				HttpListenerContext context;
				try {
					context = await _listener.GetContextAsync().ConfigureAwait(false);
				}
				catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException) {
					break;
				}
				_ = Task.Run(() => HandleRequest(context));
			}
		}

		private async Task HandleRequest(HttpListenerContext context)
		{
			var request = context.Request;
			var response = context.Response;
			var path = request.Url?.AbsolutePath ?? "";

			if (IsBodyLengthMissing(request)) {
				// v4: POST bez Content-Length (curl -X POST bez -d). Windows (HTTP.sys) ga sam odbija sa 411; managed HttpListener
				// na Linuxu odgovori 411, ali zahtev ipak preda ovde — ne sme da promeni stanje dok klijent vidi grešku.
				RejectLengthRequired(response);
				return;
			}

			int statusCode = 200;
			object? result;
			try {
				string body = "";
				if (request.HasEntityBody) {
					using var reader = new System.IO.StreamReader(request.InputStream, request.ContentEncoding);
					body = await reader.ReadToEndAsync().ConfigureAwait(false);
				}
				if (!path.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) {
					throw new ControlApiException(404, $"Nepoznata putanja: {path}");
				}
				var segments = path[Prefix.Length..].Trim('/').ToLowerInvariant().Split('/', StringSplitOptions.RemoveEmptyEntries);
				result = Dispatch(request.HttpMethod, segments, request.QueryString["since"], body);
			}
			catch (ControlApiException ex) {
				statusCode = ex.StatusCode;
				result = new { Error = ex.Message };
			}
			catch (Exception ex) {
				statusCode = 400;
				result = new { Error = ex.Message };
			}
			_logger?.LogDebug($"Control API: {request.HttpMethod} {path} -> {statusCode}");

			try {
				response.StatusCode = statusCode;
				response.ContentType = "application/json";
				var buffer = JsonSerializer.SerializeToUtf8Bytes(result, __jsonOptions);
				response.ContentLength64 = buffer.Length;
				await response.OutputStream.WriteAsync(buffer).ConfigureAwait(false);
				response.Close();
			}
			catch (Exception ex) {
				_logger?.LogWarning($"Control API: slanje odgovora nije uspelo: {ex.Message}");
			}
		}

		private static bool IsBodyLengthMissing(HttpListenerRequest request)
			=> request.HttpMethod is "POST" or "PUT"
				&& request.Headers["Content-Length"] == null
				&& request.Headers["Transfer-Encoding"] == null;

		/// <summary>411; na Linuxu je odgovor već poslao HttpListener, pa upis ne uspeva — to je očekivano.</summary>
		private static void RejectLengthRequired(HttpListenerResponse response)
		{
			try {
				response.StatusCode = 411;
				response.Close();
			}
			catch (Exception ex) when (ex is ObjectDisposedException or HttpListenerException or InvalidOperationException) { }
		}

		private object? Dispatch(string method, string[] s, string? since, string body)
		{
			switch (method, s.Length > 0 ? s[0] : "") {
				case ("GET", "health") when s.Length == 1:
					return new { Status = "ok", TcpPort = _tcpPort, ControlApiPort = Port };

				case ("POST", "reset") when s.Length == 1:
					Locked(() => {
						foreach (var module in _locker.Modules) {
							module.IsOn = true;
						}
						ForEachBox(box => { box.IsLocked = true; box.IsBlocked = false; });
						return true;
					});
					_lockFaults.Clear();
					_unlockLog.Clear();
					_onReset?.Invoke();
					_logger?.LogInformation("Control API: stanje simulatora je obrisano (reset)");
					return new { Status = "ok" };

				case ("GET", "doors") when s.Length == 1:
					return Locked(GetDoors);
				case ("POST", "doors") when s.Length == 2 && s[1] == "close-all":
					return Locked(() => { ForEachBox(box => box.IsLocked = true); return GetDoors(); });
				case ("POST", "doors") when s.Length == 3:
					return Locked(() => DoorAction(ParseNumber(s[1]), s[2]));

				case ("POST", "modules") when s.Length == 3:
					return Locked(() => ModuleAction(ParseNumber(s[1]), s[2]));

				case ("GET", "unlocks") when s.Length == 1:
					return _unlockLog.Since(long.TryParse(since, out var seq) ? seq : 0);

				case ("GET", "faults") when s.Length == 1:
					return _lockFaults.Snapshot();
				case ("POST", "faults") when s.Length == 1: {
					var request = Deserialize<LockFaultRequest>(body) ?? throw new ControlApiException(400, "Prazno telo zahteva");
					_lockFaults.Arm(request.Fault, Math.Max(1, request.Count));
					_logger?.LogInformation($"Control API: kvar kontrolera {request.Fault} naoružan za {Math.Max(1, request.Count)} komandi.");
					return _lockFaults.Snapshot();
				}
				case ("POST", "faults") when s.Length == 2 && s[1] == "clear":
					_lockFaults.Clear();
					return _lockFaults.Snapshot();
			}
			throw new ControlApiException(404, $"Nepoznata kontrolna komanda: {method} {string.Join('/', s)}");
		}

		// ─── Vrata i moduli (stanje pod lock-om; v2: view-model-i na UI niti) ───

		private T Locked<T>(Func<T> func)
		{
			lock (_stateLock) {
				return func();
			}
		}

		private IEnumerable<LockBox> AllBoxes => _locker.Modules.SelectMany(m => m.Boxes);

		private void ForEachBox(Action<LockBox> action)
		{
			foreach (var box in AllBoxes) {
				action(box);
			}
		}

		private List<DoorState> GetDoors()
			=> _locker.Modules
					.SelectMany((module, index) => module.Boxes.Select(box => new DoorState(box.Number, index + 1, box.IsLocked, box.IsBlocked, module.IsOn)))
					.ToList();

		private DoorState DoorAction(int number, string action)
		{
			var box = AllBoxes.FirstOrDefault(b => b.Number == number)
						?? throw new ControlApiException(404, $"Vrata {number} ne postoje");
			switch (action) {
				case "open": box.IsLocked = false; break;
				case "close": box.IsLocked = true; break;
				case "block": box.IsBlocked = true; break;
				case "unblock": box.IsBlocked = false; break;
				default: throw new ControlApiException(404, $"Nepoznata akcija za vrata: {action}");
			}
			return GetDoors().Single(d => d.Number == number);
		}

		private object ModuleAction(int number, string action)
		{
			if (number < 1 || number > _locker.Modules.Count) {
				throw new ControlApiException(404, $"Modul {number} ne postoji");
			}
			_locker.Modules[number - 1].IsOn = action switch {
				"on" => true,
				"off" => false,
				_ => throw new ControlApiException(404, $"Nepoznata akcija za modul: {action}"),
			};
			return new { Module = number, IsOn = _locker.Modules[number - 1].IsOn };
		}

		// ─── Pomoćno ───

		private static int ParseNumber(string text)
			=> int.TryParse(text, out var n) ? n : throw new ControlApiException(400, $"Neispravan broj: {text}");

		private static T? Deserialize<T>(string body) where T : class
			=> string.IsNullOrWhiteSpace(body) ? null : JsonSerializer.Deserialize<T>(body, __jsonOptions);

		private sealed record DoorState(int Number, int Module, bool IsLocked, bool IsBlocked, bool ModuleOn);
		private sealed record LockFaultRequest(LockFault Fault, int Count = 1);

		private sealed class ControlApiException(int statusCode, string message) : Exception(message)
		{
			public int StatusCode { get; } = statusCode;
		}
	}
}
