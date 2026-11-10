using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using TopPos.LockerSimulator.Pos;
using TopPos.LockerSimulator.ViewModels;

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
	///   GET  /_sim/health
	///   GET  /_sim/doors                        → stanje svih vrata
	///   POST /_sim/doors/{n}/open|close|block|unblock
	///   POST /_sim/doors/close-all
	///   POST /_sim/modules/{m}/on|off           → m od 1
	///   GET  /_sim/unlocks?since=&lt;seq&gt;          → otključavanja koja je tražio kiosk
	///   GET  /_sim/faults                       → naoružani kvarovi kontrolera vrata
	///   POST /_sim/faults                       → {"fault":"DropOpenResponse","count":1}
	///   POST /_sim/faults/clear
	///   POST /_sim/barcode                      → {"barcode":"..."} šalje se kao sa skenera
	///   GET  /_sim/pos                          → faza, iznos, podešavanja, kvarovi
	///   POST /_sim/pos/card                     → {"card":"MastercardContactless"} (samo u fazi WaitingForCard)
	///   POST /_sim/pos/pin                      → {"pin":"1234"} ili bez tela = ispravan PIN (samo u WaitingForPin)
	///   POST /_sim/pos/cancel                   → kupac odustaje (WaitingForCard/WaitingForPin)
	///   POST /_sim/pos/settings                 → {"autoCustomer":false,"autoCard":"...","cancel11Response":"Reject"}
	///   POST /_sim/pos/faults                   → {"fault":"NoResponse","count":1}
	///   POST /_sim/pos/faults/clear
	/// Akcija kupca van odgovarajuće faze vraća 409: engine bi je zanemario ili obrisao na početku sledeće sesije.
	/// </summary>
	public sealed class ControlApiServer
	{
		private const string Prefix = "/_sim/";

		private static readonly JsonSerializerOptions __jsonOptions = new(JsonSerializerDefaults.Web) {
			Converters = { new JsonStringEnumConverter() },
		};

		private static readonly Dictionary<string, SimCard> __cards = new(StringComparer.OrdinalIgnoreCase) {
			[nameof(SimCard.VisaChip)] = SimCard.VisaChip,
			[nameof(SimCard.MastercardContactless)] = SimCard.MastercardContactless,
			[nameof(SimCard.DinacardDeclined)] = SimCard.DinacardDeclined,
			[nameof(SimCard.VisaExpired)] = SimCard.VisaExpired,
		};

		private readonly int _port;
		private readonly LockerViewModel _locker;
		private readonly BarcodeScanner _barcodeScanner;
		private readonly PosTerminalEngine? _posEngine;
		private readonly PosTerminalViewModel? _posViewModel;
		private readonly UnlockLog _unlockLog;
		private readonly LockFaults _lockFaults;
		private readonly Dispatcher _dispatcher;
		private readonly ILogger? _logger;
		private HttpListener? _listener;

		public ControlApiServer(int port, LockerViewModel locker, BarcodeScanner barcodeScanner, PosTerminalEngine? posEngine,
								PosTerminalViewModel? posViewModel, UnlockLog unlockLog, LockFaults lockFaults, Dispatcher dispatcher, ILogger? logger)
		{
			_port = port;
			_locker = locker;
			_barcodeScanner = barcodeScanner;
			_posEngine = posEngine;
			_posViewModel = posViewModel;
			_unlockLog = unlockLog;
			_lockFaults = lockFaults;
			_dispatcher = dispatcher;
			_logger = logger;
		}

		public void Start()
		{
			_listener = new HttpListener();
			_listener.Prefixes.Add($"http://localhost:{_port}/");
			_listener.Start();
			_logger?.LogInformation($"Control API: http://localhost:{_port}{Prefix}");
			_ = Task.Run(ListenLoop);
		}

		public void Stop()
		{
			_listener?.Stop();
			_listener?.Close();
			_listener = null;
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
				result = await Dispatch(request.HttpMethod, segments, request.QueryString["since"], body).ConfigureAwait(false);
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

		private async Task<object?> Dispatch(string method, string[] s, string? since, string body)
		{
			switch (method, s.Length > 0 ? s[0] : "") {
				case ("GET", "health") when s.Length == 1:
					return new { Status = "ok" };

				case ("GET", "doors") when s.Length == 1:
					return OnUi(GetDoors);
				case ("POST", "doors") when s.Length == 2 && s[1] == "close-all":
					return OnUi(() => { ForEachBox(box => box.IsLocked = true); return GetDoors(); });
				case ("POST", "doors") when s.Length == 3:
					return OnUi(() => DoorAction(ParseNumber(s[1]), s[2]));

				case ("POST", "modules") when s.Length == 3:
					return OnUi(() => ModuleAction(ParseNumber(s[1]), s[2]));

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

				case ("POST", "barcode") when s.Length == 1: {
					var barcode = Deserialize<BarcodeRequest>(body)?.Barcode;
					if (string.IsNullOrWhiteSpace(barcode)) {
						throw new ControlApiException(400, "Barkod je obavezan");
					}
					await _barcodeScanner.Send(barcode, CancellationToken.None).ConfigureAwait(false);
					return new { Sent = barcode };
				}

				case (_, "pos"):
					return PosAction(method, s.Skip(1).ToArray(), body);
			}
			throw new ControlApiException(404, $"Nepoznata kontrolna komanda: {method} {string.Join('/', s)}");
		}

		// ─── Vrata i moduli (view-model-i žive na UI niti) ───

		private T OnUi<T>(Func<T> func)
		{
			// Greška se vraća kao rezultat i baca van Dispatcher-a.
			var (value, error) = _dispatcher.Invoke(() => {
				try {
					return (func(), (Exception?)null);
				}
				catch (Exception ex) {
					return (default(T)!, ex);
				}
			});
			if (error != null) {
				throw error;
			}
			return value;
		}

		private IEnumerable<LockBoxViewModel> AllBoxes => _locker.Modules.SelectMany(m => m.Boxes);

		private void ForEachBox(Action<LockBoxViewModel> action)
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

		// ─── POS ───

		private object PosAction(string method, string[] s, string body)
		{
			if (_posEngine == null || _posViewModel == null) {
				throw new ControlApiException(404, "POS simulator nije konfigurisan (sekcija PosTerminal)");
			}
			var phase = _posEngine.Status.Phase;
			switch (method, string.Join('/', s)) {
				case ("GET", ""):
					return GetPosState();

				case ("POST", "card"): {
					var name = Deserialize<CardRequest>(body)?.Card ?? "";
					if (!__cards.TryGetValue(name, out var card)) {
						throw new ControlApiException(400, $"Nepoznata kartica \"{name}\"; dozvoljene: {string.Join(", ", __cards.Keys)}");
					}
					RequirePhase(phase, PosTerminalPhase.WaitingForCard);
					_posEngine.PresentCard(card);
					return GetPosState();
				}

				case ("POST", "pin"):
					RequirePhase(phase, PosTerminalPhase.WaitingForPin);
					_posEngine.EnterPin(Deserialize<PinRequest>(body)?.Pin ?? _posEngine.Options.Pin);
					return GetPosState();

				case ("POST", "cancel"):
					RequirePhase(phase, PosTerminalPhase.WaitingForCard, PosTerminalPhase.WaitingForPin);
					_posEngine.CustomerCancel();
					return GetPosState();

				case ("POST", "settings"): {
					var settings = Deserialize<PosSettingsRequest>(body) ?? new PosSettingsRequest();
					SimCard? autoCard = null;
					if (settings.AutoCard != null && !__cards.TryGetValue(settings.AutoCard, out autoCard)) {
						throw new ControlApiException(400, $"Nepoznata kartica \"{settings.AutoCard}\"");
					}
					// Preko view-model-a, da prozor POS terminala prikazuje isto stanje.
					OnUi(() => {
						if (settings.AutoCustomer.HasValue) {
							_posViewModel.AutoCustomer = settings.AutoCustomer.Value;
						}
						if (autoCard != null) {
							_posViewModel.AutoCard = autoCard;
						}
						if (settings.Cancel11Response.HasValue) {
							_posViewModel.Cancel11Accept = settings.Cancel11Response == Cancel11Response.Accept;
						}
						return true;
					});
					return GetPosState();
				}

				case ("POST", "faults"): {
					var request = Deserialize<FaultRequest>(body) ?? throw new ControlApiException(400, "Prazno telo zahteva");
					_posEngine.Faults.Arm(request.Fault, Math.Max(1, request.Count));
					return GetPosState();
				}

				case ("POST", "faults/clear"):
					_posEngine.Faults.Clear();
					return GetPosState();
			}
			throw new ControlApiException(404, $"Nepoznata POS komanda: {method} pos/{string.Join('/', s)}");
		}

		private static void RequirePhase(PosTerminalPhase actual, params PosTerminalPhase[] allowed)
		{
			if (!allowed.Contains(actual)) {
				throw new ControlApiException(409, $"POS je u fazi {actual}; akcija je dozvoljena samo u: {string.Join(", ", allowed)}");
			}
		}

		private object GetPosState()
		{
			var status = _posEngine!.Status;
			return new {
				status.Phase,
				status.DisplayText,
				status.Amount,
				status.SeqNumber,
				status.TransactionType,
				_posEngine.AutoCustomer,
				AutoCard = __cards.First(c => c.Value == _posEngine.AutoCard).Key,
				_posEngine.Cancel11Response,
				Faults = _posEngine.Faults.Snapshot(),
			};
		}

		// ─── Pomoćno ───

		private static int ParseNumber(string text)
			=> int.TryParse(text, out var n) ? n : throw new ControlApiException(400, $"Neispravan broj: {text}");

		private static T? Deserialize<T>(string body) where T : class
			=> string.IsNullOrWhiteSpace(body) ? null : JsonSerializer.Deserialize<T>(body, __jsonOptions);

		private sealed record DoorState(int Number, int Module, bool IsLocked, bool IsBlocked, bool ModuleOn);
		private sealed record BarcodeRequest(string? Barcode);
		private sealed record CardRequest(string? Card);
		private sealed record PinRequest(string? Pin);
		private sealed record FaultRequest(PosFault Fault, int Count = 1);
		private sealed record LockFaultRequest(LockFault Fault, int Count = 1);
		private sealed record PosSettingsRequest(bool? AutoCustomer = null, string? AutoCard = null, Cancel11Response? Cancel11Response = null);

		private sealed class ControlApiException(int statusCode, string message) : Exception(message)
		{
			public int StatusCode { get; } = statusCode;
		}
	}
}
