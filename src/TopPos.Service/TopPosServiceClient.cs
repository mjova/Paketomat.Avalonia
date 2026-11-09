using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Mime;
using System.Net.Sockets;
using System.Text;
using Flatsoft.Common.Text;
using Flatsoft.Net.Http;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Extensions.Http;

namespace TopPos.Service
{
	public class TopPosServiceClient
    {
		private readonly string _lockerId;
		private readonly TopPosServiceConfiguration _configuration;
		private readonly HttpClient _serviceHttpClient;
		private readonly HttpClient? _heartbeatHttpClient;
		private readonly Task? _taskHeartbeat;
		private readonly SemaphoreSlim _semaphore;
		private readonly AuthenticationHeaderValue _serviceAuthenticationHeader;
		private readonly AuthenticationHeaderValue _posAuthenticationHeader;
		private readonly AuthenticationHeaderValue _courierServiceAuthenticationHeader;
		private readonly ILogger? _logger;

        public TopPosServiceClient(TopPosServiceConfiguration configuration, string lockerId, ILogger? logger, CancellationToken cancellationToken)
        {
			_lockerId = lockerId ?? throw new ArgumentNullException(nameof(lockerId));
			_configuration = configuration;
			_logger = logger;

			var serviceCredentialsBase64 = Encoding.UTF8.GetBytes($"{_configuration.ServiceUsername}:{_configuration.ServicePassword}");
			_serviceAuthenticationHeader = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(serviceCredentialsBase64));

			var posCredentialsBase64 = Encoding.UTF8.GetBytes($"{_configuration.POSUsername}:{_configuration.POSPassword}");
			_posAuthenticationHeader = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(posCredentialsBase64));

			var courierServiceCredentialsBase64 = Encoding.UTF8.GetBytes($"{_configuration.CourierServiceUsername}:{_configuration.CourierServicePassword}");
			_courierServiceAuthenticationHeader = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(courierServiceCredentialsBase64));

			_serviceHttpClient = CreateServiceHttpClient(_configuration.BaseUrl, logExcludeHeaders: ["Authorization"], logger);


			_semaphore = new SemaphoreSlim(1, 1);
			if (!configuration.DisableHeartbeats) {
				_heartbeatHttpClient = CreateHeartbeatHttpClient(_configuration.BaseUrl, logExcludeHeaders: ["Authorization"], logger);
				_taskHeartbeat = RunHeartbeatLoop(cancellationToken);
			}
			else {
				_logger?.LogWarning($"Heartbeat je isključen u konfiguraciji ({nameof(TopPosServiceConfiguration.DisableHeartbeats)} = true)");
			}
        }

		public HttpClient CreateServiceHttpClient(string baseAddress, IEnumerable<string>? logExcludeHeaders, ILogger? logger)
		{
			logger?.LogInformation($"Kreiranje HTTP klijenta...");

			var retryPolicy = HttpPolicyExtensions.HandleTransientHttpError()
													.WaitAndRetryAsync(retryCount: 3, 
																		retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
																		(outcome, timespan, retryAttempt, context) =>
																			{
																				logger?.LogWarning($"Retrying due to {outcome.Exception?.Message}. Attempt {retryAttempt}");
																			});
			var retryHandler = new PolicyHttpMessageHandler(retryPolicy);
			var requestHandler = new SocketsHttpHandler()
								{
									AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
									//PooledConnectionLifetime = TimeSpan.FromMinutes(5)	// DNS changes
								};

			var authHandler = new DynamicHttpAuthenticationHandler(uri
										=> uri!.AbsolutePath.Contains($"{nameof(PotvrdaPlacanja)}")? _posAuthenticationHeader : 
											uri!.AbsolutePath.Contains($"KurirskaSluzba")? _courierServiceAuthenticationHeader :
											_serviceAuthenticationHeader);

			var httpLoggingHandler = new HttpLoggingHandler(logger)
										{
											//LogFilter = request => request.RequestUri?.LocalPath != $"/{__loginPath}" // skip logging credentials
											ExcludeHeaders = logExcludeHeaders ?? Enumerable.Empty<string>()
										};
			// ProcessingTrackingHandler ispod Polly-ja vidi svaki pokušaj (potvrda ostavljanja razlikuje „sigurno nije upisano" od „ne zna se")
			var httpClient = HttpClientPipeline.Create(requestHandler, authHandler, retryHandler, httpLoggingHandler, new ProcessingTrackingHandler());
			httpClient.BaseAddress = new Uri(baseAddress);

			httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
			httpClient.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
			httpClient.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("deflate"));
			httpClient.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("br"));

			httpClient.DefaultRequestHeaders.Host = httpClient.BaseAddress.Host;

			logger?.LogInformation($"HTTP klijent kreiran za adresu {httpClient.BaseAddress}");

			return httpClient;
		}

		public HttpClient CreateHeartbeatHttpClient(string baseAddress, IEnumerable<string>? logExcludeHeaders, ILogger? logger)
		{
			logger?.LogInformation($"Kreiranje Heartbeat HTTP klijenta...");
			var requestHandler = new SocketsHttpHandler
									{
										AutomaticDecompression = DecompressionMethods.None, 
										ConnectTimeout = TimeSpan.FromMilliseconds(_configuration.HeartbeatFrequency),
										ResponseDrainTimeout = TimeSpan.FromMilliseconds(_configuration.HeartbeatFrequency), // Fail fast on slow responses
									};
			var authHandler = new DynamicHttpAuthenticationHandler(uri
										=> uri!.AbsolutePath.Contains($"{nameof(PotvrdaPlacanja)}") ?
												_posAuthenticationHeader : _serviceAuthenticationHeader);

			var httpLoggingHandler = new HttpLoggingHandler(logger)
										{
											//LogFilter = request => request.RequestUri?.LocalPath != $"/{__loginPath}" // skip logging credentials
											ExcludeHeaders = logExcludeHeaders ?? Enumerable.Empty<string>()
										};
			var httpClient = HttpClientPipeline.Create(requestHandler, authHandler, httpLoggingHandler);

			httpClient.DefaultRequestHeaders.Authorization = _serviceAuthenticationHeader;
			httpClient.BaseAddress = new Uri(baseAddress);
			httpClient.Timeout = TimeSpan.FromMilliseconds(_configuration.HeartbeatFrequency); // Request timeout

			return httpClient;
		}

		private async ValueTask<HttpResponseMessage?> ServiceCall(Func<CancellationToken, Task<HttpResponseMessage>> httpCall, bool notify, CancellationToken cancellationToken)
		{
			// Release samo ako je semafor preuzet: WaitAsync baca izuzetak kad se čekanje otkaže (npr. istekne RequestTimeout)
			bool acquired = false;
			try {
				await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
				acquired = true;
				if (notify) {
					NotifyRequestCallback(this.OnRequestStart, nameof(OnRequestStart));
				}
				if (cancellationToken.IsCancellationRequested) {
					return null;
				}
				HttpResponseMessage response = await httpCall(cancellationToken).ConfigureAwait(false);
				return response;
			}
			finally {
				if (acquired) {
					// Callback pre Release: sledeći zahtev ne sme da upali progress bar pre nego što ga ovaj ugasi.
					// Izuzetak iz callback-a se samo loguje — inače bi Release bio preskočen i semafor ostao trajno zauzet.
					if (notify) {
						NotifyRequestCallback(this.OnRequestEnd, nameof(OnRequestEnd));
					}
					_semaphore.Release();
				}
			}
		}

		// Povratni poziv ka UI-ju (progress bar) ne sme da obori serverski poziv.
		private void NotifyRequestCallback(Action? callback, string callbackName)
		{
			try {
				callback?.Invoke();
			}
			catch (Exception ex) {
				_logger?.LogWarning($"{nameof(ServiceCall)}: {callbackName} exception: {ex.Message}");
			}
		}

		// Da li se potvrda (PotvrdaPlacanja, PrimaocPraznjenjePozicijePotvrda) posle neuspeha
		// šalje ponovo. Samo 400 znači da je server odbio zahtev (ResponseStatus sa porukom); sve ostalo (5xx, 408, 429, 401,
		// 403, 404...) je prolazno - potvrda ostaje u listi i šalje se ponovo, umesto da se trajno izgubi.
		public static bool IsRecoverable(HttpStatusCode statusCode) => statusCode != HttpStatusCode.BadRequest;

		// Mrežne greške i isteklo vreme su prolazni. Istek RequestTimeout-a (i čekanja na semafor) stiže kao
		// OperationCanceledException (TaskCanceledException), ne kao TimeoutException.
		public static bool IsRecoverable(Exception ex)
			=> ex is OperationCanceledException or HttpRequestException or SocketException or TimeoutException or IOException;

		// Poruka iz ResponseStatus odgovora sa greškom; telo greške ne mora biti JSON (npr. HTML stranica proxy-ja za 502/503/504)
		private static async Task<string?> ReadErrorMessage(HttpResponseMessage response, CancellationToken cancellationToken)
		{
			try {
				var errorResponse = await JsonApiSerialization.DeserializeAsync<ResponseBase>(response.Content.ReadAsStream(cancellationToken), cancellationToken)
																.ConfigureAwait(false);
				return errorResponse?.ResponseStatus?.Message;
			}
			catch (System.Text.Json.JsonException) {
				return null;
			}
		}

		// Telo odgovora 200 na potvrdu; null ako nije ispravan odgovor: nije JSON objekat (prazno, "null", HTML stranica proxy-ja)
		// ili nema broj pozicije (isValid; pravi server ga u svakom 200 vraća popunjenog, i za ponovljenu potvrdu). Tada se ne zna
		// da li je server primio potvrdu, pa je neuspeh prolazan (server ponovljenu potvrdu preuzimanja/ostavljanja prihvata).
		private async Task<T?> ReadConfirmResponse<T>(HttpResponseMessage response, Func<T, bool> isValid, string caller, CancellationToken cancellationToken) where T : class
		{
			try {
				var result = await JsonApiSerialization.DeserializeAsync<T>(response.Content.ReadAsStream(cancellationToken), cancellationToken)
														.ConfigureAwait(false);
				if (result != null && isValid(result)) {
					return result;
				}
			}
			catch (System.Text.Json.JsonException) { }
			_logger?.LogError($"{caller}: [{response.StatusCode}] odgovor nije ispravan (nije JSON ili nema broj pozicije); prijem potvrde nije potvrdjen");
			return null;
		}

		private async Task RunHeartbeatLoop(CancellationToken cancellationToken)
		{
			_logger?.LogInformation($"Startovanje heartbeat petlje. Konfigurisana frekvencija slanja: {_configuration.HeartbeatFrequency / 1000} sekundi.");
			while (!cancellationToken.IsCancellationRequested)
			{
				try { 
					await SendHeartbeat(cancellationToken).ConfigureAwait(false);
					await Task.Delay(_configuration.HeartbeatFrequency, cancellationToken).ConfigureAwait(false);
				}
				catch (Exception ex) {
					_logger?.LogError($"{nameof(RunHeartbeatLoop)} exception: {ex.Message}");
				}
			}
		}

		public Action? OnRequestStart { get; set; }

		public Action? OnRequestEnd { get; set; }

		public Action? OnHeartbeatSent { get; set; }

		private async ValueTask SendHeartbeat(CancellationToken externalCancellationToken)
		{
			try
			{
				_logger?.LogDebug($"Sending a heartbeat to {_serviceHttpClient.BaseAddress}");
				// HTTP client for heartbeats is already configured with a short timeout (_configuration.HeartbeatFrequency)
				var url = $"{_configuration.PathPrefix}/{nameof(HeartbeatPaketomata)}/?{nameof(HeartbeatPaketomata.SifraBenzinskeStanice)}={_lockerId}";
				// using: oslobađa konekciju kad je HttpLoggingHandler ne pročita (logovanje ispod Debug nivoa)
				using var response = await _heartbeatHttpClient!.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, externalCancellationToken).ConfigureAwait(false);
				if (response?.IsSuccessStatusCode == true) {
					this.OnHeartbeatSent?.Invoke();
				}
            }
			catch (Exception ex) {
				_logger?.LogError($"{nameof(SendHeartbeat)} exception: {ex.Message}");	
			}
		}

		public async ValueTask<(string?, string?)> CourierLogin(string courierId, CancellationToken externalCancellationToken)
		{
			try
			{
				using var cts = new CancellationTokenSource();
				cts.CancelAfter(_configuration.RequestTimeout);
				var timeoutToken = cts.Token;

				using var linkedToken = CancellationTokenSource.CreateLinkedTokenSource(externalCancellationToken, timeoutToken);

				var url = $"{_configuration.PathPrefix}/{nameof(KurirAutorizacija)}/?{nameof(KurirAutorizacija.SifraBenzinskeStanice)}={_lockerId}&{nameof(KurirAutorizacija.QrKod)}={courierId}";
				_logger?.LogInformation($"{nameof(CourierLogin)}: GET {url}");
				var response = await ServiceCall(ct => _serviceHttpClient.GetAsync(url, ct), true, linkedToken.Token).ConfigureAwait(false);
				if (response == null) { 
					return (null, null);	// TODO: generic error message
				}
				var loginResponse = await JsonApiSerialization.DeserializeAsync<KurirAutorizacijaResponse>(response.Content.ReadAsStream(externalCancellationToken), externalCancellationToken)
																.ConfigureAwait(false);
				if (response.StatusCode != HttpStatusCode.OK) { 
					_logger?.LogError($"{nameof(CourierLogin)}: [{response.StatusCode}] {loginResponse?.ResponseStatus?.Message}");
					return (null, loginResponse?.ResponseStatus?.Message);
				}
				return (loginResponse?.SifraKurira, null);
			}
			catch (Exception ex) {
				_logger?.LogError($"{nameof(CourierLogin)} exception: {ex.Message}");
				return (null, null);
			}
		}

		public async ValueTask<(IEnumerable<BarkodPozicija>?, string?)> GetExpiredPackagesForPickup(string courierCode, CancellationToken externalCancellationToken)
		{
			try
			{
				using var cts = new CancellationTokenSource();
				cts.CancelAfter(_configuration.RequestTimeout);
				var timeoutToken = cts.Token;

				using var linkedToken = CancellationTokenSource.CreateLinkedTokenSource(externalCancellationToken, timeoutToken);

				var url = $"{_configuration.PathPrefix}/{nameof(KurirPozicijeZaPraznjenje)}/?{nameof(KurirPozicijeZaPraznjenje.SifraBenzinskeStanice)}={_lockerId}&{nameof(KurirPozicijeZaPraznjenje.SifraKurira)}={courierCode}";
				_logger?.LogInformation($"{nameof(GetExpiredPackagesForPickup)}: GET {url}");
				var response = await ServiceCall(ct => _serviceHttpClient.GetAsync(url, ct), true, linkedToken.Token).ConfigureAwait(false);
				if (response == null) {
					return (null, null);
				}
				var packagesResponse = await JsonApiSerialization.DeserializeAsync<KurirPozicijeZaPraznjenjeResponse>(response.Content.ReadAsStream(externalCancellationToken), externalCancellationToken)
																	.ConfigureAwait(false);
				if (response.StatusCode != HttpStatusCode.OK) { 
					_logger?.LogError($"{nameof(GetExpiredPackagesForPickup)}: [{response.StatusCode}] {packagesResponse?.ResponseStatus?.Message}");
					return (null, packagesResponse?.ResponseStatus?.Message);
				}
				return (packagesResponse?.ListaPaketa, null);
			}
			catch (Exception ex) {
				_logger?.LogError($"{nameof(GetExpiredPackagesForPickup)} exception: {ex.Message}");
				return (null, null);
			}
		}

		public async ValueTask<(bool, string?)> ConfirmExpiredPackagePickup(string courierCode, string packageBarcode, int doorNumber, CancellationToken externalCancellationToken)
		{
			try
			{
				using var cts = new CancellationTokenSource();
				cts.CancelAfter(_configuration.RequestTimeout);
				var timeoutToken = cts.Token;

				using var linkedToken = CancellationTokenSource.CreateLinkedTokenSource(externalCancellationToken, timeoutToken);

				var url = $"{_configuration.PathPrefix}/{nameof(KurirPraznjenjePozicijePotvrda)}/";
				_logger?.LogInformation($"{nameof(ConfirmExpiredPackagePickup)}: POST {url} (barkod={packageBarcode}, brojPozicije={doorNumber})");
				var json = JsonApiSerialization.Serialize(new KurirPraznjenjePozicijePotvrda { SifraBenzinskeStanice = _lockerId, SifraKurira=courierCode, BrojPozicije=doorNumber, Barkod=packageBarcode });
				var content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json);
				var response = await ServiceCall(ct => _serviceHttpClient.PostAsync(url, content, ct), true, linkedToken.Token).ConfigureAwait(false);
				if (response == null) {
					return (false, null);	
				}
				var confirmResponse = await JsonApiSerialization.DeserializeAsync<KurirPraznjenjePozicijePotvrdaResponse>(response.Content.ReadAsStream(externalCancellationToken), externalCancellationToken)
																.ConfigureAwait(false);
				if (response.StatusCode != HttpStatusCode.OK) { 
					_logger?.LogError($"{nameof(ConfirmExpiredPackagePickup)}: [{response.StatusCode}] {confirmResponse?.ResponseStatus?.Message}");
					return (false, confirmResponse?.ResponseStatus?.Message);
				}

				return (confirmResponse?.IspraznjenBrojPozicije == doorNumber, null);
			}
			catch (Exception ex) {
				_logger?.LogError($"{nameof(ConfirmExpiredPackagePickup)} exception: {ex.Message}");
				return (false, null);
			}
		}

		public async ValueTask<(int?, string?)> CourierRequestPackageDeposit(string courierCode, string packageBarcode, CancellationToken externalCancellationToken)
		{
			try
			{
				using var cts = new CancellationTokenSource();
				cts.CancelAfter(_configuration.RequestTimeout);
				var timeoutToken = cts.Token;

				using var linkedToken = CancellationTokenSource.CreateLinkedTokenSource(externalCancellationToken, timeoutToken);

				var url = $"{_configuration.PathPrefix}/{nameof(KurirPunjenjePozicije)}/?{nameof(KurirPunjenjePozicije.SifraBenzinskeStanice)}={_lockerId}&{nameof(KurirPunjenjePozicije.SifraKurira)}={courierCode}&{nameof(KurirPunjenjePozicije.Barkod)}={packageBarcode}";
				_logger?.LogInformation($"{nameof(CourierRequestPackageDeposit)}: GET {url}");
				var response = await ServiceCall(ct => _serviceHttpClient.GetAsync(url, ct), true, linkedToken.Token).ConfigureAwait(false);
				if (response == null) {
					return (null, null);
				}
				var depositResponse = await JsonApiSerialization.DeserializeAsync<KurirPunjenjePozicijeResponse>(response.Content.ReadAsStream(externalCancellationToken), externalCancellationToken)
																.ConfigureAwait(false);
				if (response.StatusCode != HttpStatusCode.OK) { 
					_logger?.LogError($"{nameof(CourierRequestPackageDeposit)}: [{response.StatusCode}] {depositResponse?.ResponseStatus?.Message}");
					return (null, depositResponse?.ResponseStatus?.Message);
				}
				return (depositResponse?.BrojPozicije >= 1 ? depositResponse.BrojPozicije : null, null);
			}
			catch (Exception ex) {
				_logger?.LogError($"{nameof(CourierRequestPackageDeposit)} exception: {ex.Message}");
				return (null, null);
			}
		}

		/// <summary>
		/// Potvrda ostavljanja. Ishod razlikuje „sigurno nije upisano" (<see cref="DepositConfirmResult.NotDelivered"/>) od „ne
		/// zna se" (<see cref="DepositConfirmResult.Unknown"/>): posle prvog kurir sme da uzme paket nazad, posle drugog paket
		/// ostaje u vratima. Ponovljena potvrda za ista vrata je bezbedna (de-test, 2026-10-09). Poruka servera samo uz
		/// <see cref="DepositConfirmResult.Rejected"/> (400).
		/// </summary>
		public async ValueTask<(DepositConfirmResult, string?)> CourierPackageDepositConfirm(string courierCode, string packageBarcode, int doorNumber, CancellationToken externalCancellationToken)
		{
			var url = $"{_configuration.PathPrefix}/{nameof(KurirPunjenjePozicijePotvrda)}/";
			// zahtev van ServiceCall-a: ProcessingTrackingHandler na njemu beleži da li ga je server možda obradio
			using var request = new HttpRequestMessage(HttpMethod.Post, url);
			HttpResponseMessage? response = null;
			try
			{
				using var cts = new CancellationTokenSource();
				cts.CancelAfter(_configuration.RequestTimeout);
				var timeoutToken = cts.Token;

				using var linkedToken = CancellationTokenSource.CreateLinkedTokenSource(externalCancellationToken, timeoutToken);

				_logger?.LogInformation($"{nameof(CourierPackageDepositConfirm)}: POST {url} (barkod={packageBarcode}, brojPozicije={doorNumber})");
				var json = JsonApiSerialization.Serialize(new KurirPunjenjePozicijePotvrda { SifraBenzinskeStanice = _lockerId, SifraKurira=courierCode, Barkod=packageBarcode, BrojPozicije=doorNumber });
				request.Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json);
				response = await ServiceCall(ct => _serviceHttpClient.SendAsync(request, ct), true, linkedToken.Token).ConfigureAwait(false);
				if (response == null) {
					return (DepositConfirmResult.NotDelivered, null);	// otkazano pre slanja
				}
				if (response.StatusCode != HttpStatusCode.OK) {
					var errorMessage = await ReadErrorMessage(response, externalCancellationToken).ConfigureAwait(false);
					_logger?.LogError($"{nameof(CourierPackageDepositConfirm)}: [{response.StatusCode}] {errorMessage}");
					return response.StatusCode == HttpStatusCode.BadRequest
							? (DepositConfirmResult.Rejected, errorMessage)
							: (NotDeliveredOrUnknown(request), null);
				}
				var confirmResponse = await ReadConfirmResponse<KurirPunjenjePozicijePotvrdaResponse>(response, r => r.PopunjenBrojPozicije > 0, nameof(CourierPackageDepositConfirm), externalCancellationToken)
																.ConfigureAwait(false);
				if (confirmResponse == null) {
					return (DepositConfirmResult.Unknown, null);
				}
				if (confirmResponse.PopunjenBrojPozicije != doorNumber) {
					// server je odgovorio, ponovno slanje ne menja odgovor; naša vrata ne vodi kao zauzeta
					_logger?.LogError($"{nameof(CourierPackageDepositConfirm)}: server je potvrdio poziciju {confirmResponse.PopunjenBrojPozicije}, a paket je u vratima {doorNumber}");
					return (DepositConfirmResult.Rejected, null);
				}
				return (DepositConfirmResult.Confirmed, null);
			}
			catch (Exception ex) {
				_logger?.LogError($"{nameof(CourierPackageDepositConfirm)} exception: {ex.Message}");
				// Izuzetak posle odgovora (npr. čitanje tela 200) ili posle pokušaja koji je server možda obradio: ne zna se.
				return (response != null ? DepositConfirmResult.Unknown : NotDeliveredOrUnknown(request), null);
			}
		}

		private static DepositConfirmResult NotDeliveredOrUnknown(HttpRequestMessage request)
			=> ProcessingTrackingHandler.MayHaveBeenProcessed(request) ? DepositConfirmResult.Unknown : DepositConfirmResult.NotDelivered;

		public async ValueTask<(Velicine?, string?)> CourierUpdatePackageSize(string courierCode, string packageBarcode, CancellationToken externalCancellationToken)
		{
			try
			{
				using var cts = new CancellationTokenSource();
				cts.CancelAfter(_configuration.RequestTimeout);
				var timeoutToken = cts.Token;

				using var linkedToken = CancellationTokenSource.CreateLinkedTokenSource(externalCancellationToken, timeoutToken);

				var url = $"{_configuration.PathPrefix}/{nameof(KurirPromenaVelicinePosiljke)}/";
				_logger?.LogInformation($"{nameof(CourierUpdatePackageSize)}: POST {url} (barkod={packageBarcode})");
				var json = JsonApiSerialization.Serialize(new KurirPromenaVelicinePosiljke { SifraBenzinskeStanice = _lockerId, SifraKurira=courierCode, Barkod=packageBarcode });
				var content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json);
				var response = await ServiceCall(ct => _serviceHttpClient.PostAsync(url, content, ct), true, linkedToken.Token).ConfigureAwait(false);
				if (response == null) {
					return (null, null);
				}
				var updateResponse = await JsonApiSerialization.DeserializeAsync<KurirPromenaVelicinePosiljkeResponse>(response.Content.ReadAsStream(externalCancellationToken), externalCancellationToken)
																.ConfigureAwait(false);
				if (response.StatusCode != HttpStatusCode.OK) { 
					_logger?.LogError($"{nameof(CourierUpdatePackageSize)}: [{response.StatusCode}] {updateResponse?.ResponseStatus?.Message}");
					return (null, updateResponse?.ResponseStatus?.Message);
				}
				return (updateResponse?.NovaVelicina, null);
			}
			catch (Exception ex) {
				_logger?.LogError($"{nameof(CourierUpdatePackageSize)} exception: {ex.Message}");
				return (null, null);
			}
		}

		/// <summary>Provera preuzimanja za lokalni tok: broj vrata i iznos kad je server odgovorio 200, inače poruka servera ako je ima.</summary>
		public async ValueTask<(int?, decimal?, string?)> RecipientRequestPackagePickup(string telephoneNumber, int unlockCode, CancellationToken externalCancellationToken)
		{
			var (result, doorNumber, amount, serverMessage) = await RecipientRequestPackagePickupDetailed(telephoneNumber, unlockCode, externalCancellationToken)
																		.ConfigureAwait(false);
			return result == RecipientPickupLookupResult.Found ? (doorNumber, amount, null) : (null, null, serverMessage);
		}

		/// <summary>
		/// Provera preuzimanja sa ishodom (<see cref="RecipientPickupLookupResult"/>). <c>ServerMessage</c> je poruka iz odgovora sa
		/// greškom (i kad status nije 400).
		/// </summary>
		public async ValueTask<(RecipientPickupLookupResult Result, int? DoorNumber, decimal? Amount, string? ServerMessage)> RecipientRequestPackagePickupDetailed(
			string telephoneNumber, int unlockCode, CancellationToken externalCancellationToken)
		{
			try
			{
				using var cts = new CancellationTokenSource();
				cts.CancelAfter(_configuration.RequestTimeout);
				var timeoutToken = cts.Token;

				using var linkedToken = CancellationTokenSource.CreateLinkedTokenSource(externalCancellationToken, timeoutToken);

				var url = $"{_configuration.PathPrefix}/{nameof(PrimaocPraznjenjePozicije)}/?{nameof(PrimaocPraznjenjePozicije.SifraBenzinskeStanice)}={_lockerId}&{nameof(PrimaocPraznjenjePozicije.TelefonPrimaoca)}={telephoneNumber}&{nameof(PrimaocPraznjenjePozicije.KodZaOtvaranje)}={unlockCode}";
				_logger?.LogInformation($"{nameof(RecipientRequestPackagePickup)}: GET {url}");
				var response = await ServiceCall(ct => _serviceHttpClient.GetAsync(url, ct), true, linkedToken.Token).ConfigureAwait(false);
				if (response == null) {
					return (RecipientPickupLookupResult.NoResponse, null, null, null);	// otkazano pre slanja
				}
				if (response.StatusCode != HttpStatusCode.OK) {
					var errorMessage = await ReadErrorMessage(response, externalCancellationToken).ConfigureAwait(false);
					_logger?.LogError($"{nameof(RecipientRequestPackagePickup)}: [{response.StatusCode}] {errorMessage}");
					return (IsRecoverable(response.StatusCode) ? RecipientPickupLookupResult.NoResponse : RecipientPickupLookupResult.Rejected, null, null, errorMessage);
				}
				var pickupResponse = await JsonApiSerialization.DeserializeAsync<PrimaocPraznjenjePozicijeResponse>(response.Content.ReadAsStream(externalCancellationToken), externalCancellationToken)
																.ConfigureAwait(false);
				if (pickupResponse == null) {
					_logger?.LogError($"{nameof(RecipientRequestPackagePickup)}: [{response.StatusCode}] odgovor nije ispravan (prazan JSON)");
					return (RecipientPickupLookupResult.NoResponse, null, null, null);
				}
				return (RecipientPickupLookupResult.Found, pickupResponse.BrojPozicije, pickupResponse.IznosZaUplatu, null);
			}
			catch (Exception ex) {
				_logger?.LogError($"{nameof(RecipientRequestPackagePickup)} exception: {ex.Message}");
				return (RecipientPickupLookupResult.NoResponse, null, null, null);
			}
		}

		public async ValueTask<(int?, string?, bool)> PaymentConfirmation(string unlockCode, string transactionNumber, decimal amount, string tidNumber, CancellationToken externalCancellationToken)
		{
			try 
			{
				using var cts = new CancellationTokenSource();
				cts.CancelAfter(_configuration.RequestTimeout);
				var timeoutToken = cts.Token;

				// POS ID is TID number
				// string posId = tidNumber;
				int posId = 1;          // zavareno
				int accountNo = 1;		// zavareno
				decimal cashAmount = 0;	// zavareno

				using var linkedToken = CancellationTokenSource.CreateLinkedTokenSource(externalCancellationToken, timeoutToken);

				var url = $"{_configuration.PathPrefix}/{nameof(PotvrdaPlacanja)}/";
				_logger?.LogInformation($"{nameof(PotvrdaPlacanja)}: POST {url} (" +
										$"{nameof(PotvrdaPlacanja.KodZaOtvaranje)}={unlockCode}, " +
										$"{nameof(PotvrdaPlacanja.PosId)}={posId}, " +
										$"{nameof(PotvrdaPlacanja.PosTransakcijaId)}={transactionNumber}, " +
										$"{nameof(PotvrdaPlacanja.BrojRacuna)}={accountNo}, " +
										$"{nameof(PotvrdaPlacanja.IznosGotovina)}={cashAmount:F2}, " +
										$"{nameof(PotvrdaPlacanja.IznosKartice)}={amount:F2})");

				var json = JsonApiSerialization.Serialize(new PotvrdaPlacanja { SifraBenzinskeStanice = _lockerId,
																				KodZaOtvaranje=unlockCode,
																				PosId= posId,
																				PosTransakcijaId= transactionNumber, 
																				BrojRacuna= accountNo,
																				IznosGotovina= cashAmount,
																				IznosKartice= amount });

				var content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json);
				var response = await ServiceCall(ct => _serviceHttpClient.PostAsync(url, content, ct), true, linkedToken.Token).ConfigureAwait(false);
				if(response == null) {
					return (null, null, true);
				}
				if(response.StatusCode != HttpStatusCode.OK) {
					var errorMessage = await ReadErrorMessage(response, externalCancellationToken).ConfigureAwait(false);
					_logger?.LogError($"{nameof(PaymentConfirmation)}: [{response.StatusCode}] {errorMessage}");
					return (null, errorMessage, IsRecoverable(response.StatusCode));
				}

				var paymentConfirmationResponse = await JsonApiSerialization.DeserializeAsync<PotvrdaPlacanjaResponse>(response.Content.ReadAsStream(externalCancellationToken), externalCancellationToken)
																.ConfigureAwait(false);
				return (paymentConfirmationResponse?.EvidencioniBrojUplate, null, false);
			}
			catch (Exception ex) {
				_logger?.LogError($"{nameof(PaymentConfirmation)} exception: {ex.Message}");
				return (null, null, IsRecoverable(ex));
			}
		}

		/// <summary>Vraća (potvrđeno, poruka servera, prolazna greška — vidi <see cref="IsRecoverable(HttpStatusCode)"/>).</summary>
		public async ValueTask<(bool, string?, bool)> RecipientPackagePickupConfirm(int unlockCode, string telephoneNumber, int doorNumber, CancellationToken externalCancellationToken)
		{
			try
			{
				using var cts = new CancellationTokenSource();
				cts.CancelAfter(_configuration.RequestTimeout);
				var timeoutToken = cts.Token;

				using var linkedToken = CancellationTokenSource.CreateLinkedTokenSource(externalCancellationToken, timeoutToken);

				var url = $"{_configuration.PathPrefix}/{nameof(PrimaocPraznjenjePozicijePotvrda)}/";
				_logger?.LogInformation($"{nameof(RecipientPackagePickupConfirm)}: POST {url} (kod={unlockCode}, telefon={telephoneNumber}, pozicija={doorNumber})");
				var json = JsonApiSerialization.Serialize(new PrimaocPraznjenjePozicijePotvrda { SifraBenzinskeStanice = _lockerId, TelefonPrimaoca=telephoneNumber, KodZaOtvaranje=unlockCode, BrojPozicije=doorNumber });
				var content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json);
				var response = await ServiceCall(ct => _serviceHttpClient.PostAsync(url, content, ct), true, linkedToken.Token).ConfigureAwait(false);
				if (response == null) {
					return (false, null, true);
				}
				if (response.StatusCode != HttpStatusCode.OK) {
					var errorMessage = await ReadErrorMessage(response, externalCancellationToken).ConfigureAwait(false);
					_logger?.LogError($"{nameof(RecipientPackagePickupConfirm)}: [{response.StatusCode}] {errorMessage}");
					return (false, errorMessage, IsRecoverable(response.StatusCode));
				}
				var confirmResponse = await ReadConfirmResponse<PrimaocPraznjenjePozicijePotvrdaResponse>(response, r => r.BrojPozicije > 0, nameof(RecipientPackagePickupConfirm), externalCancellationToken)
																.ConfigureAwait(false);
				if (confirmResponse == null) {
					return (false, null, true);
				}
				// 200 sa drugom pozicijom: server je odgovorio, ponovno slanje ne menja odgovor
				return (confirmResponse.BrojPozicije == doorNumber, null, false);
			}
			catch (Exception ex) {
				_logger?.LogError($"{nameof(RecipientPackagePickupConfirm)} exception: {ex.Message}");
				return (false, null, IsRecoverable(ex));
			}
		}

		public async ValueTask<(StatusPaketomata?, string?)> GetLockerBoxesAvailability(CancellationToken externalCancellationToken)
		{
			try
			{
				using var cts = new CancellationTokenSource();
				cts.CancelAfter(_configuration.RequestTimeout);
				var timeoutToken = cts.Token;

				using var linkedToken = CancellationTokenSource.CreateLinkedTokenSource(externalCancellationToken, timeoutToken);

				// No path prefix!
				var url = $"/KurirskaSluzba/StatusPaketomata/?SifraBenzinskeStanice={_lockerId}";
				_logger?.LogInformation($"{nameof(GetLockerBoxesAvailability)}: GET {url}");
				var response = await ServiceCall(ct => _serviceHttpClient.GetAsync(url, ct), true, linkedToken.Token).ConfigureAwait(false);
				if (response == null) {
					return (null, null);
				}
                // Unlike all other endpoint, this one has a non-standard response format.
                var jsonResponse = await response.Content.ReadAsStringAsync(externalCancellationToken).ConfigureAwait(false);
				var availabilityResponse = JsonApiSerialization.Deserialize<StatusPaketomata>(jsonResponse);
				if (response.StatusCode != HttpStatusCode.OK) { 
					var errorResponse = JsonApiSerialization.Deserialize<ResponseBase>(jsonResponse);
					_logger?.LogError($"{nameof(GetLockerBoxesAvailability)}: [{response.StatusCode}] {errorResponse?.ResponseStatus?.Message}");
					return (null, errorResponse?.ResponseStatus?.Message);
				}
				return (availabilityResponse, null);
			}
			catch (Exception ex) {
				_logger?.LogError($"{nameof(GetLockerBoxesAvailability)} exception: {ex.Message}");
				return (null, null);
			}
		}

		public async ValueTask<(bool, string?)> ReportLockerErrorState(VrstaGreskePaketomat errorKind, CancellationToken externalCancellationToken)
		{
			try
			{
				using var cts = new CancellationTokenSource();
				cts.CancelAfter(_configuration.RequestTimeout);
				var timeoutToken = cts.Token;

				using var linkedToken = CancellationTokenSource.CreateLinkedTokenSource(externalCancellationToken, timeoutToken);

				var url = $"{_configuration.PathPrefix}/{nameof(PrijavaGreskePaketomata)}/";
				_logger?.LogInformation($"{nameof(ReportLockerErrorState)}: POST {url} (greska={errorKind})");
				var json = JsonApiSerialization.Serialize(new PrijavaGreskePaketomata { SifraBenzinskeStanice = _lockerId, Greska = errorKind, DatumVremeGreske = DateTime.UtcNow });
				var content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json);
				var response = await ServiceCall(ct => _serviceHttpClient.PostAsync(url, content, ct), true, linkedToken.Token).ConfigureAwait(false);
				if (response == null) {
					return (false, null);
				}
				var reportResponse = await JsonApiSerialization.DeserializeAsync<PrijavaGreskePaketomataResponse>(response.Content.ReadAsStream(externalCancellationToken), externalCancellationToken)
																.ConfigureAwait(false);
				if (response.StatusCode != HttpStatusCode.OK) { 
					_logger?.LogError($"{nameof(ReportLockerErrorState)}: [{response.StatusCode}] {reportResponse?.ResponseStatus?.Message}");
                    return IsLockerInErrorStateResponse(response.StatusCode, reportResponse) ?
                                                        (true, null) : (false, reportResponse?.ResponseStatus?.Message);
				}
				return (reportResponse?.SifraBenzinskeStanice == _lockerId, null);
			}
			catch (Exception ex) {
				_logger?.LogError($"{nameof(ReportLockerErrorState)} exception: {ex.Message}");
				return (false, null);
			}
		}

		public async ValueTask<(bool, string?)> ClearLockerErrorState(CancellationToken externalCancellationToken)
		{
			try
			{
				using var cts = new CancellationTokenSource();
				cts.CancelAfter(_configuration.RequestTimeout);
				var timeoutToken = cts.Token;

				using var linkedToken = CancellationTokenSource.CreateLinkedTokenSource(externalCancellationToken, timeoutToken);

				var url = $"{_configuration.PathPrefix}/{nameof(PonistavanjeGreskePaketomata)}";
				_logger?.LogInformation($"{nameof(ClearLockerErrorState)}: POST {url}");
				var content = JsonContent.Create(new PonistavanjeGreskePaketomata { SifraBenzinskeStanice = _lockerId} );
				var response = await ServiceCall(ct => _serviceHttpClient.PostAsync(url, content, ct), true, linkedToken.Token).ConfigureAwait(false);
				if (response == null) {
					return (false, null);
				}
				var clearResponse = await JsonApiSerialization.DeserializeAsync<PonistavanjeGreskePaketomataResponse>(response.Content.ReadAsStream(externalCancellationToken), externalCancellationToken)
																.ConfigureAwait(false);
				if (response.StatusCode != HttpStatusCode.OK) {
                    _logger?.LogError($"{nameof(ClearLockerErrorState)}: [{response.StatusCode}] {clearResponse?.ResponseStatus?.Message}");
                    return IsLockerNotInErrorStateResponse(response.StatusCode, clearResponse)? 
														(true, null) : (false, clearResponse?.ResponseStatus?.Message);
				}
				return (clearResponse?.SifraBenzinskeStanice == _lockerId, null);
			}
			catch (Exception ex) {
				_logger?.LogError($"{nameof(ClearLockerErrorState)} exception: {ex.Message}");
				return (false, null);
			}
		}

		public async ValueTask<(bool, string?)> ReportDoorErrorState(int doorNumber, string errorCode, CancellationToken externalCancellationToken)
		{
			try
			{
				if (string.IsNullOrEmpty(errorCode) || !PrijavaGreskePozicije.SupportedErrorCodes.Any(code => code.ToLower() == errorCode.ToLower())) {	
					return (false, $"Unsupported error code: '{errorCode}'");
				}

				var cts = new CancellationTokenSource();
				cts.CancelAfter(_configuration.RequestTimeout);
				var timeoutToken = cts.Token;

				using var linkedToken = CancellationTokenSource.CreateLinkedTokenSource(externalCancellationToken, timeoutToken);

				var url = $"{_configuration.PathPrefix}/{nameof(PrijavaGreskePozicije)}/";
				_logger?.LogInformation($"{nameof(ReportDoorErrorState)}: POST {url} (pozicija={doorNumber}, greska={errorCode})");
				var json = JsonApiSerialization.Serialize(new PrijavaGreskePozicije { SifraBenzinskeStanice = _lockerId, BrojPozicije=doorNumber, GreskaPozicije=errorCode });
				var content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json);
				var response = await ServiceCall(ct => _serviceHttpClient.PostAsync(url, content, ct), true, linkedToken.Token).ConfigureAwait(false);
				if (response == null) {
					return (false, null);
				}
				var reportResponse = await JsonApiSerialization.DeserializeAsync<PrijavaGreskePozicijeResponse>(response.Content.ReadAsStream(externalCancellationToken), externalCancellationToken)
																.ConfigureAwait(false);
				if (response.StatusCode != HttpStatusCode.OK) { 
                    _logger?.LogError($"{nameof(ReportDoorErrorState)}: [{response.StatusCode}] {reportResponse?.ResponseStatus?.Message}");
					if (IsUnknownDoorReportResponse(response.StatusCode, reportResponse)) {
						LogUnknownDoor(nameof(ReportDoorErrorState), doorNumber);
						return (true, null);
					}
                    return IsDoorInErrorStateResponse(response.StatusCode, reportResponse) ?
                                                        (true, null) : (false, reportResponse?.ResponseStatus?.Message);
				}
				return (reportResponse?.BrojPozicije == doorNumber, null);
			}
			catch (Exception ex) {
				_logger?.LogError($"{nameof(ReportDoorErrorState)} exception: {ex.Message}");
				return (false, null);
			}
		}

		public async ValueTask<(bool, string?)> ClearDoorErrorState(int doorNumber, CancellationToken externalCancellationToken)
		{
			try
			{
				using var cts = new CancellationTokenSource();
				cts.CancelAfter(_configuration.RequestTimeout);
				var timeoutToken = cts.Token;

				using var linkedToken = CancellationTokenSource.CreateLinkedTokenSource(externalCancellationToken, timeoutToken);

				var url = $"{_configuration.PathPrefix}/{nameof(PonistavanjeGreskePozicije)}";
				_logger?.LogInformation($"{nameof(ClearDoorErrorState)}: POST {url} (pozicija={doorNumber})");
				var content = JsonContent.Create(new PonistavanjeGreskePozicije { SifraBenzinskeStanice = _lockerId, BrojPozicije = doorNumber } );
				var response = await ServiceCall(ct => _serviceHttpClient.PostAsync(url, content, ct), true, linkedToken.Token).ConfigureAwait(false);
				if (response == null) {
					return (false, null);	
				}
				var clearResponse = await JsonApiSerialization.DeserializeAsync<PonistavanjeGreskePozicijeResponse>(response.Content.ReadAsStream(externalCancellationToken), externalCancellationToken).ConfigureAwait(false);
				if (response.StatusCode != HttpStatusCode.OK) { 
					_logger?.LogError($"{nameof(ClearDoorErrorState)}: [{response.StatusCode}] {clearResponse?.ResponseStatus?.Message}");
					if (IsUnknownDoorClearResponse(response.StatusCode, clearResponse)) {
						LogUnknownDoor(nameof(ClearDoorErrorState), doorNumber);
						return (true, null);
					}
                    return IsDoorNotInErrorStateResponse(response.StatusCode, clearResponse) ?
                                                        (true, null) : (false, clearResponse?.ResponseStatus?.Message);
				}

				return (clearResponse?.BrojPozicije == doorNumber, null);
			}
			catch (Exception ex) {
				_logger?.LogError($"{nameof(ClearDoorErrorState)} exception: {ex.Message}");
				return (false, null);
			}
		}

        //{
        //    "responseStatus": {
        //        "errorCode": "ArgumentException",
        //        "message": "Paketomat na benzinskoj stanici \"111\" se nalazi u stanju greške",
        //        "errors": [
        //            {
        //                "errorCode": "ArgumentException",
        //                "fieldName": "SifraBenzinskeStanice",
        //                "message": "Paketomat na benzinskoj stanici \"111\" se nalazi u stanju greške"
        //            }
        //        ]
        //    }
        //}
        private static bool IsLockerInErrorStateResponse(HttpStatusCode httpStatusCode, PrijavaGreskePaketomataResponse? response)
			=> IsArgumentErrorResponse(httpStatusCode, response, "se nalazi u stanju greške");

        //{
        //    "responseStatus": {
        //        "errorCode": "ArgumentException",
        //        "message": "Paketomat na benzinskoj stanici \"111\" nije u stanju greške",
        //        "errors": [
        //            {
        //                "errorCode": "ArgumentException",
        //                "fieldName": "SifraBenzinskeStanice",
        //                "message": "Paketomat na benzinskoj stanici \"111\" nije u stanju greške"
        //            }
        //        ]
        //    }
        //}
        private static bool IsLockerNotInErrorStateResponse(HttpStatusCode httpStatusCode, PonistavanjeGreskePaketomataResponse? response)
			=> IsArgumentErrorResponse(httpStatusCode, response, "nije u stanju greške");
		
        //{
        //    "responseStatus": {
        //        "errorCode": "ArgumentException",
        //        "message": "Pozicija \"12\" je već u zahtevanom statusu",
        //        "errors": [
        //            {
        //                "errorCode": "ArgumentException",
        //                "fieldName": "BrojPozicije",
        //                "message": "Pozicija \"12\" je već u zahtevanom statusu"
        //            }
        //        ]
        //    },
        //    "brojPozicije": 0
        //}           
        private static bool IsDoorInErrorStateResponse(HttpStatusCode httpStatusCode, PrijavaGreskePozicijeResponse? response)
			=> IsArgumentErrorResponse(httpStatusCode, response, "je već u zahtevanom statusu");

        //{
        //    "responseStatus": {
        //        "errorCode": "ArgumentException",
        //        "message": "Pozicija \"12\" je već bez greške",
        //        "errors": [
        //            {
        //                "errorCode": "ArgumentException",
        //                "fieldName": "BrojPozicije",
        //                "message": "Pozicija \"12\" je već bez greške"
        //            }
        //        ]
        //    },
        //    "brojPozicije": 0
        //}
        private static bool IsDoorNotInErrorStateResponse(HttpStatusCode httpStatusCode, PonistavanjeGreskePozicijeResponse? response)
			=> IsArgumentErrorResponse(httpStatusCode, response, "je već bez greške");

		// Pozicija koju server ne poznaje (de-test, 3.11.2026; fieldName BrojPozicije):
		//   PrijavaGreskePozicije      → "Na paketomatu \"111\" ne postoji broj pozicije \"999\""
		//   PonistavanjeGreskePozicije → "Na paketomatu \"111\" ne postoji navedeni broj pozicije \"999\""
		// Npr. Locker.LockerBoxCount veći od broja pozicija na serveru. Ponovno slanje ne menja odgovor, a stanje vrata koje
		// čeka slanje zadržava prijavu kvara paketomata (LockerStatusUpdater), pa se odgovor tretira kao primljen.
		// Paketomat u stanju greške server proverava pre broja pozicije („se nalazi u stanju greške") — to nije ovaj odgovor.
		private static bool IsUnknownDoorReportResponse(HttpStatusCode httpStatusCode, PrijavaGreskePozicijeResponse? response)
			=> IsArgumentErrorResponse(httpStatusCode, response, "ne postoji broj pozicije");

		private static bool IsUnknownDoorClearResponse(HttpStatusCode httpStatusCode, PonistavanjeGreskePozicijeResponse? response)
			=> IsArgumentErrorResponse(httpStatusCode, response, "ne postoji navedeni broj pozicije");

		private void LogUnknownDoor(string caller, int doorNumber)
			=> _logger?.LogError($"{caller}: server ne poznaje poziciju {doorNumber} — stanje vrata se ne šalje ponovo; proveriti Locker.LockerBoxCount i pozicije paketomata na serveru");
       
        private static bool IsArgumentErrorResponse(HttpStatusCode httpStatusCode, ResponseBase? response, string messageKeyPart)
            => httpStatusCode == HttpStatusCode.BadRequest && response?.ResponseStatus != null &&
                    response.ResponseStatus.ErrorCode == "ArgumentException" &&
                    response.ResponseStatus.Message.Contains(messageKeyPart) &&
                    response.ResponseStatus.Errors?.Count == 1;
    }
}
