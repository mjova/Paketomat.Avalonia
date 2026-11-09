using System.Net;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Flatsoft.Common.Text;
using Flatsoft.Net.Http;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Extensions.Http;

namespace TopPos.PenPosService
{
    public class PenPosServiceClient
    {
		private readonly PenPosServiceConfiguration _configuration;
		private readonly HttpClient _serviceHttpClient;
		private readonly ILogger? _logger;
        private readonly JsonSerializerOptions _jsonOptions;

        public PenPosServiceClient(PenPosServiceConfiguration configuration, ILogger? logger, CancellationToken cancellationToken)
        {
			_configuration = configuration;
			_logger = logger;

			_serviceHttpClient = CreateServiceHttpClient(logExcludeHeaders: ["Authorization"], logger);

             _jsonOptions = new() {
                 DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
             };
            _jsonOptions.Converters.Add(new ProcessorResultCodeJsonConverter());
        }

		private HttpClient CreateServiceHttpClient(IEnumerable<string>? logExcludeHeaders, ILogger? logger)
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


			var httpLoggingHandler = new HttpLoggingHandler(logger)
										{
											//LogFilter = invoiceRequest => invoiceRequest.RequestUri?.LocalPath != $"/{__loginPath}" // skip logging credentials
											ExcludeHeaders = logExcludeHeaders ?? Enumerable.Empty<string>()
										};
			var httpClient = HttpClientPipeline.Create(requestHandler, retryHandler, httpLoggingHandler);

			httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
			httpClient.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
			httpClient.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("deflate"));
			httpClient.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("br"));

			logger?.LogInformation($"PenPos HTTP klijent kreiran");

			return httpClient;
		}

        public bool TestMode { get; set; }

        private async ValueTask TryLogErrorDetails(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            if (response.IsSuccessStatusCode) {
                return;
            }
         
            try
            {
                var foo = await response.Content.ReadAsStringAsync(cancellationToken);
                var errorResponse = await JsonApiSerialization.DeserializeAsync<ErrorDetails>(response.Content.ReadAsStream(cancellationToken), cancellationToken, _jsonOptions)
                                                               .ConfigureAwait(false);
                if (errorResponse != null)
                {
                    var sb = new StringBuilder();
                    if (!string.IsNullOrWhiteSpace(errorResponse.Message)) {
                        sb.AppendLine($"{errorResponse.Message}");
                    }
                    if (errorResponse.ModelState?.Count > 0)
                    {
                        sb.AppendLine("Details:");
                        foreach (var detail in errorResponse.ModelState) {
                            sb.AppendLine($"- {detail}");
                        }
                    }
                    _logger?.LogError($"PenPos validation error: {sb.ToString()}");
                }
                else { 
                    _logger?.LogError($"Error details not available for the request failed with [{response.StatusCode}]");
                }
            }
            catch (Exception ex) {
                _logger?.LogError($"Failed to read response error details: {ex.Message}");
            }
        }

        public async ValueTask<ProcessorStatus?> GetProcessorStatus(CancellationToken externalCancellationToken)
		{
			try
			{
                if (this.TestMode) {
                    return TestProcessorStatus;
                }

                using var cts = new CancellationTokenSource();
                cts.CancelAfter(TimeSpan.FromMilliseconds(_configuration.RequestTimeout ?? 30000));
                var timeoutToken = cts.Token;

                using var linkedToken = CancellationTokenSource.CreateLinkedTokenSource(externalCancellationToken, timeoutToken);
				var statusEndpoint = $"{_configuration.BaseManagementUrl}/status";
                _logger?.LogInformation($"Getting PenPos processor status from {statusEndpoint}");
				var response = await _serviceHttpClient!.GetAsync(statusEndpoint, linkedToken.Token).ConfigureAwait(false);
				var body = await response.Content.ReadAsStreamAsync(externalCancellationToken).ConfigureAwait(false);
                var processorStatus = await JsonApiSerialization.DeserializeAsync<ProcessorStatus>(body, externalCancellationToken, _jsonOptions)
                                                                .ConfigureAwait(false);
                return processorStatus;
			}
			catch (Exception ex) {
				_logger?.LogError($"{nameof(PenPosServiceClient)}.{nameof(GetProcessorStatus)} exception: {ex.Message}");	
				return null;
			}
		}

        public async ValueTask<ProcessorConfiguration?> GetProcessorConfiguration(CancellationToken externalCancellationToken)
        {
            try
            {
                if (this.TestMode) {
                    return TestProcessorConfiguration;
                }

                var cts = new CancellationTokenSource();
                cts.CancelAfter(TimeSpan.FromMilliseconds(_configuration.RequestTimeout ?? 30000));
                var timeoutToken = cts.Token;

                using var linkedToken = CancellationTokenSource.CreateLinkedTokenSource(externalCancellationToken, timeoutToken);
                var configEndpoint = $"{_configuration.BaseManagementUrl}/config";
                _logger?.LogInformation($"Getting PenPos processor configuration from {configEndpoint}");
                var response = await _serviceHttpClient!.GetAsync(configEndpoint, linkedToken.Token).ConfigureAwait(false);
                var body = await response.Content.ReadAsStreamAsync(externalCancellationToken).ConfigureAwait(false);
                var processorConfiguration = await JsonApiSerialization.DeserializeAsync<ProcessorConfiguration>(body, externalCancellationToken)
                                                                .ConfigureAwait(false);
                return processorConfiguration;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"{nameof(PenPosServiceClient)}.{nameof(GetProcessorConfiguration)} exception: {ex.Message}");
                return null;
            }
        }

        public async ValueTask<ProcessorResultCode?> Initialize(CancellationToken externalCancellationToken)
        {
            try
            {
                if (this.TestMode) {
                    return ProcessorResultCode.PinOK;
                }

                using var cts = new CancellationTokenSource();
                cts.CancelAfter(TimeSpan.FromMilliseconds(_configuration.RequestTimeout ?? 30000));
                var timeoutToken = cts.Token;

                using var linkedToken = CancellationTokenSource.CreateLinkedTokenSource(externalCancellationToken, timeoutToken);

                var invoiceEndpoint = $"{_configuration.BaseManagementUrl}/pin";
                _logger?.LogInformation($"{nameof(ProcessInvoice)}: sending invoice to {invoiceEndpoint}");

                var content = new StringContent(_configuration.Pin, Encoding.UTF8, MediaTypeNames.Text.Plain);
                var response = await _serviceHttpClient.PostAsync(invoiceEndpoint, content, linkedToken.Token).ConfigureAwait(false);
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    _logger?.LogError($"{nameof(ProcessInvoice)}: [{response.StatusCode}]");
                    return null;
                }
                var responseContent = await response.Content.ReadAsStringAsync(externalCancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(responseContent))
                {
                    _logger?.LogError($"{nameof(PenPosServiceClient)}.{nameof(Initialize)}: Empty response from server.");
                    return null;
                }
                return new ProcessorResultCode(responseContent);
            }
            catch (Exception ex)
            {
                _logger?.LogError($"{nameof(PenPosServiceClient)}.{nameof(Initialize)} exception: {ex.Message}");
                return null;
            }
        }

        public async ValueTask<InvoiceResult?> ProcessInvoice(decimal paymentAmount, CancellationToken externalCancellationToken)
        {
            try
            {
                if (this.TestMode) {
                    return TestInvoiceResult;
                }

                using var cts = new CancellationTokenSource();
                cts.CancelAfter(TimeSpan.FromMilliseconds(_configuration.RequestTimeout ?? 30000));
                var timeoutToken = cts.Token;

                using var linkedToken = CancellationTokenSource.CreateLinkedTokenSource(externalCancellationToken, timeoutToken);

                var invoiceRequest = new InvoiceRequest() { 
					InvoiceType = InvoiceType.Normal,
					TransactionType = TransactionType.Sale,
					Cashier = _configuration.Cashier ?? "1",
                    Items = [new InvoiceItem() { 
                                    Gtin = _configuration.ItemName ?? "1", 
                                    Quantity = 1,
                                    UnitPrice = paymentAmount,
                                    Name = _configuration.ItemName,
                                    Labels = _configuration.TaxCategory?.Split(',').Select(s => s.Trim()).ToList() ?? null
                                }],
					Payment = [new PaymentItem() { Amount = paymentAmount, PaymentType=PaymentType.Card } ]
                };
                var invoiceEndpoint = $"{_configuration.BaseInvoiceUrl}/invoice";
                _logger?.LogInformation($"{nameof(ProcessInvoice)}: sending invoice to {invoiceEndpoint}");

                var json = JsonApiSerialization.Serialize(invoiceRequest, _jsonOptions);
                var content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json);
                var response = await _serviceHttpClient.PostAsync(invoiceEndpoint, content, linkedToken.Token).ConfigureAwait(false);
                if (response.StatusCode != HttpStatusCode.OK) { 
                	_logger?.LogError($"{nameof(ProcessInvoice)}: [{response.StatusCode}]");
                    await TryLogErrorDetails(response, externalCancellationToken).ConfigureAwait(false);
                    return null;
                }
                var invoiceResult = await JsonApiSerialization.DeserializeAsync<InvoiceResult>(response.Content.ReadAsStream(externalCancellationToken), externalCancellationToken, _jsonOptions)
                												.ConfigureAwait(false);
                return invoiceResult;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"{nameof(PenPosServiceClient)}.{nameof(ProcessInvoice)} exception: {ex.Message}");
                return null;
            }
        }

        public async ValueTask<bool> PrintFinancialReport(CancellationToken externalCancellationToken)
        {
            try
            {
                using var cts = new CancellationTokenSource();
                cts.CancelAfter(TimeSpan.FromMilliseconds(_configuration.RequestTimeout ?? 30000));
                var timeoutToken = cts.Token;

                using var linkedToken = CancellationTokenSource.CreateLinkedTokenSource(externalCancellationToken, timeoutToken);

                string url = "/print-finansijski-izvestaj";
                _logger?.LogInformation($"{nameof(PrintFinancialReport)}: POST {url}");
                //var json = JsonApiSerialization.Serialize(new KurirPraznjenjePozicijePotvrda { SifraBenzinskeStanice = _lockerId, SifraKurira=courierCode, BrojPozicije=doorNumber, Barkod=packageBarcode });
                var content = new StringContent("{}"/*json*/, Encoding.UTF8, MediaTypeNames.Application.Json);
                var response = await _serviceHttpClient.PostAsync(url, content, linkedToken.Token).ConfigureAwait(false);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"{nameof(PenPosServiceClient)}.{nameof(PrintFinancialReport)} exception: {ex.Message}");
                return false;
            }
        }

        private static readonly ProcessorStatus TestProcessorStatus = new ()
                                                {
                                                        IsPinRequired=false,
                                                        AuditRequired=false,
                                                        LastInvoiceNumber = "",
                                                        ProtocolVersion = "1.0.0.0",
                                                        SecureElementVersion = "1.1.0",
                                                        HardwareVersion = "0.0.0.0",
                                                        SoftwareVersion = "1.0",
                                                        DeviceSerialNumber = "12-0001-0E5D-55-D7A5RC1",
                                                        Make = "Digit Group d.o.o.",
                                                        Model = "DG-LPFR",
                                                        Mssc = [],
                                                        Gsc = [ProcessorResultCode.PinOK, ProcessorResultCode.InternetConnectionNotAvailable],
                                                        SdcDateTime = DateTime.Now,
                                                        SupportedLanguages = [ "sr-Cyrl-RS" ],
                                                        Uid = "J75XEFCF",
                                                        TaxCoreApi = "https://api.sandbox.suf.purs.gov.rs",
                                                        CurrentTaxRates = null,
                                                        AllTaxRates = null
                                                };

        private static readonly ProcessorConfiguration TestProcessorConfiguration = new ProcessorConfiguration()
                                                {
                                                    OrganizationTaxNumber = "102786854",
                                                    OrganizationName = "Digit Group d.o.o.",
                                                    OrganizationUnit = "Digit Group d.o.o.",
                                                    SecureElementNumber = "J75XEFCF",
                                                    Time = DateTime.Now,
                                                    LicenceNumber = "0E5D-55-D7A5",
                                                    VatPayer = true,
                                                    LocationId = "bb827f13a0504d0c87f1c627beaddd9d",
                                                    Environment = "5.8",
                                                    PinRequired = false,
                                                    PendingAuditPackages = 0,
                                                    LpfrVersion = "250528.1"
                                                };

        private static readonly InvoiceResult TestInvoiceResult = new ()
                                                {
                                                    SdcDateTime = DateTime.Now,
                                                    InvoiceNumber = "J75XEFCF-J75XEFCF-44",
                                                    TaxItems = [
                                                        new TaxItem
                                                        {
                                                            Label = "A",
                                                            Amount = 11.2227,
                                                            Rate = 10.0,
                                                            CategoryName = "VAT"
                                                        }
                                                    ],
                                                    VerificationUrl = "https://sandbox.suf.purs.gov.rs/v/?vl=A0o3NVhFRkNGSjc1WEVGQ0YsAAAAKwAAAETWEgAAAAAAAAABl5gDpMwAAAAhF93PpsOikrew%2F29Ym9mY1GTFQNnY%2B0gf62P9HSr7W1RpwO%2B0C8orxDVDhcRYM%2B9Xa3xOPoc6GKDtHkMytPX%2BInupJ9gz3FlhvQRqXQF5tMRwFLUOAr3I7OJ%2BvqnZ%2BkbaqxM5MqfpXwq4meU7UhIRMTPeOP%2BgafbrNikfguaQMrzARCikaCceIgWhKZJs8JoHkvkQUa0BnCAd3guMRKB5FhphmTABuh5I9PGytCTAFMkZJq81TXJ5W3FWbEmPK54kO1le%2B9V9Y19tfppWcSj2VmTC1pYdhlff0J3foqWjHVSPX%2Bt26pDgOqB6wvORmm%2BSdEwLPmZ8wp%2BiCEhrhOuiwKEGuCngR8mPWB4x36IFuGbexFTbdDikYOQ5bNTmfXembqVILz6EkRrGqM6QFxXD%2F3kvC%2FWggrf6gueu5tBar7cMpazwdQx0BG9GHe2Y1FDRgN7QvcHLxG1zTOfVAt8Fh%2B4zoQBagSz6b%2FrYK6MaZHiRqI6%2FmZ4prylHCF%2Fa3vEUp771r937Hg4TvYEDdM6%2F5FkZrppg68mhSz45W0sAULCr2wMDRCnd1Bx3JdXnNspCfT6UL73nqm9hq%2FVTreTEdiuiajgLp3vvczGlM%2BXy%2FKSTVpx1ZVuC5QeBjzF6opNdzm%2FgLi96pROF%2Bc7ZopYfnbHPdQDvc7Z2cXlfp8eTyGrD%2BQdn5ZH1jyObxPddXmc%3D",
                                                    TotalAmount = 123.45
                                                };
    }
}
