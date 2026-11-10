using System.Collections.ObjectModel;
using System.Net;
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
		private readonly ObservableCollection<PackageItem> _packages;
		private readonly DoorPanelViewModel _doorPanel;
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

		public HttpServer(ServerSimulatorConfiguration config, ObservableCollection<PackageItem> packages, DoorPanelViewModel doorPanel, ILogger logger)
		{
			_config = config;
			_packages = packages;
			_doorPanel = doorPanel;
			_logger = logger;
		}

		public void Start()
		{
			_cts = new CancellationTokenSource();
			_listener = new HttpListener();
			var bindAddress = string.IsNullOrWhiteSpace(_config.Server.BindAddress) ? "localhost" : _config.Server.BindAddress;
			_listener.Prefixes.Add($"http://{bindAddress}:{_config.Server.Port}/");
			_listener.Start();
			_logger.LogInformation($"HTTP server started on http://{bindAddress}:{_config.Server.Port}");
			Task.Run(() => ListenLoop(_cts.Token));
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
					_ = Task.Run(() => HandleRequest(context), ct);
				}
				catch (HttpListenerException) when (ct.IsCancellationRequested) { break; }
				catch (ObjectDisposedException) { break; }
				catch (Exception ex)
				{
					_logger.LogError($"Listen error: {ex.Message}");
				}
			}
		}

		private async Task HandleRequest(HttpListenerContext context)
		{
			var request = context.Request;
			var response = context.Response;
			var path = request.Url?.AbsolutePath ?? "";
			var method = request.HttpMethod;

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

			try
			{
				var fault = _config.Server.EnableControlApi ? _faults.Take(endpoint) : null;
				if (fault != null)
				{
					_logger.LogInformation($"Zadata greška za {endpoint}: kašnjenje {fault.DelayMs} ms, greška '{fault.Error}', status {fault.Status}{(fault.AfterProcessing ? " posle obrade" : "")}");
					if (fault.DelayMs > 0)
						await Task.Delay(fault.DelayMs).ConfigureAwait(false);
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
				if (_config.Server.EnableControlApi)	// u ručnom radu lista bi rasla bez granice
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
		//   GET  /_sim/health             → server je gore
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
					("GET", "health") => new { Status = "ok" },
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

			// Provera i dodavanje na UI niti (kolekcija je vezana za DataGrid); greška se baca van Dispatcher-a.
			var conflict = System.Windows.Application.Current.Dispatcher.Invoke(() =>
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
			return System.Windows.Application.Current.Dispatcher.Invoke(pkg.Snapshot);
		}

		// Kolekciju menja UI nit (DataGrid, potvrde preuzimanja): kopija vrednosti se pravi na UI niti, a serijalizuje van nje.
		private List<PackageItem> GetPackagesSnapshot()
			=> System.Windows.Application.Current.Dispatcher.Invoke(() => _packages.Select(p => p.Snapshot()).ToList());

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
			var packages = _packages.Where(p => !p.Preuzet).ToList();
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
			var packages = _packages.Where(p => p.Istekao && !p.Preuzet).ToList();
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

			var packages = _packages.ToList();
			var pkg = packages.FirstOrDefault(p => p.Barkod == barkod && p.BrojPozicije == 0);

			if (pkg == null)
				throw new InvalidOperationException($"Paket sa barkodom {barkod} nije pronađen ili već ima poziciju");

			// Kao pravi server: dodela ne zauzima vrata (zauzima ih tek potvrda), pa dva zahteva zaredom dobijaju ista vrata;
			// vrata preuzetog paketa su slobodna.
			var usedPositions = packages.Where(p => p.UPaketomatu).Select(p => p.BrojPozicije).ToHashSet();
			var erroredPositions = _doorPanel.GetErrors().Select(e => e.DoorNumber).ToHashSet();
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
				var error = System.Windows.Application.Current.Dispatcher.Invoke(() =>
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
				System.Windows.Application.Current.Dispatcher.Invoke(() =>
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
			var pkg = _packages.ToList().FirstOrDefault(p => p.Barkod == req?.Barkod);
			var currentSize = pkg?.Velicina ?? "S";

			// S → M → L; L ostaje L
			var newSize = currentSize switch
			{
				"S" => Velicine.M,
				"M" => Velicine.L,
				_ => Velicine.L
			};

			if (pkg != null)
			{
				System.Windows.Application.Current.Dispatcher.Invoke(() =>
				{
					pkg.Velicina = newSize.ToString();
				});
			}

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

			var pkg = _packages.ToList().FirstOrDefault(p => p.KodZaOtvaranje == kod);
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
			if (pkg.Preuzet && _packages.ToList().Any(p => p.UPaketomatu && p.BrojPozicije == pkg.BrojPozicije))
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
				System.Windows.Application.Current.Dispatcher.Invoke(() =>
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
			var error = System.Windows.Application.Current.Dispatcher.Invoke(() =>
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
				_doorPanel.SetError(req.BrojPozicije, req.GreskaPozicije);
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
			if (req != null && !_doorPanel.HasError(req.BrojPozicije))
				throw new InvalidOperationException($"Pozicija \"{req.BrojPozicije}\" je već bez greške");
			if (req != null)
				_doorPanel.ClearError(req.BrojPozicije);
			return JsonApiSerialization.Serialize(new PonistavanjeGreskePozicijeResponse
			{
				BrojPozicije = req?.BrojPozicije ?? 0
			});
		}

		private bool IsKnownDoor(int doorNumber) => doorNumber >= 1 && doorNumber <= _config.DoorCount;
	}
}
