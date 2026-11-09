using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.IO.Ports;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Flatsoft.Net.Serial
{
	public class SerialClient : IDisposable, IAsyncDisposable
	{
		private SerialPort? _serialPort;
		private int _baudRate;
		private int _dataBits;
		private Parity _parity;
		private StopBits _stopBits;
		private int _receivedBytesThreshold;
		private TimeSpan? _receiveTimeout;
		private TimeSpan? _sendTimeout;
		private readonly Pipe _sendPipe;
		private readonly Pipe _receivePipe;
		private Task? _taskSend;
		private Task? _taskReceive;
		private readonly SemaphoreSlim _semaphore;
		private CancellationTokenSource _mainCancellation;
		private readonly ILogger? _logger;
		private bool _disconnecting;
		private bool _emptyReaderBeforeWrite = true;    // default: empty the reader before writing to the serial port

		public SerialClient(string portName, ILogger? logger = null)
		{
			Port = portName ?? throw new ArgumentNullException(nameof(portName));
			_baudRate = 9600;
			_dataBits = 8;
			_parity = Parity.None;
			_stopBits = StopBits.One;
			_receivedBytesThreshold = 2;
			_receiveTimeout = TimeSpan.FromMilliseconds(1000);
			_sendTimeout = TimeSpan.FromMilliseconds(1000);

			_semaphore = new SemaphoreSlim(1, 1);
			_sendPipe = new Pipe();
			_receivePipe = new Pipe();
			_logger = logger;
			_mainCancellation = new CancellationTokenSource();
		}

		public string Port { get; }
		public int BaudRate {
			get => _serialPort?.BaudRate ?? _baudRate;
			set {
				_baudRate = value;
				RecreateSerialPortIfNeeded();
			}
		}
		public int DataBits {
			get => _serialPort?.DataBits ?? _dataBits;
			set {
				_dataBits = value;
				RecreateSerialPortIfNeeded();
			}
		}

		public Parity Parity {
			get => _serialPort?.Parity ?? _parity;
			set {
				_parity = value;
				RecreateSerialPortIfNeeded();
			}
		}

		public StopBits StopBits {
			get => _serialPort?.StopBits ?? _stopBits;
			set {
				_stopBits = value;
				RecreateSerialPortIfNeeded();
			}
		}

		private void RecreateSerialPortIfNeeded()
		{
			if (_serialPort != null && _serialPort.IsOpen) {
				if (_serialPort.BaudRate != _baudRate || _serialPort.DataBits != _dataBits || _serialPort.StopBits != _stopBits) {
					_serialPort.Close();
					_serialPort = null;
				}
			}
		}

		public TimeSpan? ReceiveTimeout {
			get {
				if (_serialPort != null) {
					return _serialPort.ReadTimeout != SerialPort.InfiniteTimeout ? TimeSpan.FromMilliseconds(_serialPort.ReadTimeout) : null;
				}
				return _receiveTimeout;
			}
			set {
				_receiveTimeout = value;
				if (_serialPort != null) {
					_serialPort.ReadTimeout = value.HasValue ? (int) value.Value.TotalMilliseconds : SerialPort.InfiniteTimeout;
				}
			}
		}

		public TimeSpan? SendTimeout {
			get {
				if (_serialPort != null) {
					return _serialPort.WriteTimeout != SerialPort.InfiniteTimeout ? TimeSpan.FromMilliseconds(_serialPort.WriteTimeout) : null;
				}
				return _sendTimeout;
			}
			set {
				_sendTimeout = value;
				if (_serialPort != null) {
					_serialPort.WriteTimeout = value.HasValue ? (int) value.Value.TotalMilliseconds : SerialPort.InfiniteTimeout;
				}
			}
		}

		public int ReceivedBytesThreshold {
			get => _serialPort?.ReceivedBytesThreshold ?? _receivedBytesThreshold;
			set {
				_receivedBytesThreshold = Math.Max(1, value);
				if (_serialPort != null) {
					_serialPort.ReceivedBytesThreshold = _receivedBytesThreshold;
				}
			}
		}

		public bool EmptyReaderBeforeWrite {
			get => _emptyReaderBeforeWrite;
			set => _emptyReaderBeforeWrite = value;
		}

		public bool IsConnected => _serialPort?.IsOpen == true;

		public async ValueTask<bool> OpenAsync(CancellationToken cancellationToken)
		{
			if (_taskSend != null && _taskSend.IsCompleted || _taskReceive != null && _taskReceive.IsCompleted)
			{
				_logger?.LogInformation($"{nameof(SerialClient)}[{Port}]: {nameof(OpenAsync)}: one or both loops have terminated. Restarting.");

				// Observe exceptions to prevent UnobservedTaskException
				if (_taskSend is { IsFaulted: true }) { _ = _taskSend.Exception; }
				if (_taskReceive is { IsFaulted: true }) { _ = _taskReceive.Exception; }

				_mainCancellation.Cancel();

				// Wait for both loops to fully exit before touching pipes
				try { if (_taskSend != null) await _taskSend; } catch { }
				try { if (_taskReceive != null) await _taskReceive; } catch { }

				_disconnecting = false;
				_mainCancellation = new CancellationTokenSource();

				// Dispose stale serial port
				if (_serialPort != null) {
					try { _serialPort.Dispose(); } catch { }
					_serialPort = null;
				}

				// Complete and reset pipes
				_sendPipe.Writer.Complete();
				_sendPipe.Reader.Complete();
				_receivePipe.Writer.Complete();
				_receivePipe.Reader.Complete();
				_sendPipe.Reset();
				_receivePipe.Reset();

				_taskSend = null;
				_taskReceive = null;
			}

			_taskSend ??= SendLoop();
			_taskReceive ??= ReceiveLoop();

			using var cts = CancellationTokenSource.CreateLinkedTokenSource(_mainCancellation.Token, cancellationToken);
			await EnsureSerialPortOpen(cts.Token);
			return true;
		}

		public async ValueTask EnsureSerialPortOpen(CancellationToken cancellationToken)
		{
			// Čekanje pre try: otkazano čekanje ne preuzima semafor, pa finally ne sme da ga oslobodi.
			await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
			try {
				if (IsConnected) {
					return;
				}

				if (_serialPort != null && !_serialPort.IsOpen) {
					_logger?.LogInformation($"{nameof(SerialClient)}[{Port}]: {nameof(EnsureSerialPortOpen)}: disposing closed serial port instance before reconnecting.");
					try { _serialPort.Dispose(); } catch { }
					_serialPort = null;
				}

				_serialPort ??= new SerialPort(Port, BaudRate, Parity, DataBits, StopBits);
				//_serialPort.DtrEnable = false;
				//_serialPort.RtsEnable = false;
				_serialPort.Open();
				_serialPort.ReadTimeout = _receiveTimeout.HasValue ? (int) _receiveTimeout.Value.TotalMilliseconds : SerialPort.InfiniteTimeout;
				_serialPort.WriteTimeout = _sendTimeout.HasValue ? (int) _sendTimeout.Value.TotalMilliseconds : SerialPort.InfiniteTimeout;
				_serialPort.ReceivedBytesThreshold = _receivedBytesThreshold;
			}
			finally {
				_semaphore.Release();
			}
		}

		private async Task SendLoop()
		{
			_logger?.LogInformation($"{nameof(SerialClient)}[{Port}]: {nameof(SendLoop)}: entering send loop.");

			while (!_disconnecting && !_mainCancellation.IsCancellationRequested) {
				try {
					ReadResult result = await _sendPipe.Reader.ReadAsync(_mainCancellation.Token)
														.ConfigureAwait(false);
					if (result.IsCanceled) {
						continue;
					}
					SequencePosition position = result.Buffer.Start;
					while (result.Buffer.TryGet(ref position, out ReadOnlyMemory<byte> segment)) {
						if (_disconnecting || _mainCancellation.Token.IsCancellationRequested) {
							break;
						}
						await EnsureSerialPortOpen(_mainCancellation.Token).ConfigureAwait(false);
						if (result.Buffer.Length != segment.Length) {
							Debugger.Break();
						}

						await _serialPort!.WriteAsync(segment, _mainCancellation.Token).ConfigureAwait(false);
						_logger?.LogDebug($"{nameof(SerialClient)}[{Port}]: {nameof(SendLoop)}: written {segment.Length} byte(s) to the serial port: {BitConverter.ToString(segment.ToArray())}");
					}
					_sendPipe.Reader.AdvanceTo(result.Buffer.End);
				}
				catch (FileNotFoundException fex) {
					_logger?.LogError($"{nameof(SerialClient)}[{Port}]: {nameof(SendLoop)}: port opening failed. Details: {fex.Message}");
					if (_serialPort != null) {
						try { _serialPort.Dispose(); } catch { }
						_serialPort = null;
					}
					return;
				}
				catch (OperationCanceledException) {
					_logger?.LogDebug($"{nameof(SerialClient)}[{Port}]: {nameof(SendLoop)}: send operation cancelled.");
					_sendPipe.Reader.CancelPendingRead();
					_logger?.LogDebug($"{nameof(SerialClient)}[{Port}]: {nameof(SendLoop)}: cancelled pending read on the send pipe reader");
				}
				catch (IOException) when (!_mainCancellation.IsCancellationRequested) {
					// ovo se dešava samo kada je povezan USB-serial adapter
					_logger?.LogDebug($"{nameof(SerialClient)}[{Port}]: {nameof(SendLoop)}: send operation aborted (I/O cancelled).");
					_sendPipe.Reader.CancelPendingRead();
				}
				catch (Exception ex) {
					_logger?.LogError($"{nameof(SerialClient)}[{Port}]: {nameof(SendLoop)}: send operation failed. Details: {ex.Message}");
					_sendPipe.Reader.CancelPendingRead();
					_logger?.LogDebug($"{nameof(SerialClient)}[{Port}]: {nameof(SendLoop)}: cancelled pending read on the send pipe reader");
				}
			}
			_logger?.LogInformation($"{nameof(SerialClient)}[{Port}]: {nameof(SendLoop)}: exiting send loop.");
		}

		private async Task ReceiveLoop()
		{
			_logger?.LogInformation($"{nameof(SerialClient)}[{Port}]: {nameof(ReceiveLoop)}: entering receive loop.");
			while (!_disconnecting && !_mainCancellation.IsCancellationRequested) {
				try {
					await EnsureSerialPortOpen(_mainCancellation.Token).ConfigureAwait(false);

					// Pre-emtpive flush, just so that any previous/pending cancellation doesn't impact the next write attempt
					var flushResult = await _receivePipe.Writer.FlushAsync(_mainCancellation.Token).ConfigureAwait(false);
					if (flushResult.IsCanceled) {
						_logger?.LogDebug($"{nameof(SerialClient)}[{Port}]: {nameof(ReceiveLoop)}: pre-emptive flush cancelled, likely from a previous timeout. Retrying.");
						continue;
					}

					Memory<byte> memory = _receivePipe.Writer.GetMemory(1024);
					int bytesRead = await _serialPort!.BaseStream.ReadAtLeastAsync(memory, _serialPort.ReceivedBytesThreshold, false, _mainCancellation.Token).ConfigureAwait(false);
					//_logger?.LogDebug($"{nameof(SerialClient)}[{_serialPort?.PortName}].{nameof(ReceiveLoop)}: received {bytesRead} bytes.");
					if (_disconnecting || _mainCancellation.IsCancellationRequested) {
						break;
					}

					if (bytesRead > 0) {
						_logger?.LogDebug($"{nameof(SerialClient)}[{Port}]: {nameof(ReceiveLoop)}: read {bytesRead} byte(s) from the serial port: {BitConverter.ToString(memory.Slice(0, bytesRead).ToArray())}");
						_receivePipe.Writer.Advance(bytesRead);

						int retries = 1;
						do {
							flushResult = await _receivePipe.Writer.FlushAsync(_mainCancellation.Token).ConfigureAwait(false);
							if (flushResult.IsCanceled) {
								retries--;
								_logger?.LogDebug($"{nameof(SerialClient)}[{Port}]: {nameof(ReceiveLoop)}: writing to the receive pipe writer is cancelled, likely from a previous timeout. Retrying.");
							}
							else if (flushResult.IsCompleted) {
								_logger?.LogWarning($"{nameof(SerialClient)}[{Port}]: {nameof(ReceiveLoop)}: no reader on the other side of the receive pipe. Terminating loop.");
								break;
							}
						} while (flushResult.IsCanceled && retries >= 0);
					}
				}
				catch (FileNotFoundException fex) {
					_logger?.LogError($"{nameof(SerialClient)}[{Port}]: {nameof(ReceiveLoop)}: port opening failed. Details: {fex.Message}");
					if (_serialPort != null) {
						try { _serialPort.Dispose(); } catch { }
						_serialPort = null;
					}
					return;
				}
				catch (OperationCanceledException) {
					_logger?.LogDebug($"{nameof(SerialClient)}[{Port}]: {nameof(ReceiveLoop)}: receive operation cancelled");
					_receivePipe.Writer.CancelPendingFlush();
					_logger?.LogDebug($"{nameof(SerialClient)}[{Port}]: {nameof(ReceiveLoop)}: cancelled pending flush on the receive pipe");
				}
				catch (IOException) when (!_mainCancellation.IsCancellationRequested) {
					// ovo se dešava samo kada je povezan USB-serial adapter
					// zato ne logujemo
					// _logger?.LogDebug($"{nameof(SerialClient)}[{Port}]: {nameof(ReceiveLoop)}: receive operation aborted (I/O cancelled).");
					// _receivePipe.Writer.CancelPendingFlush();
				}
				catch (Exception ex) {
					_logger?.LogError($"{nameof(SerialClient)}[{Port}]: {nameof(ReceiveLoop)}: receive operation failed. Details: {ex.Message}");
					_receivePipe.Writer.CancelPendingFlush();
					_logger?.LogDebug($"{nameof(SerialClient)}[{Port}]: {nameof(ReceiveLoop)}: cancelled pending flush on the receive pipe");
				}
			}
			_logger?.LogInformation($"{nameof(SerialClient)}[{Port}]: {nameof(ReceiveLoop)}: exiting receive loop.");
		}

		public async ValueTask Write(ReadOnlyMemory<byte> data, TimeSpan? writeTimeout, CancellationToken cancellationToken)
		{
			TimeSpan? currentTimeout = SendTimeout;
			try {
				SendTimeout = writeTimeout;
				await Write(data, cancellationToken).ConfigureAwait(false);
			}
			finally {
				SendTimeout = currentTimeout;
			}
		}

		public async ValueTask Write(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
		{
			try {
				await EnsureSerialPortOpen(cancellationToken).ConfigureAwait(false);

				while (!cancellationToken.IsCancellationRequested) {
					_sendPipe.Reader.CancelPendingRead();

					_serialPort?.DiscardInBuffer();
					// The owner of this instance might be awaiting to read data from serial port at this moment.
					// If so, the owner must indicate that emptying the read buffer should not be attempted because
					// there is already an ongoing read operation. Otherwise, InvalidOperationException will be thrown.
					if (_emptyReaderBeforeWrite && _receivePipe.Reader.TryRead(out var r)) {
						_logger?.LogDebug($"{nameof(SerialClient)}[{Port}]: {nameof(Write)}: discarding {r.Buffer.Length} byte(s) from a previous pipe write/read operation");
						_receivePipe.Reader.AdvanceTo(r.Buffer.End);
					}

					var result = await _sendPipe.Writer.WriteAsync(data, cancellationToken).ConfigureAwait(false);
					if (result.IsCanceled) {
						_logger?.LogDebug($"{nameof(SerialClient)}[{Port}]: {nameof(Write)}: result from the send pipe writer is cancelled, likely from a previous timeout. Retrying.");
						continue;
					}
					_logger?.LogDebug($"{nameof(SerialClient)}[{Port}]: {nameof(Write)}: written {data.Length} byte(s) to the send pipe: {BitConverter.ToString(data.ToArray())}");
					await _sendPipe.Writer.FlushAsync(cancellationToken).ConfigureAwait(false);
					break;
				}
			}
			catch (Exception ex) {
				if (ex is not OperationCanceledException) {
					_logger?.LogError($"{nameof(SerialClient)}[{Port}]: {nameof(Write)}: write failed. Details: {ex.Message}");
					throw;
				}
			}
		}

		public async ValueTask Read(Func<ReadOnlySequence<byte>, ResponseProcessingResult> responseProcessor, CancellationToken cancellationToken)
		{
			try {
				_logger?.LogDebug($"{nameof(SerialClient)}[{Port}]: {nameof(Read)}: entering read loop");
				await EnsureSerialPortOpen(cancellationToken).ConfigureAwait(false);

				_logger?.LogDebug($"{nameof(SerialClient)}[{Port}]: {nameof(Read)}: cancellationToken.IsCancellationRequested = {cancellationToken.IsCancellationRequested}");
				while (!cancellationToken.IsCancellationRequested) {
					_logger?.LogDebug($"{nameof(SerialClient)}[{Port}]: {nameof(Read)}: creating a cancellation token using read timeout {_serialPort!.ReadTimeout}");
					using var cts = new CancellationTokenSource(_serialPort!.ReadTimeout);
					var timeoutToken = cts.Token;

					using var ctsLinked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutToken);

					ReadResult result = await _receivePipe.Reader.ReadAsync(ctsLinked.Token)
																	.ConfigureAwait(false);
					if (result.IsCanceled) {
						_logger?.LogDebug($"{nameof(SerialClient)}[{Port}]: {nameof(Read)}: result from the receive pipe reader is cancelled, likely from a previous timeout. Retrying.");
						continue;
					}
					ReadOnlySequence<byte> buffer = result.Buffer;
					_logger?.LogDebug($"{nameof(SerialClient)}[{Port}]: {nameof(Read)}: read {buffer.Length} byte(s) from the receive pipe: {BitConverter.ToString(buffer.ToArray())}");
#if DEBUG
					{
						SequencePosition position = result.Buffer.Start;
						while (result.Buffer.TryGet(ref position, out ReadOnlyMemory<byte> segment)) {
							_logger?.LogDebug($"{nameof(SerialClient)}[{Port}]: {nameof(Read)}: reading {segment.Length} byte(s) from the receive pipe: {BitConverter.ToString(segment.ToArray())}");
						}
					}
#endif
					_logger?.LogDebug($"{nameof(SerialClient)}[{Port}]: {nameof(Read)}: passing bytes to the response processor.");
					var processingResult = responseProcessor(buffer);

					_receivePipe.Reader.AdvanceTo(processingResult.Consumed, processingResult.Examined);
					if (processingResult.StopReading) {
						_logger?.LogDebug($"{nameof(SerialClient)}[{Port}]: {nameof(Read)}: response processor sent a stop signal. Reading complete.");
						return;
					}
				}
			}
			catch (Exception ex) {
				_logger?.LogError($"{nameof(SerialClient)}[{Port}]: {nameof(Read)}: failed to get response data. Details: {ex.Message}");

				if (ex is OperationCanceledException) {
					if (!cancellationToken.IsCancellationRequested) // if not cancelled externally, it's a timeout
					{
						_logger?.LogError($"{nameof(SerialClient)}[{Port}]: {nameof(Read)}: timed out waiting {_serialPort!.ReadTimeout} milliseconds for data.");
						try {
							_logger?.LogDebug($"{nameof(SerialClient)}[{Port}]: {nameof(Read)}: canceling pending read.");
							_receivePipe.Reader.CancelPendingRead();
							_serialPort?.DiscardInBuffer();
						}
						catch (Exception exd) {
							_logger?.LogError($"{nameof(SerialClient)}[{Port}]: {nameof(Read)}: failed to discard serial port input buffer. Details: {exd.Message}");
						}
						throw new TimeoutException();
					}
				}
				throw;
			}
		}

		public void Disconnect()
		{
			try {
				_disconnecting = true;
				_logger?.LogInformation($"{nameof(SerialClient)}[{Port}]: disconnecting instance.");
				_mainCancellation?.Cancel();
				_receivePipe.Reader.Complete();
				_receivePipe.Writer.Complete();
				_sendPipe.Writer.Complete();
				_sendPipe.Reader.Complete();
				if (IsConnected) {
					_logger?.LogInformation($"{nameof(SerialClient)}[{Port}]: discarding port {Port} in/out buffers.");
					_serialPort?.DiscardInBuffer();
					_serialPort?.DiscardOutBuffer();

					_logger?.LogInformation($"{nameof(SerialClient)}[{Port}]: closing port {Port}");
					_serialPort?.Close();
				}
				_logger?.LogInformation($"{nameof(SerialClient)}[{Port}]: instance disconnected.");
			}
			catch (Exception ex) {
				_logger?.LogError($"{nameof(SerialClient)}[{Port}]: disconnect failed. Details {ex.Message}");
			}
		}

		public void Dispose()
		{
			try {
				_logger?.LogInformation($"Disposing {nameof(SerialClient)}[{Port}] instance.");
				try {
					Disconnect();
				}
				catch { }

				_receivePipe?.Writer.Complete();
				_receivePipe?.Reader.Complete();
				_sendPipe?.Writer.Complete();
				_sendPipe?.Reader.Complete();
				_serialPort?.Dispose();
				_serialPort = null;
				_logger?.LogInformation($"{nameof(SerialClient)}[{Port}] instance disposed.");
			}
			catch (Exception ex) {
				_logger?.LogError($"Failed to dispose {nameof(SerialClient)}[{Port}] instance properly. Details: {ex.Message}");
			}
		}

		public async ValueTask DisposeAsync()
		{
			await Task.Yield();

			try {
				_logger?.LogInformation($"Disposing {nameof(SerialClient)}[{Port}] instance asynchronously.");
				try {
					Disconnect();
				}
				catch { }

				await _receivePipe.Writer.CompleteAsync().ConfigureAwait(false);
				await _receivePipe.Reader.CompleteAsync().ConfigureAwait(false);
				await _sendPipe.Writer.CompleteAsync().ConfigureAwait(false);
				await _sendPipe.Reader.CompleteAsync().ConfigureAwait(false);
				_serialPort?.Dispose();
				_serialPort = null;
				_logger?.LogInformation($"{nameof(SerialClient)}[{Port}] instance disposed asynchronously.");
			}
			catch (Exception ex) {
				_logger?.LogError($"Failed to async dispose {nameof(SerialClient)}[{Port}] instance properly. Details: {ex.Message}");
			}
		}
	}

	// https://sparxeng.com/blog/software/must-use-net-system-io-ports-serialport
	// https://stackoverflow.com/questions/51917749/c-sharp-serialport-basestream-readasync-cancellationtoken-never-cancelled/54610437#54610437
	// https://github.com/AndreasAmMueller/Modbus/blob/a6d11080c2f5a1205681c881f3ba163d2ac84a1f/src/Modbus.Serial/Util/Extensions.cs
	internal static class SerialClientAsyncFixes
	{
		internal static async Task<int> ReadAsync(this SerialPort serialPort, Memory<byte> buffer, CancellationToken cancellationToken)
		{
			if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
				using var cts = new CancellationTokenSource(serialPort.ReadTimeout);
				using var registration1 = cts.Token.Register(() => serialPort.DiscardInBuffer());
				using var registration2 = cancellationToken.Register(() => cts.Cancel());

				try {
					return await serialPort.BaseStream.ReadAsync(buffer, cts.Token).ConfigureAwait(false);
				}
				catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
					cancellationToken.ThrowIfCancellationRequested();
					return 0;
				}
				catch (OperationCanceledException) when (cts.IsCancellationRequested) {
					throw new TimeoutException($"{nameof(SerialPort)}[{serialPort.PortName}]: read timeout.");
				}
				catch (IOException) when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested) {
					throw new TimeoutException($"{nameof(SerialPort)}[{serialPort.PortName}]: read timeout.");
				}
			}
			else {
				return await serialPort.BaseStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
			}
		}

		internal static async Task WriteAsync(this SerialPort serialPort, ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
		{
			if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
				using var cts = new CancellationTokenSource(serialPort.WriteTimeout);
				using var registration1 = cts.Token.Register(() => serialPort.DiscardOutBuffer());
				using var registration2 = cancellationToken.Register(() => cts.Cancel());

				try {
					await serialPort.BaseStream.WriteAsync(buffer, cts.Token).ConfigureAwait(false);
				}
				catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
					cancellationToken.ThrowIfCancellationRequested();
				}
				catch (OperationCanceledException) when (cts.IsCancellationRequested) {
					throw new TimeoutException($"{nameof(SerialPort)}[{serialPort.PortName}]: write timeout.");
				}
				catch (IOException) when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested) {
					throw new TimeoutException($"{nameof(SerialPort)}[{serialPort.PortName}]: write timeout.");
				}
			}
			else {
				await serialPort.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
				await serialPort.BaseStream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
			}
		}
	}

	public struct ResponseProcessingResult
	{
		public SequencePosition Consumed { get; set; }

		public SequencePosition Examined { get; set; }

		public bool StopReading { get; set; }
	}
}
