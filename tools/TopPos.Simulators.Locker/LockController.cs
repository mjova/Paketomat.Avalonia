using System.Buffers;
using System.Diagnostics;
using System.IO.Ports;
using Microsoft.Extensions.Logging;
using Flatsoft.Common;
using TopPos.LockController;
using Flatsoft.Net.Serial;
using TopPos.LockController.Serial;

namespace TopPos.LockerSimulator
{
	// Development tool. Used to simulate actual locker.
	public class LockController
	{
		private readonly SerialClient _serialClient;
		private readonly Func<LockCommand, LockResponse?> _funcMakeResponse;
		private readonly Task _taskProcessIncomingCommands;
		private readonly ILogger? _logger;

        public LockController(ComPortConfiguration configuration, 
									Func<LockCommand, LockResponse?> funcMakeResponse,	
									CancellationToken cancellationToken, 
									ILogger? logger = null)
        {
            _serialClient = new SerialClient(configuration.PortName, logger);
			BaudRate = configuration.BaudRate;
			DataBits = configuration.DataBits;
			StopBits = StopBits.One;	// TODO
			Parity = Enum.TryParse<Parity>(configuration.Parity, out var parity)? parity : Parity.Even;

			_serialClient.ReceiveTimeout = null;	// Infinite, wait for incoming commands

			_funcMakeResponse = funcMakeResponse;

			_logger = logger;
			_taskProcessIncomingCommands = ProcessIncomingCommands(cancellationToken);
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

		private async Task ProcessIncomingCommands(CancellationToken cancellationToken) {

			while (!cancellationToken.IsCancellationRequested)
			{
				try
				{
					if (!_serialClient.IsConnected) {
						await _serialClient.OpenAsync(cancellationToken).ConfigureAwait(false);
					}

					LockCommand? lockCommand = null;
					await _serialClient.Read(buffer =>
												{
													var processingResult = TryProcessLockCommand(buffer, out lockCommand);
													return processingResult;
												}, cancellationToken)
										.ConfigureAwait(false);
					if (lockCommand == null) {
						continue;
					}
				
					var response = _funcMakeResponse(lockCommand);
					if (response != null/* && DateTime.Now.Microsecond % 2 == 0*/) {	// Uncomment to enable "random" non-responsiveness from the controller
						await SendResponse(response, cancellationToken).ConfigureAwait(false);
					}
				}
				catch (Exception ex) {
				}
			}
		}

		private async ValueTask SendResponse(LockResponse lockResponse, CancellationToken cancellationToken)
		{
			int? retryCount = RetryCount;

			for (;;)
			{
				try
				{
					await _serialClient.Write(lockResponse.Data, cancellationToken).ConfigureAwait(false);
					// Simulate slow communication
					//await _serialClient.Write(lockResponse.Data.Slice(0, 1), cancellationToken).ConfigureAwait(false);
					//await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
					//await _serialClient.Write(lockResponse.Data.Slice(1), cancellationToken).ConfigureAwait(false);
					//await _serialClient.Write(lockResponse.Data.Slice(0, 8), cancellationToken).ConfigureAwait(false);
					//await _serialClient.Write(lockResponse.Data.Slice(8, 7), cancellationToken).ConfigureAwait(false);
					//await _serialClient.Write(lockResponse.Data.Slice(16, 8), cancellationToken).ConfigureAwait(false);
					return;
				}
				catch (Exception ex)
				{
					if (ex is TimeoutException)
					{
						if (!retryCount.HasValue || --retryCount == 0)
						{
							_logger?.LogWarning($"{nameof(LockController)}: exceeded maximum retry count trying to read response data. Request: {BitConverter.ToString(lockResponse.Data.ToArray(), 0, lockResponse.Data.Length)}");
							throw;
						}
						_logger?.LogWarning($"{nameof(LockController)}: retrying to read custom response data. Retries left: {retryCount}. Custom request: {BitConverter.ToString(lockResponse.Data.ToArray(), 0, lockResponse.Data.Length)}");
					}
					else
					{
						throw;
					}
				}
			}
		}

		
		private ResponseProcessingResult TryProcessLockCommand(ReadOnlySequence<byte> requestData, out LockCommand? command)
		{
			command = null;

			var r = new SequenceReader<byte>(requestData);

			// Skip any garbage data that might have arrived. Look for the text start marker. Also acceptable is the case where a matching text start
			// is found at the very last position in the buffer.
			bool responseFound = TryAdvanceTo(ref r, LockCommand.TextStart, out var skipped);
			if (!responseFound)
			{
				_logger?.LogDebug($"{nameof(TryProcessLockCommand)}: matching command not found. Searched {requestData.Length} byte(s).");
				return new ResponseProcessingResult { Consumed = requestData.GetPosition(requestData.Length), Examined = requestData.GetPosition(requestData.Length), StopReading = false };
			}

			// Check if we have enough data to parse a command (always 8 bytes)
			if (r.Remaining < Constants.CommandDataLength)
			{
				_logger?.LogDebug($"{nameof(TryProcessLockCommand)}: Not enough data. Expecting {Constants.CommandDataLength} bytes, currently available {r.Remaining}");
				return new ResponseProcessingResult { Consumed = requestData.GetPosition(skipped), Examined = requestData.GetPosition(requestData.Length), StopReading = false };
			}
			var data = requestData.Slice(skipped, Constants.CommandDataLength).ToArray();
			// if (OpenDoorSingleCommand.TryParse)
			var commandId = (CommandId)data[2];
			if (!Enum.IsDefined(commandId)) {
				return new ResponseProcessingResult { Consumed = requestData.GetPosition(skipped + Constants.CommandDataLength), 
													Examined = requestData.GetPosition(skipped + Constants.CommandDataLength),
													StopReading = false };
			}
			int doorNumber = data[1];
			switch (commandId)
			{
				case CommandId.OpenDoorSingle: 
					command = new OpenDoorSingleCommand(doorNumber);
					break;
				case CommandId.OpenDoorMultiple: 
					// Take care of the schizophrenic numbers (constructed using a module number, serialized as a door number)
					{ 
						var moduleNumber = (doorNumber / Constants.NumberOfDoorsPerController) + 1;
						command = new OpenDoorMultipleCommand(moduleNumber);
					}
					break;
				case CommandId.CheckDoorStatusSingle: 
					command = new CheckDoorStatusSingleCommand(doorNumber);
					break;
				case CommandId.CheckDoorStatusMultiple: 
					// Take care of the schizophrenic numbers (constructed using a module number, serialized as a door number)
					{ 
						var moduleNumber = (doorNumber / Constants.NumberOfDoorsPerController) + 1;
						command = new CheckDoorStatusMultipleCommand(moduleNumber);
					}
					break;
				default:
					Debug.Assert(false);
					break;
			}
			if (command != null) {
				return new ResponseProcessingResult { Consumed = requestData.GetPosition(skipped + Constants.CommandDataLength), 
													Examined = requestData.GetPosition(skipped + Constants.CommandDataLength),
													StopReading = true };
			}

			Debug.Assert(false);	// TODO: handle this command type
			return new ResponseProcessingResult { Consumed = requestData.GetPosition(requestData.Length), 
												Examined = requestData.GetPosition(requestData.Length),
												StopReading = false };
		}

		private static bool TryAdvanceTo(ref SequenceReader<byte> r, byte searchValue, out int skipped)
		{
			skipped = 0;
			while (r.TryRead(out var b))
			{
				if (b == searchValue) {
					r.Rewind(1);
					return true;
				}
				skipped++;
			}
			return false;
		}

		// High-level public operations
		public async ValueTask SendOpenDoorSingleResponse(int doorNumber, DoorStatusCode doorStatusCode, CancellationToken cancellationToken)
		{
			try
			{
				var response = new OpenDoorSingleResponse(doorNumber, doorStatusCode);
				await SendResponse(response, cancellationToken).ConfigureAwait(false);
			}
			catch (Exception ex) {
			}
		}
    }
}
