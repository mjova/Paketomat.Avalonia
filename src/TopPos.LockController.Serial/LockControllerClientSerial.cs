using System.Buffers;
using System.IO.Ports;
using Flatsoft.Common;
using Flatsoft.Net.Serial;
using Microsoft.Extensions.Logging;

namespace TopPos.LockController.Serial
{
	public class LockControllerClientSerial : ILockControllerClient
	{
		private readonly SerialClient _serialClient;
		// Jedna komanda (zahtev + odgovor) na portu u trenutku. Release samo ako je semafor preuzet (acquired):
		// otkazan WaitAsync ga ne preuzima, a Release bez preuzimanja pustio bi drugu komandu dok prethodna još traje.
		private readonly SemaphoreSlim _semaphore;
		private readonly ILogger? _logger;

		public LockControllerClientSerial(ComPortConfiguration configuration, ILogger? logger = null)
		{
			_serialClient = new SerialClient(configuration.PortName, logger);
			_semaphore = new SemaphoreSlim(1, 1);
			BaudRate = configuration.BaudRate;
			DataBits = configuration.DataBits;
			StopBits = StopBits.One;
			Parity = Enum.TryParse<Parity>(configuration.Parity, out var parity)? parity : Parity.None;
			// Uvek 1, bez obzira na konfiguraciju.
			// SerialClient prosleđuje ovu vrednost kao minimumBytes u ReadAtLeastAsync, pa svaka
			// vrednost > 1 ostavlja poslednji bajt odgovora zaglavljen u nezavršenom čitanju sve
			// dok ne istekne timeout (primer iz produkcije: 24-bajtni CheckDoorStatusMultiple
			// odgovor stigne kao 23 + 1, poslednji ETX se ne pročita, modul se prijavi kao nedostupan).
			ReceivedBytesThreshold = 1;
			if (configuration.ReceivedBytesThreshold is int configuredThreshold && configuredThreshold != 1) {
				logger?.LogWarning($"{nameof(LockControllerClientSerial)}: konfigurisani ReceivedBytesThreshold ({configuredThreshold}) se ignoriše, koristi se 1.");
			}
			RetryCount = configuration.RetryCount;
			_logger = logger;
			_logger?.LogInformation($"{nameof(LockControllerClientSerial)}: inicijalizacija zavrsena: {_serialClient.Port}, {_serialClient.BaudRate}, data bits {_serialClient.DataBits}, parity {_serialClient.Parity}, stop bits {_serialClient.StopBits}, received bytes threshold {_serialClient.ReceivedBytesThreshold}, send timeout {_serialClient.SendTimeout}, receive timeout {_serialClient.ReceiveTimeout}");
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

		public int ReceivedBytesThreshold
		{
			get => _serialClient.ReceivedBytesThreshold;
			set => _serialClient.ReceivedBytesThreshold = value;
		}

		public int? RetryCount { get; set; }

		/// <summary>
		/// Serial controller has 8 doors per module
		/// </summary>
		public int NumberOfDoorsPerController => Constants.NumberOfDoorsPerController;

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

		private ValueTask<LockResponse?> SendReceive(LockCommand lockCommand, CancellationToken cancellationToken)
			=> SendReceive(lockCommand, _serialClient.SendTimeout, _serialClient.ReceiveTimeout, cancellationToken);
		private ValueTask<LockResponse?> SendReceive(LockCommand lockCommand,
														TimeSpan? sendTimeout,
														TimeSpan? receiveTimeout,
														CancellationToken cancellationToken)
			=> SendReceive(lockCommand, sendTimeout, receiveTimeout, RetryCount, null, cancellationToken);
		// retryCount: null ili 1 = jedno slanje; onWrite se poziva pre svakog upisa komande na port (i pre ponovljenog).
		private async ValueTask<LockResponse?> SendReceive(LockCommand lockCommand,
																TimeSpan? sendTimeout,
																TimeSpan? receiveTimeout,
																int? retryCount,
																Action? onWrite,
																CancellationToken cancellationToken)
		{
			TimeSpan? currentSendTimeout = _serialClient.SendTimeout;
			TimeSpan? currentReceiveTimeout = _serialClient.ReceiveTimeout;
			try
			{
				SendTimeout = sendTimeout;
				ReceiveTimeout = receiveTimeout;
				_logger?.LogDebug($"{nameof(LockControllerClientSerial)}[{_serialClient.Port}]: {nameof(SendReceive)}: {nameof(SendTimeout)}: {SendTimeout}, {nameof(ReceiveTimeout)}: {ReceiveTimeout}");
				for (;;)
				{
					try
					{
						if (!_serialClient.IsConnected) {
							_logger?.LogDebug($"{nameof(LockControllerClientSerial)}[{_serialClient.Port}]: {nameof(SendReceive)}: serial client is not connected. Opening port.");
							await _serialClient.OpenAsync(cancellationToken).ConfigureAwait(false);
						}
						onWrite?.Invoke();
						await _serialClient.Write(lockCommand.Data, cancellationToken).ConfigureAwait(false);
						LockResponse? lockResponse = null;
						await _serialClient.Read(buffer => TryProcessLockResponse(lockCommand, buffer, out lockResponse), cancellationToken)
											.ConfigureAwait(false);
						return lockResponse;
					}
					catch (Exception ex)
					{
						if (ex is TimeoutException)
							{
							_logger?.LogDebug($"{nameof(LockControllerClientSerial)}[{_serialClient.Port}]: timeout exception: {ex.Message}");
							if (!retryCount.HasValue || --retryCount == 0)
								{
								_logger?.LogWarning($"{nameof(LockControllerClientSerial)}[{_serialClient.Port}]: exceeded maximum retry count trying to read response data. Request: {BitConverter.ToString(lockCommand.Data.ToArray(), 0, lockCommand.Data.Length)}");
								throw;
							}
							_logger?.LogWarning($"{nameof(LockControllerClientSerial)}[{_serialClient.Port}]: retrying to read custom response data. Retries left: {retryCount}. Custom request: {BitConverter.ToString(lockCommand.Data.ToArray(), 0, lockCommand.Data.Length)}");
						}
						else {
							_logger?.LogError($"{nameof(LockControllerClientSerial)}[{_serialClient.Port}]: exception: {ex.Message}");
							throw;
						}
					}
				}
			}
			finally
			{
				SendTimeout = currentSendTimeout;
				ReceiveTimeout = currentReceiveTimeout;
				_logger?.LogDebug($"{nameof(LockControllerClientSerial)}[{_serialClient.Port}]: timeouts restored to default values: {nameof(SendReceive)}: {nameof(SendTimeout)}: {SendTimeout}, {nameof(ReceiveTimeout)}: {ReceiveTimeout}");
			}
		}

		// Returns (<consumedPos/skipped>,<examined>) + <response|null>

		private ResponseProcessingResult TryProcessLockResponse(LockCommand lockCommand,
															  ReadOnlySequence<byte> responseData,
															  out LockResponse? response)
		{
			response = null;

			_logger?.LogDebug($"{nameof(LockControllerClientSerial)}[{_serialClient.Port}]: {nameof(TryProcessLockResponse)}: {responseData.Length} byte(s) received for the lock command [{lockCommand.Id}].");

			var reader = new SequenceReader<byte>(responseData);

			int sequenceLength = LockCommand.GetCommandResponseLength(lockCommand.Id);
			Span<byte> data = stackalloc byte[sequenceLength];

			while (reader.Remaining >= sequenceLength)
			{
				if (!reader.TryAdvanceTo(LockResponse.Acknowledge, advancePastDelimiter: false) ||
					reader.Remaining < sequenceLength)
				{
					break;
				}

				response = null;
				reader.Sequence.Slice(reader.Position, sequenceLength).CopyTo(data);
				if (lockCommand.Id == CommandId.OpenDoorSingle) {
					response = OpenDoorSingleResponse.TryParse(data);
				}
				else if(lockCommand.Id == CommandId.CheckDoorStatusSingle) {
					response = CheckDoorStatusSingleResponse.TryParse(data);
				}
				else if(lockCommand.Id == CommandId.Restart) {
					response = RestartModuleResponse.TryParse(data);
				}
				else if(lockCommand.Id == CommandId.LedLight) {
					response = LedLightModuleResponse.TryParse(data);
				}
				else if(lockCommand.Id == CommandId.OpenDoorMultiple) {
					response = OpenDoorMultipleResponse.TryParse(data);
				}
				else if(lockCommand.Id == CommandId.CheckDoorStatusMultiple) {
					response = CheckDoorStatusMultipleResponse.TryParse(data);
				}

				if (response != null)
				{
					bool match = lockCommand.MatchResponse(response);
					if (match)
					{
						var totalConsumed = reader.Consumed + sequenceLength;
						var consumedPos = responseData.GetPosition(totalConsumed);
						_logger?.LogDebug($"{nameof(LockControllerClientSerial)}[{_serialClient.Port}]: {nameof(TryProcessLockResponse)}: response found: {response}. Consumed {totalConsumed} bytes, examined {totalConsumed} bytes.");
						return new ResponseProcessingResult(){ Consumed = consumedPos, Examined = consumedPos, StopReading = true };
					}
					else {
						_logger?.LogError($"{nameof(LockControllerClientSerial)}[{_serialClient.Port}]: {nameof(TryProcessLockResponse)}: parsed response {response} does not match the command {lockCommand}.");
					}
				}
				reader.Advance(1); // Move forward by one byte to search for the next potential match
			}

			_logger?.LogDebug($"{nameof(LockControllerClientSerial)}[{_serialClient.Port}]: {nameof(TryProcessLockResponse)}: failed to find response. Consumed {reader.Consumed} bytes, examined {responseData.Length} bytes.");

			return new ResponseProcessingResult()
						{
							Consumed = responseData.GetPosition(reader.Consumed),
							Examined = responseData.End,
							StopReading = false
						};
		}

		// High-level public operations
		public ValueTask<DoorStatusCode?> OpenDoorSingle(int doorNumber, CancellationToken cancellationToken)
			=> OpenDoorSingle(doorNumber, _serialClient.SendTimeout, _serialClient.ReceiveTimeout, cancellationToken);
		public ValueTask<DoorStatusCode?> OpenDoorSingle(int doorNumber, TimeSpan? receiveTimeout, CancellationToken cancellationToken)
			=> OpenDoorSingle(doorNumber, _serialClient.SendTimeout, receiveTimeout, cancellationToken);
		public async ValueTask<DoorStatusCode?> OpenDoorSingle(int doorNumber, TimeSpan? sendTimeout,
																TimeSpan? receiveTimeout,
																CancellationToken cancellationToken)
		{
			bool acquired = false;
			try
			{
				await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
				acquired = true;
				if (cancellationToken.IsCancellationRequested) {
					return null;
				}
				var command = new OpenDoorSingleCommand(doorNumber);
				_logger?.LogInformation($"{nameof(LockControllerClientSerial)}[{_serialClient.Port}]: {nameof(OpenDoorSingle)}: {doorNumber}");
				var response = await SendReceive(command, sendTimeout, receiveTimeout, cancellationToken).ConfigureAwait(false);
				return response is OpenDoorSingleResponse ods &&
					ods.DoorNumber == doorNumber ?
					ods.DoorStatusCode : null;
			}
			catch(Exception ex)
			{
				_logger?.LogError($"{nameof(LockControllerClientSerial)}[{_serialClient.Port}]: {nameof(OpenDoorSingle)} exception: {ex.Message}");
				return null;
			}
			finally {
				if (acquired) {
					_semaphore.Release();
				}
			}
		}

		public async ValueTask<DoorOpenAttempt> OpenDoorOnce(int doorNumber, TimeSpan? receiveTimeout, CancellationToken cancellationToken)
		{
			bool acquired = false;
			bool sent = false;
			try
			{
				await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
				acquired = true;
				if (cancellationToken.IsCancellationRequested) {
					return DoorOpenAttempt.NotSent;
				}
				var command = new OpenDoorSingleCommand(doorNumber);
				_logger?.LogInformation($"{nameof(LockControllerClientSerial)}[{_serialClient.Port}]: {nameof(OpenDoorOnce)}: {doorNumber}");
				var response = await SendReceive(command, _serialClient.SendTimeout, receiveTimeout, retryCount: null, () => sent = true, cancellationToken)
										.ConfigureAwait(false);
				return new DoorOpenAttempt(sent, response is OpenDoorSingleResponse ods && ods.DoorNumber == doorNumber ? ods.DoorStatusCode : null);
			}
			catch(Exception ex)
			{
				_logger?.LogError($"{nameof(LockControllerClientSerial)}[{_serialClient.Port}]: {nameof(OpenDoorOnce)} exception (komanda {(sent ? "poslata" : "nije poslata")}): {ex.Message}");
				return new DoorOpenAttempt(sent, null);
			}
			finally {
				if (acquired) {
					_semaphore.Release();
				}
			}
		}

		// High-level public operations
		public ValueTask<DoorStatusCode?> CheckDoorSingle(int doorNumber, CancellationToken cancellationToken)
			=> CheckDoorSingle(doorNumber, _serialClient.SendTimeout, _serialClient.ReceiveTimeout, cancellationToken);
		public ValueTask<DoorStatusCode?> CheckDoorSingle(int doorNumber, TimeSpan? receiveTimeout, CancellationToken cancellationToken)
			=> CheckDoorSingle(doorNumber, _serialClient.SendTimeout, receiveTimeout, cancellationToken);
		public async ValueTask<DoorStatusCode?> CheckDoorSingle(int doorNumber,
																TimeSpan? sendTimeout,
																TimeSpan? receiveTimeout,
																CancellationToken cancellationToken)
		{
			bool acquired = false;
			try
			{
				await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
				acquired = true;
				if (cancellationToken.IsCancellationRequested) {
					return null;
				}
				var command = new CheckDoorStatusSingleCommand(doorNumber);
				_logger?.LogInformation($"{nameof(LockControllerClientSerial)}[{_serialClient.Port}]: {nameof(CheckDoorSingle)}: {doorNumber}");
				var response = await SendReceive(command, sendTimeout, receiveTimeout, cancellationToken).ConfigureAwait(false);
				return response is CheckDoorStatusSingleResponse cdss &&
					cdss.DoorNumber == doorNumber ?
					cdss.DoorStatusCode : null;
			}
			catch(Exception ex)
			{
				_logger?.LogError($"{nameof(LockControllerClientSerial)}[{_serialClient.Port}]: {nameof(CheckDoorSingle)} exception: {ex.Message}");
				return null;
			}
			finally {
				if (acquired) {
					_semaphore.Release();
				}
			}
		}

		// High-level public operations
		public ValueTask<DoorStatusCode[]?> CheckDoorMultiple(int moduleNumber, CancellationToken cancellationToken)
			=> CheckDoorMultiple(moduleNumber, _serialClient.SendTimeout, _serialClient.ReceiveTimeout, cancellationToken);
		public ValueTask<DoorStatusCode[]?> CheckDoorMultiple(int moduleNumber,
																	TimeSpan? receiveTimeout,
																	CancellationToken cancellationToken)
			=> CheckDoorMultiple(moduleNumber, _serialClient.SendTimeout, receiveTimeout, cancellationToken);

		public async ValueTask<DoorStatusCode[]?> CheckDoorMultiple(int moduleNumber,
																		TimeSpan? sendTimeout,
																		TimeSpan? receiveTimeout,
																		CancellationToken cancellationToken)
		{
			bool acquired = false;
			try {
				await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
				acquired = true;
				if (cancellationToken.IsCancellationRequested) {
					_logger?.LogDebug($"{nameof(LockControllerClientSerial)}[{_serialClient.Port}]: {nameof(CheckDoorMultiple)}: cancellation requested. Exiting before making a call to {nameof(SendReceive)}.");
					return null;
				}
				var command = new CheckDoorStatusMultipleCommand(moduleNumber);
				_logger?.LogInformation($"{nameof(LockControllerClientSerial)}[{_serialClient.Port}]: {nameof(CheckDoorMultiple)}: checking doors' statuses for module {moduleNumber}");
				var response = await SendReceive(command, sendTimeout, receiveTimeout, cancellationToken).ConfigureAwait(false);
				if (response is CheckDoorStatusMultipleResponse cdsm) {
					if (cdsm.ModuleNumber == moduleNumber || cdsm.ModuleNumber == moduleNumber*8-7) {
						return cdsm.DoorStatusCodes?.ToArray();
					}
				}
				return null;
			}
			catch(Exception ex) {
				_logger?.LogError($"{nameof(LockControllerClientSerial)}[{_serialClient.Port}]: {nameof(CheckDoorMultiple)} exception: {ex.Message}");
				return null;
			}
			finally {
				if (acquired) {
					_semaphore.Release();
				}
			}
		}

		// High-level public operations
		public ValueTask<DoorStatusCode[]?> OpenDoorMultiple(int moduleNumber,
															CancellationToken cancellationToken)
			=> OpenDoorMultiple(moduleNumber, _serialClient.SendTimeout, _serialClient.ReceiveTimeout, cancellationToken);
		public ValueTask<DoorStatusCode[]?> OpenDoorMultiple(int moduleNumber,
															TimeSpan? receiveTimeout,
															CancellationToken cancellationToken)
			=> OpenDoorMultiple(moduleNumber, _serialClient.SendTimeout, receiveTimeout, cancellationToken);
		public async ValueTask<DoorStatusCode[]?> OpenDoorMultiple(int moduleNumber,
																	TimeSpan? sendTimeout,
																	TimeSpan? receiveTimeout,
																	CancellationToken cancellationToken)
		{
			bool acquired = false;
			try {
				await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
				acquired = true;
				if (cancellationToken.IsCancellationRequested) {
					return null;
				}
				var command = new OpenDoorMultipleCommand(moduleNumber);
				_logger?.LogInformation($"{nameof(LockControllerClientSerial)}[{_serialClient.Port}]: {nameof(OpenDoorMultiple)}: {moduleNumber}");
				var response = await SendReceive(command, sendTimeout, receiveTimeout, cancellationToken).ConfigureAwait(false);
				return response is OpenDoorMultipleResponse odsm &&
						odsm.ModuleNumber == moduleNumber ?
						odsm.DoorStatusCodes?.ToArray() : null;
			}
			catch(Exception ex) {
				_logger?.LogError($"{nameof(LockControllerClientSerial)}[{_serialClient.Port}]: {nameof(OpenDoorMultiple)} exception: {ex.Message}");
				return null;
			}
			finally {
				if (acquired) {
					_semaphore.Release();
				}
			}
		}

		// High-level public operations
		public ValueTask<bool?> RestartModule(int moduleNumber, CancellationToken cancellationToken)
			=> RestartModule(moduleNumber, _serialClient.SendTimeout, _serialClient.ReceiveTimeout, cancellationToken);
		public ValueTask<bool?> RestartModule(int moduleNumber,
													TimeSpan? receiveTimeout,
													CancellationToken cancellationToken)
			=> RestartModule(moduleNumber, _serialClient.SendTimeout, receiveTimeout, cancellationToken);
		public async ValueTask<bool?> RestartModule(int moduleNumber,
													TimeSpan? sendTimeout,
													TimeSpan? receiveTimeout,
													CancellationToken cancellationToken)
		{
			bool acquired = false;
			try {
				await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
				acquired = true;
				if (cancellationToken.IsCancellationRequested) {
					return null;
				}
				var command = new RestartModuleCommand(moduleNumber);
				_logger?.LogInformation($"{nameof(LockControllerClientSerial)}[{_serialClient.Port}]: {nameof(RestartModule)}: {moduleNumber}");
				var response = await SendReceive(command, sendTimeout, receiveTimeout, cancellationToken).ConfigureAwait(false);
				return response is RestartModuleResponse rm &&
						rm.ModuleNumber == moduleNumber ?
						true : null;
			}
			catch(Exception ex) {
				_logger?.LogError($"{nameof(LockControllerClientSerial)}[{_serialClient.Port}]: {nameof(RestartModule)} exception: {ex.Message}");
				return null;
			}
			finally {
				if (acquired) {
					_semaphore.Release();
				}
			}
		}

		// High-level public operations
		public ValueTask<bool?> LedLightModule(int moduleNumber,
													bool on,
													CancellationToken cancellationToken)
			=> LedLightModule(moduleNumber, on, _serialClient.SendTimeout, _serialClient.ReceiveTimeout, cancellationToken);
		public ValueTask<bool?> LedLightModule(int moduleNumber,
													bool on,
													TimeSpan? receiveTimeout,
													CancellationToken cancellationToken)
			=> LedLightModule(moduleNumber, on, _serialClient.SendTimeout, receiveTimeout, cancellationToken);
		public async ValueTask<bool?> LedLightModule(int moduleNumber,
													bool on,
													TimeSpan? sendTimeout,
													TimeSpan? receiveTimeout,
													CancellationToken cancellationToken)
		{
			bool acquired = false;
			try {
				await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
				acquired = true;
				if (cancellationToken.IsCancellationRequested) {
					return null;
				}
				var command = new LedLightCommand(moduleNumber, on);
				_logger?.LogInformation($"{nameof(LockControllerClientSerial)}[{_serialClient.Port}]: {nameof(LedLightModule)}: {moduleNumber}");
				var response = await SendReceive(command, sendTimeout, receiveTimeout, cancellationToken).ConfigureAwait(false);
				return response is LedLightModuleResponse ll &&
						ll.ModuleNumber == moduleNumber ?
						true : null;
			}
			catch(Exception ex) {
				_logger?.LogError($"{nameof(LockControllerClientSerial)}[{_serialClient.Port}]: {nameof(LedLightModule)} exception: {ex.Message}");
				return null;
			}
			finally {
				if (acquired) {
					_semaphore.Release();
				}
			}
		}
	}
}