using System.Buffers;
using System.IO;
using System.IO.Ports;
using System.Text;
using Microsoft.Extensions.Logging;
using Flatsoft.Net.Serial;

namespace TopPos.Kiosk
{
	public class BarcodeScannerClient
	{
		private readonly BarcodeScannerConfiguration _configuration;
		private readonly SerialClient _serialClient;
		private readonly CancellationToken _externalCancellationToken;
		private Task? _taskProcessIncomingBarcodes;
		private CancellationTokenSource? _ctsAcceptBarcodes;
		private Func<string, ValueTask<bool>>? _processBarcode;
		private readonly object _lock = new object();
		private readonly SemaphoreSlim _syncBarcode;
		private CancellationTokenSource? _ctsSendBarcode;
		private StringBuilder _pendingBarcode;
		private readonly ILogger? _logger;

		public const string GoodReadDelayResponsePattern = "0000@GRDDUR";      

		public BarcodeScannerClient(BarcodeScannerConfiguration configuration, 
									ILogger? logger,
									CancellationToken cancellationToken)
			: this(configuration, processBarcode: null, logger, cancellationToken) 
			{ }

        public BarcodeScannerClient(BarcodeScannerConfiguration configuration, 
									Func<string, ValueTask<bool>>? processBarcode,
									ILogger? logger,
									CancellationToken cancellationToken)
        {
			_configuration = configuration;
            _serialClient = new SerialClient(configuration.ComPort.PortName, logger);
			// Ako je threshold > 1, onda ReadAtLeastAsync zadrzi jedan bajt (baš terminator CR)
			// to uzrokuje da se sledeće čitanje prebaci zapravo završi prethodno čitanje. Ukoliko se očitavaju 
			// isti kodovi uzastopno, onda problem neće biti primećen, međutim ako se čita drugi kod, to će učiniti
			// da se obradi prethodni kod, što je pogrešno. Zato je threshold = 1, da se odmah obradi svaki bajt i
			// da se ne zadrži rep.
			// Delimične okvire rešava Pipe preko Consumed/Examined u TryProcessBarcode.
			_serialClient.ReceivedBytesThreshold = 1;
			BaudRate = configuration.ComPort.BaudRate;
			DataBits = configuration.ComPort.DataBits;
			StopBits = StopBits.One;	// TODO
			Parity = Enum.TryParse<Parity>(configuration.ComPort.Parity, out var parity)? parity : Parity.Even;
			SendTimeout = TimeSpan.FromSeconds(3);	// TODO
			ReceiveTimeout = TimeSpan.FromSeconds(3);	// TODO
			this.CarriageReturnBarcodeTermination = _configuration.CarriageReturnBarcodeTermination;

			_processBarcode = processBarcode;
			_syncBarcode = new SemaphoreSlim(1, 1);
			_pendingBarcode = new StringBuilder();
			_externalCancellationToken = cancellationToken;

			_logger = logger;
			_logger?.LogInformation($"{nameof(BarcodeScannerClient)} inicijalizacija zavrsena.");
		}

		public int BaudRate
		{
			get => _serialClient.BaudRate;
			set => _serialClient.BaudRate = value;
		}

		public Parity Parity
		{
			get => _serialClient.Parity;
			set => _serialClient.Parity = value;
		}

		public int DataBits
		{
			get => _serialClient.DataBits;
			set => _serialClient.DataBits = value;
		}

		public StopBits StopBits
		{
			get => _serialClient.StopBits;
			set => _serialClient.StopBits = value;
		}

		public TimeSpan? SendTimeout 
		{ 
			get => _serialClient.SendTimeout; 
			set => _serialClient.SendTimeout = value;
		}

		public TimeSpan? ReceiveTimeout
		{
			get => _serialClient.ReceiveTimeout;
			set => _serialClient.ReceiveTimeout = value;
		}

		public int? RetryCount { get; set; }

		public bool CarriageReturnBarcodeTermination { get; set; }

		public bool IsInAcceptBarcodesMode => _taskProcessIncomingBarcodes != null && !_taskProcessIncomingBarcodes.IsCompleted;

		public Func<string, ValueTask<bool>>? ProcessBarcode 
		{ 
			get => _processBarcode; 
			set
			{
				lock (_lock) {
					_processBarcode = value;
					_pendingBarcode.Clear();
				}
			}
		}

		public Func<string, ValueTask>? OnBarcodeSent { get; set; }

		public void EnterCommandMode()
		{
			_ctsAcceptBarcodes?.Cancel();
			_ctsAcceptBarcodes = null;
			_taskProcessIncomingBarcodes = null;
			this.ReceiveTimeout = TimeSpan.FromSeconds(3);	// TODO
		}

		public void AcceptBarcodes()
		{
			if (_taskProcessIncomingBarcodes != null && !_taskProcessIncomingBarcodes.IsCompleted) {
				return;
			}
			_serialClient.ReceiveTimeout = null;    // Infinite, wait for incoming barcodes
			_ctsAcceptBarcodes?.Cancel();
			_ctsAcceptBarcodes = new CancellationTokenSource();
			var ctsLinked = CancellationTokenSource.CreateLinkedTokenSource(_externalCancellationToken, _ctsAcceptBarcodes.Token);
			_taskProcessIncomingBarcodes = ProcessIncomingBarcodes(ctsLinked.Token);
		}

		// Used in AcceptBarcodes mode
		private const int ReconnectDelayBaseMs = 2000;

		private async Task ProcessIncomingBarcodes(CancellationToken cancellationToken)
		{
			_logger?.LogInformation($"{nameof(BarcodeScannerClient)}[{_serialClient.Port}]: start petlje za procesiranje barkodova.");
			int reconnectDelayMs = 0;
			int reconnectDelayMaxMs = _configuration.ComPort.ReconnectMaxDelayMs;

			while (!cancellationToken.IsCancellationRequested)
			{
				try
				{
					if (!_serialClient.IsConnected) {
						if (reconnectDelayMs > 0) {
							_logger?.LogDebug($"{nameof(BarcodeScannerClient)}[{_serialClient.Port}]: cekanje {reconnectDelayMs}ms pre ponovnog pokusaja otvaranja porta.");
							await Task.Delay(reconnectDelayMs, cancellationToken).ConfigureAwait(false);
						}
						_logger?.LogInformation($"{nameof(BarcodeScannerClient)}[{_serialClient.Port}]: otvaranje serijskog porta {_serialClient.Port}");
						await _serialClient.OpenAsync(cancellationToken).ConfigureAwait(false);
						if (!_serialClient.IsConnected) {
							_logger?.LogDebug($"{nameof(BarcodeScannerClient)}[{_serialClient.Port}]: port nije otvoren.");
							reconnectDelayMs = reconnectDelayMs == 0
								? ReconnectDelayBaseMs
								: Math.Min(reconnectDelayMs * 2, reconnectDelayMaxMs);
							continue;
						}
						_logger?.LogInformation($"{nameof(BarcodeScannerClient)}[{_serialClient.Port}]: serijski port {_serialClient.Port} otvoren.");
					}

					reconnectDelayMs = 0;	// connected successfully, reset delay

					string? barcode = null;
					await _serialClient.Read(buffer =>
												{
													var processingResult = TryProcessBarcode(buffer, out barcode);
													return processingResult;
												}, cancellationToken)
										.ConfigureAwait(false);
					if (!string.IsNullOrEmpty(barcode)) {
						if (this.CarriageReturnBarcodeTermination) {
							barcode = barcode.Trim().Replace("\r", string.Empty);
							await SendBarcode(barcode).ConfigureAwait(false);
						}
						else {
							await DelaySendBarcode(barcode).ConfigureAwait(false);
						}
					}
				}
				catch (Exception ex) when (ex is not OperationCanceledException) {
					_logger?.LogError($"{nameof(ProcessIncomingBarcodes)}[{_serialClient.Port}]: exception: {ex.Message}");
					reconnectDelayMs = reconnectDelayMs == 0
						? ReconnectDelayBaseMs
						: Math.Min(reconnectDelayMs * 2, reconnectDelayMaxMs);
				}
			}
		}

		private async ValueTask SendBarcode(string barcode)
		{
			_logger?.LogInformation($"{nameof(BarcodeScannerClient)}[{_serialClient.Port}]: skenirani barkod: '{barcode}'");
			Func<string, ValueTask<bool>>? processBarcode = null;
			lock (_lock) {
				processBarcode = _processBarcode;
			}
			if (processBarcode != null)
			{
				await processBarcode(barcode).ConfigureAwait(false);
				var onBarcodeSent = this.OnBarcodeSent;
				if (onBarcodeSent != null) {
					await onBarcodeSent(barcode).ConfigureAwait(false);
                }
			}
		}

#if DEBUG
		/// <summary>
		/// DEBUG-only: simulira skeniran barkod isto kao da je došao sa serial port-a.
		/// Pozvati npr. iz F11 keyboard handler-a kad nemaš fizički skener.
		/// Prolazi kroz iste handler-e kao pravi scan (ProcessBarcode + OnBarcodeSent).
		/// </summary>
		public ValueTask SimulateBarcodeAsync(string barcode) => SendBarcode(barcode);
#endif

		private async ValueTask DelaySendBarcode(string partialBarcode)
		{
			// Čekanje pre try: otkazano čekanje ne preuzima semafor, pa finally ne sme da ga oslobodi.
			await _syncBarcode.WaitAsync(_externalCancellationToken).ConfigureAwait(false);
			try
			{
				_ctsSendBarcode?.Cancel();
				_ctsSendBarcode = new CancellationTokenSource();
				_pendingBarcode.Append(partialBarcode);
				_ = Task.Run(async () => 
							{ 
								bool lockAcquired = false;
								try 
								{ 
									var ctsLinked = CancellationTokenSource.CreateLinkedTokenSource(_externalCancellationToken, _ctsSendBarcode.Token);
									await Task.Delay(_configuration.ReadThresholdInMilliseconds, ctsLinked.Token).ConfigureAwait(false);
									await _syncBarcode.WaitAsync(ctsLinked.Token).ConfigureAwait(false);
									lockAcquired = true;
									if (_pendingBarcode.Length > 0) {
										await SendBarcode(_pendingBarcode.ToString()).ConfigureAwait(false);
										_pendingBarcode.Clear();
									}
								}
								catch (TaskCanceledException cex) {
									// Note: cancellation does not clear any pending barcode
									_logger?.LogDebug($"{nameof(DelaySendBarcode)}[{_serialClient.Port}]: {nameof(DelaySendBarcode)}: send task cancelled.");
								}
								catch (Exception ex) {
									_pendingBarcode.Clear();
									_logger?.LogError($"{nameof(DelaySendBarcode)}[{_serialClient.Port}]: exception: {ex.Message}");
								}
								finally {
									if (lockAcquired) { 
										_syncBarcode.Release();
									}
								}
							})
						.ConfigureAwait(false);
			}
			finally {
				_syncBarcode.Release();
			}
		}

		// Returns (<consumed/skipped>,<examined>) + <barcode|null>
		private ResponseProcessingResult TryProcessBarcode(ReadOnlySequence<byte> incomingData, out string? barcode)
		{
			barcode = null;

			var r = new SequenceReader<byte>(incomingData);

			// Check if we have enough data to consider it as barcode
			if (r.Remaining < _configuration.MinimumBarcodeLength)
			{
				_logger?.LogDebug($"{nameof(TryProcessBarcode)}: Not enough data. Expecting {_configuration.MinimumBarcodeLength} bytes, currently available {r.Remaining}");
				return new ResponseProcessingResult { Consumed = incomingData.GetPosition(0), Examined = incomingData.GetPosition(incomingData.Length), StopReading = false };
			}
			if (this.CarriageReturnBarcodeTermination) 
			{
				if (!r.TryReadTo(out ReadOnlySpan<byte> data, (byte)'\r')) { 
					_logger?.LogDebug($"{nameof(BarcodeScannerClient)}[{_serialClient.Port}]: {nameof(TryProcessBarcode)}: carriage return character not found. Searched {incomingData.Length} byte(s).");
					return new ResponseProcessingResult { Consumed = incomingData.GetPosition(0), Examined = incomingData.GetPosition(incomingData.Length), StopReading = false };
				}
				barcode = Encoding.ASCII.GetString(data);
				return new ResponseProcessingResult { Consumed = r.Position, Examined = r.Position, StopReading = true };
			}
			
			barcode = Encoding.ASCII.GetString(incomingData);
			return new ResponseProcessingResult { Consumed = incomingData.GetPosition(incomingData.Length), Examined = incomingData.GetPosition(incomingData.Length), StopReading = true };
		}

		// Used in command (send-receive) mode
		private async ValueTask SendReceive(ReadOnlyMemory<byte> scannerCommand, 
											Func<ReadOnlySequence<byte>, ResponseProcessingResult> responseProcessor,
											CancellationToken cancellationToken)
		{
			if (_taskProcessIncomingBarcodes != null) {
				throw new InvalidOperationException($"{nameof(BarcodeScannerClient)}: client is in {nameof(AcceptBarcodes)} mode. Call {nameof(EnterCommandMode)} to enter command-response mode.");
			}

			int? retryCount = RetryCount;
			for (;;)
			{
				try
				{
					if (!_serialClient.IsConnected) {
						_logger?.LogDebug($"{nameof(BarcodeScannerClient)}[{_serialClient.Port}]: {nameof(SendReceive)}: serial client is not connected. Opening port.");
						await _serialClient.OpenAsync(cancellationToken).ConfigureAwait(false);
					}
					
					await _serialClient.Write(scannerCommand, cancellationToken).ConfigureAwait(false);
					
					ResponseProcessingResult? processingResult = null;
					await _serialClient.Read(buffer => {
													processingResult = responseProcessor(buffer);
													return processingResult.Value;
												}, cancellationToken)
										.ConfigureAwait(false);
					if (processingResult?.StopReading == true) {
						return;
					}
				}
				catch (Exception ex)
				{
					if (ex is TimeoutException)
					{
						_logger?.LogDebug($"{nameof(BarcodeScannerClient)}[{_serialClient.Port}]: timeout exception: {ex.Message}");
						if (!retryCount.HasValue || --retryCount == 0)
						{
							_logger?.LogWarning($"{nameof(BarcodeScannerClient)}[{_serialClient.Port}]: exceeded maximum retry count trying to read response data. Command: {BitConverter.ToString(scannerCommand.ToArray(), 0, scannerCommand.Length)}");
							throw;
						}
						_logger?.LogWarning($"{nameof(BarcodeScannerClient)}[{_serialClient.Port}]: retrying to read custom response data. Retries left: {retryCount}. Command: {BitConverter.ToString(scannerCommand.ToArray(), 0, scannerCommand.Length)}");
					}
					else {
						_logger?.LogError($"{nameof(BarcodeScannerClient)}[{_serialClient.Port}]: exception: {ex.Message}");
						throw;
					}
				}
			}
		}
	


		public async Task<bool> CheckPresence(CancellationToken cancellationToken)
		{
			bool previousIsInAcceptBarcodesMode = IsInAcceptBarcodesMode;
			try {
				if(IsInAcceptBarcodesMode)
					EnterCommandMode();

				BarcodeCommand bcCmd = new BarcodeCommand("GRD", "DUR"); // Good Read Delay
				bool isPresent = false;
				await SendReceive(bcCmd.Data, 
									buffer => { 
										var processingResult = HandleGoodReadDelayResponse(buffer, out bool isValidResponse);
										isPresent = isValidResponse;
										return processingResult;
									},
									cancellationToken);
				if (previousIsInAcceptBarcodesMode)
					AcceptBarcodes();
				return isPresent;
			} catch (Exception ex) {
				_logger?.LogError($"{nameof(CheckPresence)}[{_serialClient.Port}]: exception: {ex.Message}");
				return false;
			}
			finally {
				if (previousIsInAcceptBarcodesMode) { 
					AcceptBarcodes();
				}
			}
		}

		private ResponseProcessingResult HandleGoodReadDelayResponse(ReadOnlySequence<byte> responseData, out bool isValidResponse)
		{
			// <STX><SOH>0000@GRDDUR5000<ACK>;<ETX>
			// '0000@GRDDUR5000;'

			isValidResponse = false;
			var r = new SequenceReader<byte>(responseData);
			// Search for [STX][SOH] (0x0201)
			if (!r.TryAdvanceTo(0x02) || !r.TryAdvanceTo(0x01))
			{
				return new ResponseProcessingResult { 
					Consumed = responseData.GetPosition(0), 
					Examined = responseData.GetPosition(responseData.Length),
					StopReading = false };
			}
			// A response is checked only after its bytes are all received, which is marked by the '<ACK>;<ETX>' sequence (0x063b03)
			if (!r.TryReadTo(out ReadOnlySpan<byte> response, 0x06) || 
				!r.TryRead(out var b1) || b1 != 0x3b ||
				!r.TryRead(out var b2) || b2 != 0x03)
			{
				return new ResponseProcessingResult { 
					Consumed = responseData.GetPosition(0), 
					Examined = responseData.GetPosition(responseData.Length),
					StopReading = false };
			}
				
			var commandResponse = Encoding.ASCII.GetString(response);
			isValidResponse = commandResponse.Contains(BarcodeScannerClient.GoodReadDelayResponsePattern);
			return new ResponseProcessingResult { 
					Consumed = responseData.GetPosition(isValidResponse? responseData.Length : 0), 
					Examined = responseData.GetPosition(responseData.Length),
					StopReading = isValidResponse
				};
		}
	}
}
