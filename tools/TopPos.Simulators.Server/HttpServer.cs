using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Flatsoft.Common.Text;
using Microsoft.Extensions.Logging;
using TopPos.Service;
using PenPos = TopPos.PenPosService;

namespace TopPos.ServerSimulator
{
	public class HttpServer
	{
		private readonly ServerSimulatorConfiguration _config;
		// v4: stanje je samo u memoriji (bez ServerStatus.json). U v2 ga je čuvao WPF Dispatcher (kolekcija vezana za DataGrid);
		// ovde jedan lock za pakete i vrata, a mrežna čekanja su van njega.
		private readonly List<PackageItem> _packages = [];
		private readonly DoorPanelViewModel _doorPanel;
		private readonly Lock _stateLock = new();
		private readonly ILogger _logger;
		private HttpListener? _listener;
		private CancellationTokenSource? _cts;
		private int _requestCount;

		// Evidencija primljenih API poziva za kontrolni API (/_sim/calls); kontrolni pozivi se ne beleže.
		private readonly List<LogEntry> _calls = [];
		private readonly Lock _callsLock = new();
		private long _callSeq;
		private readonly ServerFaults _faults = new();

		private int _invoiceSeq;
		private volatile bool _lockerInError;	// PrijavaGreskePaketomata / PonistavanjeGreskePaketomata; ne čuva se u ServerStatus.json

		private const string ControlApiPrefix = "/_sim/";
		private static readonly JsonSerializerOptions __controlJsonOptions = new(JsonSerializerDefaults.Web);

		public event Action<LogEntry>? OnRequestLogged;
		public int RequestCount => _requestCount;

		public HttpServer(ServerSimulatorConfiguration config, ILogger logger)
		{
			_config = config;
			_doorPanel = new DoorPanelViewModel(config.DoorCount);
			_logger = logger;
		}

		/// <summary>Port na kome server sluša; sa <c>Server.Port</c> = 0 bira se slobodan port.</summary>
		public int Port { get; private set; }

		public void Start()
		{
			_cts = new CancellationTokenSource();
			var bindAddress = string.IsNullOrWhiteSpace(_config.Server.BindAddress) ? "localhost" : _config.Server.BindAddress;
			// v4: HttpListener ne prima port 0 — slobodan port se bira unapred; ako ga u međuvremenu zauzme neko drugi, bira se nov.
			for (int attempt = 1; ; attempt++)
			{
				var port = _config.Server.Port != 0 ? _config.Server.Port : FreeTcpPort();
				var listener = new HttpListener();
				listener.Prefixes.Add($"http://{bindAddress}:{port}/");
				try
				{
					listener.Start();
				}
				catch (HttpListenerException) when (_config.Server.Port == 0 && attempt < 5)
				{
					listener.Close();
					continue;
				}
				_listener = listener;
				Port = port;
				break;
			}
			_logger.LogInformation($"HTTP server started on http://{bindAddress}:{Port}");
			Task.Run(() => ListenLoop(_cts.Token));
		}

		private static int FreeTcpPort()
		{
			var probe = new TcpListener(IPAddress.Loopback, 0);
			probe.Start();
			var port = ((IPEndPoint)probe.LocalEndpoint).Port;
			probe.Stop();
			return port;
		}

		// v4: reset (/_sim/reset) menja generaciju stanja. Zahtev primljen pre reset-a (npr. još čeka zadato kašnjenje) posle
		// njega ne sme da menja stanje ni evidenciju sledećeg testa — dobija 503. Provera je pod lock-om, zajedno sa izmenom.
		private int _generation;
		private readonly AsyncLocal<int?> _requestGeneration = new();

		private bool IsStaleRequest() => _requestGeneration.Value is int generation && generation != Volatile.Read(ref _generation);

		private void ThrowIfStaleRequest()
		{
			if (IsStaleRequest())
				throw new SimulatedStatusException(503, "Simulator je resetovan tokom obrade zahteva");
		}

		private T Locked<T>(Func<T> func)
		{
			lock (_stateLock)
			{
				ThrowIfStaleRequest();
				return func();
			}
		}

		private void Locked(Action action)
		{
			lock (_stateLock)
			{
				ThrowIfStaleRequest();
				action();
			}
		}

		public void Stop()
		{
			_cts?.Cancel();
			_listener?.Stop();
			_listener?.Close();
			_listener = null;
			_logger.LogInformation("HTTP server stopped");
		}

		public bool IsRunning => _listener?.IsListening == true;

		private async Task ListenLoop(CancellationToken ct)
		{
			while (!ct.IsCancellationRequested && _listener?.IsListening == true)
			{
				try
				{
					var context = await _listener.GetContextAsync().ConfigureAwait(false);
					_ = Task.Run(() => HandleRequestSafeAsync(context), ct);
				}
				catch (HttpListenerException) when (ct.IsCancellationRequested) { break; }
				catch (ObjectDisposedException) { break; }
				catch (Exception ex)
				{
					_logger.LogError($"Listen error: {ex.Message}");
				}
			}
		}

		/// <summary>
		/// v4: izuzetak iz obrade (npr. čitanje tela) ne sme ostati neprimećen — klijent bi čekao do isteka vremena bez traga u
		/// logu (E2E, 10.11.2026). Beleži se kao greška i, ako odgovor još nije poslat, vraća se 500.
		/// </summary>
		private async Task HandleRequestSafeAsync(HttpListenerContext context)
		{
			try
			{
				await HandleRequest(context).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, $"Obrada zahteva {context.Request.HttpMethod} {context.Request.Url?.AbsolutePath} nije uspela: {ex.Message}");
				try
				{
					context.Response.StatusCode = 500;
					context.Response.Close();
				}
				catch (Exception)
				{
					// odgovor je već poslat ili veza zatvorena
				}
			}
		}

		private async Task HandleRequest(HttpListenerContext context)
		{
			var request = context.Request;
			var response = context.Response;
			var path = request.Url?.AbsolutePath ?? "";
			var method = request.HttpMethod;

			if (IsBodyLengthMissing(request))
			{
				// v4: POST bez Content-Length (curl -X POST bez -d). Windows (HTTP.sys) ga sam odbija sa 411; managed HttpListener
				// na Linuxu odgovori 411, ali zahtev ipak preda ovde — ne sme da promeni stanje dok klijent vidi grešku.
				RejectLengthRequired(response);
				return;
			}

			string requestBody = "";
			if (request.HasEntityBody)
			{
				using var reader = new System.IO.StreamReader(request.InputStream, request.ContentEncoding);
				requestBody = await reader.ReadToEndAsync().ConfigureAwait(false);
			}

			if (_config.Server.EnableControlApi && path.StartsWith(ControlApiPrefix, StringComparison.OrdinalIgnoreCase))
			{
				await HandleControlRequest(request, response, path[ControlApiPrefix.Length..].TrimEnd('/'), requestBody).ConfigureAwait(false);
				return;
			}

			Interlocked.Increment(ref _requestCount);
			var timestamp = DateTime.Now;
			var endpoint = ExtractEndpoint(path);
			_logger.LogDebug($"{method} {path} (endpoint: {endpoint})");

			string responseJson;
			string contentType = "application/json";
			int statusCode = 200;
			bool isError = false;

			_requestGeneration.Value = Volatile.Read(ref _generation);
			try
			{
				var fault = _config.Server.EnableControlApi ? _faults.Take(endpoint) : null;
				if (fault != null)
				{
					_logger.LogInformation($"Zadata greška za {endpoint}: kašnjenje {fault.DelayMs} ms, greška '{fault.Error}', status {fault.Status}{(fault.AfterProcessing ? " posle obrade" : "")}");
					if (fault.DelayMs > 0)
						await Task.Delay(fault.DelayMs).ConfigureAwait(false);
					ThrowIfStaleRequest();
					if (!fault.AfterProcessing)
					{
						if (fault.Status != null)
							throw new SimulatedStatusException(fault.Status.Value, fault.Error ?? $"Zadat status {fault.Status}");
						if (fault.Error != null)
							throw new InvalidOperationException(fault.Error);
					}
				}
				responseJson = endpoint switch
				{
					nameof(HeartbeatPaketomata) => HandleHeartbeat(request),
					"StatusPaketomata" => HandleStatusPaketomata(),
					nameof(KurirAutorizacija) => HandleKurirAutorizacija(request),
					nameof(KurirPozicijeZaPraznjenje) => HandleKurirPozicijeZaPraznjenje(),
					nameof(KurirPunjenjePozicije) => HandleKurirPunjenjePozicije(request),
					nameof(KurirPunjenjePozicijePotvrda) => HandleKurirPunjenjePozicijePotvrda(requestBody),
					nameof(KurirPraznjenjePozicijePotvrda) => HandleKurirPraznjenjePozicijePotvrda(requestBody),
					nameof(KurirPromenaVelicinePosiljke) => HandleKurirPromenaVelicinePosiljke(requestBody),
					nameof(PrimaocPraznjenjePozicije) => HandlePrimaocPraznjenjePozicije(request),
					nameof(PrimaocPraznjenjePozicijePotvrda) => HandlePrimaocPraznjenjePozicijePotvrda(requestBody),
					nameof(PotvrdaPlacanja) => HandlePotvrdaPlacanja(requestBody),
					nameof(PrijavaGreskePaketomata) => HandlePrijavaGreskePaketomata(requestBody),
					nameof(PonistavanjeGreskePaketomata) => HandlePonistavanjeGreskePaketomata(requestBody),
					nameof(PrijavaGreskePozicije) => HandlePrijavaGreskePozicije(requestBody),
					nameof(PonistavanjeGreskePozicije) => HandlePonistavanjeGreskePozicije(requestBody),
					PenPosPinEndpoint => HandlePenPosPin(requestBody),
					PenPosInvoiceEndpoint => HandlePenPosInvoice(requestBody),
					_ => throw new InvalidOperationException($"Unknown endpoint: {endpoint}")
				};
				if (endpoint == PenPosPinEndpoint)
					contentType = "text/plain";
				// Zahtev je obrađen (stanje promenjeno), ali klijent dobija grešku — kao izgubljen odgovor.
				if (fault?.AfterProcessing == true)
					throw new SimulatedStatusException(fault.Status!.Value, fault.Error ?? $"Zadat status {fault.Status} posle obrade");
			}
			catch (Exception ex)
			{
				statusCode = ex is SimulatedStatusException simulated ? simulated.StatusCode : 400;
				isError = true;
				// PenPOS greške imaju svoj oblik (ErrorDetails) — klijent ga čita u PenPosServiceClient.TryLogErrorDetails.
				responseJson = endpoint is PenPosPinEndpoint or PenPosInvoiceEndpoint
								? JsonSerializer.Serialize(new PenPos.ErrorDetails { Message = ex.Message })
								: JsonApiSerialization.Serialize(new ResponseBase
								{
									ResponseStatus = new ResponseStatus
									{
										ErrorCode = "ArgumentException",
										Message = ex.Message,
										// Kao pravi server: jedna stavka sa istom porukom. Klijent po njoj prepoznaje odgovore koje
										// tretira kao uspeh (npr. „je već bez greške", TopPosServiceClient.IsArgumentErrorResponse).
										Errors = ex is SimulatedStatusException ? null! :
													[new ResponseError { ErrorCode = "ArgumentException", Message = ex.Message }]
									}
								});
			}

			var logEntry = new LogEntry
			{
				Timestamp = timestamp,
				Endpoint = endpoint,
				Method = method,
				Query = request.QueryString.AllKeys
								.Where(key => key != null)
								.ToDictionary(key => key!, key => request.QueryString[key]),
				RequestBody = requestBody,
				ResponseBody = responseJson,
				StatusCode = statusCode,
				IsError = isError,
				Detail = GetLogDetail(endpoint, requestBody, responseJson)
			};
			lock (_callsLock)
			{
				logEntry.Seq = ++_callSeq;
				if (_config.Server.EnableControlApi && !IsStaleRequest())	// u ručnom radu lista bi rasla bez granice; v4: bez zahteva pre reset-a
				{
					_calls.Add(logEntry);
				}
			}
			OnRequestLogged?.Invoke(logEntry);

			try
			{
				response.StatusCode = statusCode;
				response.ContentType = contentType;
				var buffer = Encoding.UTF8.GetBytes(responseJson);
				response.ContentLength64 = buffer.Length;
				await response.OutputStream.WriteAsync(buffer).ConfigureAwait(false);
				response.Close();
			}
			catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
			{
				// klijent je odustao (npr. timeout posle zadatog kašnjenja) — odgovor nema kome da ode
				_logger.LogWarning($"{endpoint}: odgovor nije poslat ({ex.Message})");
			}
		}

		private static bool IsBodyLengthMissing(HttpListenerRequest request)
			=> request.HttpMethod is "POST" or "PUT"
				&& request.Headers["Content-Length"] == null
				&& request.Headers["Transfer-Encoding"] == null;

		/// <summary>411; na Linuxu je odgovor već poslao HttpListener, pa upis ne uspeva — to je očekivano.</summary>
		private static void RejectLengthRequired(HttpListenerResponse response)
		{
			try
			{
				response.StatusCode = 411;
				response.Close();
			}
			catch (Exception ex) when (ex is ObjectDisposedException or HttpListenerException or InvalidOperationException) { }
		}

		private string GetLogDetail(string endpoint, string requestBody, string responseBody)
		{
			try
			{
				if (endpoint == nameof(PrimaocPraznjenjePozicije) && !string.IsNullOrEmpty(responseBody))
				{
					var resp = JsonApiSerialization.Deserialize<PrimaocPraznjenjePozicijeResponse>(responseBody);
					if (resp != null)
						return $"pozicija={resp.BrojPozicije}, iznos={resp.IznosZaUplatu:F2}";
				}
				if (endpoint == nameof(PrijavaGreskePozicije) && !string.IsNullOrEmpty(requestBody))
				{
					var req = JsonApiSerialization.Deserialize<PrijavaGreskePozicije>(requestBody);
					if (req != null)
						return $"pozicija={req.BrojPozicije}, greska={req.GreskaPozicije}";
				}
				if (endpoint == nameof(PonistavanjeGreskePozicije) && !string.IsNullOrEmpty(requestBody))
				{
					var req = JsonApiSerialization.Deserialize<PonistavanjeGreskePozicije>(requestBody);
					if (req != null)
						return $"pozicija={req.BrojPozicije}";
				}
				if (endpoint == PenPosPinEndpoint)
					return $"pin={requestBody} -> {responseBody}";
				if (endpoint == PenPosInvoiceEndpoint && !string.IsNullOrEmpty(responseBody))
				{
					var resp = JsonSerializer.Deserialize<PenPos.InvoiceResult>(responseBody);
					if (resp?.InvoiceNumber != null)
						return $"racun={resp.InvoiceNumber}, iznos={resp.TotalAmount:F2}";
				}
			}
			catch { }
			return string.Empty;
		}

		private string ExtractEndpoint(string path)
		{
			// Handle both /Api/EndpointName/ and /KurirskaSluzba/StatusPaketomata/
			var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
			return segments.Length >= 2 ? segments[^1] : segments.Length == 1 ? segments[0] : "";
		}

		// ─── Kontrolni API (/_sim/*) — samo za E2E testove, uključuje se sa Server.EnableControlApi ───
		//   GET  /_sim/health             → server je gore (v4: i port)
		//   POST /_sim/reset              → v4: bez paketa, grešaka vrata i paketomata, zadatih grešaka i evidencije poziva
		//   GET  /_sim/packages           → trenutni paketi
		//   POST /_sim/packages           → dodaje paket (telo: PackageItem)
		//   GET  /_sim/calls?since=<seq>  → primljeni API pozivi sa Seq > since (redosled obrade)
		//   GET  /_sim/faults             → zadate greške
		//   POST /_sim/faults             → {"endpoint":"PrimaocPraznjenjePozicije","delayMs":0,"error":"...","status":503,"count":1,"afterProcessing":false}
		//   POST /_sim/faults/clear
		private async Task HandleControlRequest(HttpListenerRequest request, HttpListenerResponse response, string command, string requestBody)
		{
			int statusCode = 200;
			object? result;
			try
			{
				result = (request.HttpMethod, command.ToLowerInvariant()) switch
				{
					("GET", "health") => new { Status = "ok", Port },
					("POST", "reset") => Reset(),
					("GET", "packages") => GetPackagesSnapshot(),
					("POST", "packages") => AddPackage(requestBody),
					("GET", "calls") => GetCalls(request.QueryString["since"]),
					("GET", "faults") => _faults.Snapshot(),
					("POST", "faults") => ArmFault(requestBody),
					("POST", "faults/clear") => ClearFaults(),
					_ => throw new ControlApiException(404, $"Nepoznata kontrolna komanda: {request.HttpMethod} {command}")
				};
			}
			catch (ControlApiException ex)
			{
				statusCode = ex.StatusCode;
				result = new { Error = ex.Message };
			}
			catch (Exception ex)
			{
				statusCode = 400;
				result = new { Error = ex.Message };
			}
			_logger.LogDebug($"Control API: {request.HttpMethod} {command} -> {statusCode}");

			response.StatusCode = statusCode;
			response.ContentType = "application/json";
			var buffer = JsonSerializer.SerializeToUtf8Bytes(result, __controlJsonOptions);
			response.ContentLength64 = buffer.Length;
			await response.OutputStream.WriteAsync(buffer).ConfigureAwait(false);
			response.Close();
		}

		private PackageItem AddPackage(string requestBody)
		{
			var pkg = JsonSerializer.Deserialize<PackageItem>(requestBody, __controlJsonOptions)
						?? throw new ControlApiException(400, "Prazno telo zahteva");
			if (string.IsNullOrWhiteSpace(pkg.Barkod))
				throw new ControlApiException(400, "Barkod je obavezan");

			// Provera i dodavanje pod lock-om stanja; greška se baca van lock-a.
			var conflict = Locked(() =>
			{
				if (_packages.Any(p => p.Barkod == pkg.Barkod))
					return $"Paket sa barkodom {pkg.Barkod} već postoji";
				if (pkg.KodZaOtvaranje > 0 && _packages.Any(p => p.KodZaOtvaranje == pkg.KodZaOtvaranje))
					return $"Paket sa kodom {pkg.KodZaOtvaranje} već postoji";
				if (pkg.UPaketomatu && _packages.Any(p => p.UPaketomatu && p.BrojPozicije == pkg.BrojPozicije))
					return $"Pozicija {pkg.BrojPozicije} je već zauzeta";
				_packages.Add(pkg);
				return null;
			});
			if (conflict != null)
				throw new ControlApiException(409, conflict);
			return Locked(pkg.Snapshot);
		}

		// Kopija vrednosti se pravi pod lock-om, a serijalizuje van njega.
		private List<PackageItem> GetPackagesSnapshot()
			=> Locked(() => _packages.Select(p => p.Snapshot()).ToList());

		// v4: E2E počinje svaki test od čistog stanja bez ponovnog pokretanja procesa. Redni broj poziva raste dalje.
		private object Reset()
		{
			Locked(() =>
			{
				Interlocked.Increment(ref _generation);
				_packages.Clear();
				_doorPanel.ClearErrors();
			});
			_lockerInError = false;
			_faults.Clear();
			lock (_callsLock)
			{
				_calls.Clear();
			}
			_logger.LogInformation("Control API: stanje simulatora je obrisano (reset)");
			return new { Status = "ok" };
		}

		private List<ServerFault> ArmFault(string requestBody)
		{
			var fault = JsonSerializer.Deserialize<ServerFault>(requestBody, __controlJsonOptions)
						?? throw new ControlApiException(400, "Prazno telo zahteva");
			_faults.Arm(fault);
			return _faults.Snapshot();
		}

		private List<ServerFault> ClearFaults()
		{
			_faults.Clear();
			return _faults.Snapshot();
		}

		private List<LogEntry> GetCalls(string? since)
		{
			long sinceSeq = long.TryParse(since, out var s) ? s : 0;
			lock (_callsLock)
			{
				return _calls.Where(c => c.Seq > sinceSeq).ToList();
			}
		}

		private sealed class ControlApiException(int statusCode, string message) : Exception(message)
		{
			public int StatusCode { get; } = statusCode;
		}

		// ─── PenPOS (ESIR/LPFR fiskalni uređaj) — isti port, endpoint = poslednji segment putanje ───
		// Prati ugovor klijenta (TopPos.PenPosService.PenPosServiceClient), ne ponašanje pravog uređaja.
		// Klijent se usmerava na simulator u app-configuration.json:
		//   "PenPosService": { "IsEnabled": true,
		//                      "BaseManagementUrl": "http://<host>:<port>/esir/v3",   → POST .../pin
		//                      "BaseInvoiceUrl": "http://<host>:<port>/api", ... }    → POST .../invoice
		// /_sim/faults važi i ovde (endpoint "pin" / "invoice"): kašnjenje, ili 400 sa ErrorDetails.
		private const string PenPosPinEndpoint = "pin";
		private const string PenPosInvoiceEndpoint = "invoice";

		// Telo je PIN kao tekst; odgovor je kod rezultata kao tekst (klijent poredi sirov sadržaj sa "0100").
		private string HandlePenPosPin(string requestBody)
		{
			var expected = _config.PenPos.Pin;
			return expected == null || requestBody.Trim() == expected
					? "0100"	// PinOK
					: "2100";	// PinNotOK
		}

		private string HandlePenPosInvoice(string requestBody)
		{
			var invoice = JsonSerializer.Deserialize<PenPos.InvoiceRequest>(requestBody)
							?? throw new InvalidOperationException("Prazno telo zahteva");
			if (invoice.Items is not { Count: > 0 })
				throw new InvalidOperationException("Racun nema stavki");
			if (invoice.Payment is not { Count: > 0 })
				throw new InvalidOperationException("Racun nema placanja");

			var total = invoice.Payment.Sum(p => p.Amount);
			var number = $"SIMUL001-SIMUL001-{Interlocked.Increment(ref _invoiceSeq)}";
			const double vatRate = 20.0;
			var labels = invoice.Items.SelectMany(i => i.Labels ?? []).Distinct().ToList();
			return JsonSerializer.Serialize(new PenPos.InvoiceResult
			{
				SdcDateTime = DateTime.Now,
				InvoiceNumber = number,
				TotalAmount = (double)total,
				TaxItems = labels.Select(label => new PenPos.TaxItem
				{
					Label = label,
					Rate = vatRate,
					Amount = Math.Round((double)total * vatRate / (100 + vatRate), 4),
					CategoryName = "VAT"
				}).ToList(),
				// Klijent od adrese samo pravi QR kod; adresa ne vodi na pravi račun.
				VerificationUrl = $"https://sandbox.suf.purs.gov.rs/v/?vl=SIMULATOR-{number}"
			});
		}

		private string HandleHeartbeat(HttpListenerRequest request)
		{
			return JsonApiSerialization.Serialize(new HeartbeatPaketomataResponse
			{
				HeartbeatInterval = 30
			});
		}

		private string HandleStatusPaketomata()
		{
			var packages = Locked(() => _packages.Where(p => !p.Preuzet).Select(p => p.Snapshot()).ToList());
			var statusBySize = new[] { "S", "M", "L" }.Select(size => new StatusPoVeliciniPozicije
			{
				Velicina = size,
				Zauzeto = packages.Count(p => p.Velicina == size),
				Slobodno = 8 - packages.Count(p => p.Velicina == size),
				ZaPreuzimanje = packages.Count(p => p.Velicina == size)
			}).ToList();

			var posiljke = packages.Select(p => new PosiljkaPoPoziciji
			{
				Barkod = p.Barkod,
				VelicinaPosiljke = p.Velicina,
				TelefonPrimaoca = p.TelefonPrimaoca,
				IznosOtkupa = p.IznosOtkupa,
				PaketVaziDo = DateTime.Now.AddDays(3),
				BrojRegistracije = 1,
				KodZaOtvaranje = p.KodZaOtvaranje,
				BrojPozicije = p.BrojPozicije,
				VelicinaPozicije = p.Velicina
			}).ToList();

			return JsonApiSerialization.Serialize(new StatusPaketomata
			{
				StatusiPoVelicinamaPozicija = statusBySize,
				PosiljkePoPozicijama = posiljke
			});
		}

		private string HandleKurirAutorizacija(HttpListenerRequest request)
		{
			var qrKod = request.QueryString[nameof(KurirAutorizacija.QrKod)] ?? "";
			if (_config.CourierQrCodes.Count > 0 && !_config.CourierQrCodes.Contains(qrKod))
				throw new InvalidOperationException("Nepoznat kurir");

			return JsonApiSerialization.Serialize(new KurirAutorizacijaResponse
			{
				SifraKurira = _config.DefaultCourierCode
			});
		}

		private string HandleKurirPozicijeZaPraznjenje()
		{
			var packages = Locked(() => _packages.Where(p => p.Istekao && !p.Preuzet).Select(p => p.Snapshot()).ToList());
			return JsonApiSerialization.Serialize(new KurirPozicijeZaPraznjenjeResponse
			{
				ListaPaketa = packages.Select(p => new BarkodPozicija
				{
					Barkod = p.Barkod,
					BrojPozicije = p.BrojPozicije
				}).ToList()
			});
		}

		private string HandleKurirPunjenjePozicije(HttpListenerRequest request)
		{
			var queryParams = request.QueryString;
			var barkod = queryParams["Barkod"] ?? "";

			var (packages, doorErrors) = Locked(() => (_packages.Select(p => p.Snapshot()).ToList(), _doorPanel.GetErrors()));
			var pkg = packages.FirstOrDefault(p => p.Barkod == barkod && p.BrojPozicije == 0);

			if (pkg == null)
				throw new InvalidOperationException($"Paket sa barkodom {barkod} nije pronađen ili već ima poziciju");

			// Kao pravi server: dodela ne zauzima vrata (zauzima ih tek potvrda), pa dva zahteva zaredom dobijaju ista vrata;
			// vrata preuzetog paketa su slobodna.
			var usedPositions = packages.Where(p => p.UPaketomatu).Select(p => p.BrojPozicije).ToHashSet();
			var erroredPositions = doorErrors.Select(e => e.DoorNumber).ToHashSet();
			int freePosition = Enumerable.Range(1, _config.DoorCount)
				.FirstOrDefault(i => !usedPositions.Contains(i) && !erroredPositions.Contains(i) && GetBoxSize(i) == pkg.Velicina);

			return JsonApiSerialization.Serialize(new KurirPunjenjePozicijeResponse
			{
				BrojPozicije = freePosition > 0 ? freePosition : 0
			});
		}

		private string HandleKurirPunjenjePozicijePotvrda(string requestBody)
		{
			var req = JsonApiSerialization.Deserialize<KurirPunjenjePozicijePotvrda>(requestBody);
			var brojPozicije = req?.BrojPozicije ?? 0;

			if (brojPozicije > 0 && req?.Barkod != null)
			{
				var error = Locked(() =>
				{
					var pkg = _packages.FirstOrDefault(p => p.Barkod == req.Barkod);
					// Kao pravi server (de-test, 2026-10-09): vrata koja drži drugi paket se ne potvrđuju — npr. zakasnela
					// potvrda posle nove dodele istih vrata. Ponovljena potvrda istog paketa prolazi.
					if (_packages.Any(p => p != pkg && p.UPaketomatu && p.BrojPozicije == brojPozicije))
						return $"Prijavljena pozicija \"{brojPozicije}\" se ne poklapa sa slobodnom pozicijom iz sistema";
					if (pkg != null)
					{
						pkg.BrojPozicije = brojPozicije;
						pkg.Velicina = GetBoxSize(brojPozicije);
					}
					_doorPanel.UpdatePackages(_packages);
					return null;
				});
				if (error != null)
					throw new InvalidOperationException(error);
			}

			return JsonApiSerialization.Serialize(new KurirPunjenjePozicijePotvrdaResponse
			{
				PopunjenBrojPozicije = brojPozicije
			});
		}

		private string HandleKurirPraznjenjePozicijePotvrda(string requestBody)
		{
			var req = JsonApiSerialization.Deserialize<KurirPraznjenjePozicijePotvrda>(requestBody);
			var brojPozicije = req?.BrojPozicije ?? 0;

			if (brojPozicije > 0)
			{
				Locked(() =>
				{
					var pkg = _packages.FirstOrDefault(p => p.UPaketomatu && p.BrojPozicije == brojPozicije);
					if (pkg != null)
						_packages.Remove(pkg);
				});
			}

			return JsonApiSerialization.Serialize(new KurirPraznjenjePozicijePotvrdaResponse
			{
				IspraznjenBrojPozicije = brojPozicije
			});
		}

		private static string GetBoxSize(int position)
		{
			int posInGroup = (position - 1) % 8;
			return posInGroup < 3 ? "S" : posInGroup < 6 ? "M" : "L";
		}

		private string HandleKurirPromenaVelicinePosiljke(string requestBody)
		{
			var req = JsonApiSerialization.Deserialize<KurirPromenaVelicinePosiljke>(requestBody);
			var newSize = Locked(() =>
			{
				var pkg = _packages.FirstOrDefault(p => p.Barkod == req?.Barkod);
				var currentSize = pkg?.Velicina ?? "S";

				// S → M → L; L ostaje L
				var size = currentSize switch
				{
					"S" => Velicine.M,
					"M" => Velicine.L,
					_ => Velicine.L
				};

				if (pkg != null)
				{
					pkg.Velicina = size.ToString();
				}
				return size;
			});

			return JsonApiSerialization.Serialize(new KurirPromenaVelicinePosiljkeResponse
			{
				NovaVelicina = newSize
			});
		}

		private string HandlePrimaocPraznjenjePozicije(HttpListenerRequest request)
		{
			var queryParams = request.QueryString;
			var kodStr = queryParams["KodZaOtvaranje"];
			var telefon = queryParams["TelefonPrimaoca"];
			int.TryParse(kodStr, out int kod);

			var pkg = Locked(() => _packages.FirstOrDefault(p => p.KodZaOtvaranje == kod)?.Snapshot());
			if (pkg == null)
			{
				// throw new InvalidOperationException($"Paket sa kodom {kod} nije pronađen");
				throw new InvalidOperationException($"Neispravan kod za otvaranje");
			}

			if (!string.IsNullOrEmpty(pkg.TelefonPrimaoca) && pkg.TelefonPrimaoca != telefon)
			{
				throw new InvalidOperationException($"Broj telefona nije ispravan");
			}

			// Kao pravi server (de-test, 2026-10-09): pre potvrde ostavljanja paket nije u paketomatu; posle potvrđenog
			// preuzimanja isti kod otvara ista vrata dok kurir u njih ne ostavi nov paket.
			if (pkg.BrojPozicije == 0)
			{
				throw new InvalidOperationException("Paket još nije isporučen u paketomat");
			}
			if (pkg.Preuzet && Locked(() => _packages.Any(p => p.UPaketomatu && p.BrojPozicije == pkg.BrojPozicije)))
			{
				throw new InvalidOperationException("Na poziciji se nalazi novi paket. Preuzimanje nije moguće.");
			}

			// Kao pravi server: dok otkup nije plaćen, pozicija se ne otkriva (0) i vraća se iznos;
			// posle PotvrdaPlacanja vraća se stvarna pozicija i iznos 0.
			var cekaUplatu = pkg.CekaUplatu;
			return JsonApiSerialization.Serialize(new PrimaocPraznjenjePozicijeResponse
			{
				BrojPozicije = cekaUplatu ? 0 : pkg.BrojPozicije,
				IznosZaUplatu = cekaUplatu ? pkg.IznosOtkupa : 0
			});
		}

		private string HandlePrimaocPraznjenjePozicijePotvrda(string requestBody)
		{
			var req = JsonApiSerialization.Deserialize<PrimaocPraznjenjePozicijePotvrda>(requestBody);
			var brojPozicije = req?.BrojPozicije ?? 0;

			if (brojPozicije > 0)
			{
				// Paket ostaje (Preuzet), vrata postaju slobodna za ostavljanje; ponovljena potvrda prolazi (de-test, 2026-10-09).
				// Po kodu, ne po vratima: zakasnela potvrda ne sme da „preuzme" nov paket koji je u međuvremenu u istim vratima.
				Locked(() =>
				{
					var pkg = _packages.FirstOrDefault(p => p.KodZaOtvaranje == req!.KodZaOtvaranje && p.BrojPozicije == brojPozicije);
					if (pkg != null && !pkg.Preuzet)
					{
						pkg.Preuzet = true;
						_doorPanel.UpdatePackages(_packages);
					}
				});
			}

			return JsonApiSerialization.Serialize(new PrimaocPraznjenjePozicijePotvrdaResponse
			{
				BrojPozicije = brojPozicije
			});
		}

		private string HandlePotvrdaPlacanja(string requestBody)
		{
			var req = JsonApiSerialization.Deserialize<PotvrdaPlacanja>(requestBody);
			int.TryParse(req?.KodZaOtvaranje, out int kod);
			var uplaceno = (req?.IznosKartice ?? 0) + (req?.IznosGotovina ?? 0);

			// Provera i upis na UI niti (kolekcija i paket su vezani za DataGrid); greška se baca van Dispatcher-a.
			var error = Locked(() =>
			{
				var pkg = _packages.FirstOrDefault(p => p.KodZaOtvaranje == kod);
				if (pkg == null)
					return "Neispravan kod za otvaranje";
				// Kao pravi server (de-test, 2026-10-09): ponovljena potvrda (npr. iz PaymentConfirmationSender reda posle
				// izgubljenog odgovora) se odbija; uplata je već evidentirana.
				if (pkg.Placeno)
					return $"Za paket sa kodom \"{kod}\" je već evidentirana uplata";
				if (uplaceno != pkg.IznosOtkupa)
					return $"Iznos uplate {uplaceno:F2} ne odgovara iznosu otkupa {pkg.IznosOtkupa:F2}";
				pkg.Placeno = true;
				return null;
			});
			if (error != null)
				throw new InvalidOperationException(error);

			return JsonApiSerialization.Serialize(new PotvrdaPlacanjaResponse
			{
				EvidencioniBrojUplate = new Random().Next(10000, 99999)
			});
		}

		private string HandlePrijavaGreskePaketomata(string requestBody)
		{
			var req = JsonApiSerialization.Deserialize<PrijavaGreskePaketomata>(requestBody);
			_lockerInError = true;
			return JsonApiSerialization.Serialize(new PrijavaGreskePaketomataResponse
			{
				SifraBenzinskeStanice = req?.SifraBenzinskeStanice ?? _config.PaketomatId
			});
		}

		private string HandlePonistavanjeGreskePaketomata(string requestBody)
		{
			var req = JsonApiSerialization.Deserialize<PonistavanjeGreskePaketomata>(requestBody);
			var lockerId = req?.SifraBenzinskeStanice ?? _config.PaketomatId;
			// Kao pravi server: paketomat bez greške → 400 (klijent ga tretira kao uspeh, IsLockerNotInErrorStateResponse)
			if (!_lockerInError)
				throw new InvalidOperationException($"Paketomat na benzinskoj stanici \"{lockerId}\" nije u stanju greške");
			_lockerInError = false;
			return JsonApiSerialization.Serialize(new PonistavanjeGreskePaketomataResponse
			{
				SifraBenzinskeStanice = req?.SifraBenzinskeStanice ?? _config.PaketomatId
			});
		}

		private string HandlePrijavaGreskePozicije(string requestBody)
		{
			var req = JsonApiSerialization.Deserialize<PrijavaGreskePozicije>(requestBody);
			// Kao pravi server (de-test, 3.11.2026): pozicija van DoorCount → 400 (klijent ga tretira kao primljen)
			if (req != null && !IsKnownDoor(req.BrojPozicije))
				throw new InvalidOperationException($"Na paketomatu \"{req.SifraBenzinskeStanice}\" ne postoji broj pozicije \"{req.BrojPozicije}\"");
			if (req != null)
				Locked(() => _doorPanel.SetError(req.BrojPozicije, req.GreskaPozicije));
			return JsonApiSerialization.Serialize(new PrijavaGreskePozicijeResponse
			{
				BrojPozicije = req?.BrojPozicije ?? 0
			});
		}

		private string HandlePonistavanjeGreskePozicije(string requestBody)
		{
			var req = JsonApiSerialization.Deserialize<PonistavanjeGreskePozicije>(requestBody);
			if (req != null && !IsKnownDoor(req.BrojPozicije))
				throw new InvalidOperationException($"Na paketomatu \"{req.SifraBenzinskeStanice}\" ne postoji navedeni broj pozicije \"{req.BrojPozicije}\"");
			// Kao pravi server: vrata bez greške → 400 (klijent ga tretira kao uspeh, IsDoorNotInErrorStateResponse)
			if (req != null && !Locked(() => _doorPanel.HasError(req.BrojPozicije)))
				throw new InvalidOperationException($"Pozicija \"{req.BrojPozicije}\" je već bez greške");
			if (req != null)
				Locked(() => _doorPanel.ClearError(req.BrojPozicije));
			return JsonApiSerialization.Serialize(new PonistavanjeGreskePozicijeResponse
			{
				BrojPozicije = req?.BrojPozicije ?? 0
			});
		}

		private bool IsKnownDoor(int doorNumber) => doorNumber >= 1 && doorNumber <= _config.DoorCount;
	}
}
