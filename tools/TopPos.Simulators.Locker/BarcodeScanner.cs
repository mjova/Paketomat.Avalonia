using System.Buffers;
using System.IO;
using System.IO.Ports;
using System.Text;
using Microsoft.Extensions.Logging;
using Flatsoft.Net.Serial;

namespace TopPos.LockerSimulator
{
	// Development tool. Used for sending barcodes entered in the simulator to a com port.
	// v4: IAsyncDisposable — gašenje zatvara port i završava petlju (simulator u testovima se pokreće i gasi u istom procesu).
	public class BarcodeScanner : IAsyncDisposable
	{
		private readonly CancellationTokenSource _cts = new();
		private readonly BarcodeScannerConfiguration _configuration;
		private readonly SerialClient _serialClient;
		private readonly Task _taskProcessScannerCommands;	// simulate expected responses
		private readonly ILogger? _logger;

		public const string CheckPresenceExpectedResponse = "\u0002\u00010000@GRDDUR5000\u0006;\u0003"; 

		public BarcodeScanner(BarcodeScannerConfiguration configuration, ILogger? logger = null)
		{
			_configuration = configuration;
			_serialClient = new SerialClient(_configuration.ComPort.PortName, logger);
			BaudRate = _configuration.ComPort.BaudRate;
			DataBits = _configuration.ComPort.DataBits;
			StopBits = StopBits.One;
			Parity = Enum.TryParse<Parity>(_configuration.ComPort.Parity, out var parity) ? parity : Parity.None;

			_serialClient.ReceiveTimeout = null;    // infinite; just wait for any commands
			// We are permanently processing incoming commands, so there's a permanent ongoing read operation.
			// SerialClient should not attempt to empty reader before writing, because it would raise InvalidOperationException (reading already in progress).
			_serialClient.EmptyReaderBeforeWrite = false;  
			_logger = logger;
			_taskProcessScannerCommands = ProcessIncomingScannerCommands(_cts.Token);
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

		public int? RetryCount { get; set; }

		public TimeSpan? SendTimeout
		{
			get => _serialClient.SendTimeout;
			set => _serialClient.SendTimeout = value;
		}

		public async ValueTask Send(string barcode, CancellationToken cancellationToken)
		{
			int? retryCount = RetryCount;
			for (; ; )
			{
				if (!_serialClient.IsConnected) {
					await _serialClient.OpenAsync(cancellationToken).ConfigureAwait(false);
				}

				try
				{
					if (_configuration.CarriageReturnBarcodeTermination) {
						barcode += '\r';
					}
					await _serialClient.Write(new ReadOnlyMemory<byte>(Encoding.ASCII.GetBytes(barcode)), cancellationToken).ConfigureAwait(false);
					return;
				}
				catch (Exception ex)
				{
					if (ex is TimeoutException)
					{
						if (!retryCount.HasValue || --retryCount == 0)
						{
							_logger?.LogWarning($"{nameof(BarcodeScanner)}: exceeded maximum retry count trying to send barcode. Barcode: {barcode}");
							throw;
						}
						_logger?.LogWarning($"{nameof(BarcodeScanner)}: retrying to send barcode {barcode}. Retries left: {retryCount}.");
					}
					else
					{
						throw;
					}
				}
			}
		}

		private async Task ProcessIncomingScannerCommands(CancellationToken cancellationToken) 
		{
			_logger?.LogInformation($"{nameof(BarcodeScanner)}[{_serialClient.Port}]: {nameof(ProcessIncomingScannerCommands)}: enter processing loop.");
			while (!cancellationToken.IsCancellationRequested)
			{
				try
				{
					if (!_serialClient.IsConnected) {
						_logger?.LogInformation($"{nameof(BarcodeScanner)}[{_serialClient.Port}]: opening port {_serialClient.Port}");
						await _serialClient.OpenAsync(cancellationToken).ConfigureAwait(false);
						_logger?.LogInformation($"{nameof(BarcodeScanner)}[{_serialClient.Port}]: port {_serialClient.Port} opened.");
					}
					bool sendCheckPresenceResponse = false;
					await _serialClient.Read(buffer =>
												{
													var processingResult = TryProcessCheckPresenceCommand(buffer, out bool isCheckPresenceCommand);
													sendCheckPresenceResponse = isCheckPresenceCommand;
													return processingResult;
												}, cancellationToken)
										.ConfigureAwait(false);
					if (sendCheckPresenceResponse) {
						await Send(BarcodeScanner.CheckPresenceExpectedResponse, cancellationToken).ConfigureAwait(false);
					}

				}
				catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
					break;	// v4: gašenje simulatora nije greška
				}
				catch (FileNotFoundException fex) {
					_logger?.LogError($"{nameof(ProcessIncomingScannerCommands)}[{_serialClient.Port}]: port opening failed. Details: {fex.Message}");
					return;
				}
				catch (Exception ex) {
					_logger?.LogError($"{nameof(ProcessIncomingScannerCommands)}[{_serialClient.Port}]: exception: {ex.Message}");
					await PauseAfterError(cancellationToken).ConfigureAwait(false);
				}
			}
		}

		/// <summary>v4: posle greške porta pauza, da petlja ne bi vrtela procesor dok port ne postoji.</summary>
		private static async Task PauseAfterError(CancellationToken cancellationToken)
		{
			try {
				await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException) { }
		}

		public async ValueTask DisposeAsync()
		{
			_cts.Cancel();
			await _serialClient.DisposeAsync().ConfigureAwait(false);	// prekida čitanje koje čeka komandu
			try {
				await _taskProcessScannerCommands.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
			}
			catch (Exception ex) when (ex is OperationCanceledException or TimeoutException) { }
			_cts.Dispose();
		}

		private ResponseProcessingResult TryProcessCheckPresenceCommand(ReadOnlySequence<byte> incomingData, out bool isCheckPresenceCommand)
		{
			// <STX><SOH>0000@GRDDUR5000<ACK>;<ETX>
			// '0000@GRDDUR5000;'

			isCheckPresenceCommand = false;
			var r = new SequenceReader<byte>(incomingData);	
			var command = Encoding.ASCII.GetString(r.UnreadSpan);
			isCheckPresenceCommand = command.Contains("@GRDDUR");
			return new ResponseProcessingResult { 
					Consumed = incomingData.GetPosition(isCheckPresenceCommand? incomingData.Length : 0), 
					Examined = incomingData.GetPosition(incomingData.Length),
					StopReading = isCheckPresenceCommand
				};
		}
	}
}