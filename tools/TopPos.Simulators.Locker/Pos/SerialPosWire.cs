using System.IO.Ports;
using System.Threading.Channels;
using Flatsoft.Common;
using Microsoft.Extensions.Logging;

namespace TopPos.LockerSimulator.Pos
{
	/// <summary>
	/// Serijski port simulatora. Namerno ne koristi Flatsoft SerialClient: on čita najmanje 2 bajta (samostalan ACK ne bi
	/// stigao) i pri svakom upisu prazni ulazni bafer. Ovde: jedna petlja čitanja, upis bez pražnjenja ulaza.
	/// Port se drži otvoren sve vreme; ako ne može da se otvori (npr. nema com0com para), pokušava ponovo.
	/// </summary>
	public sealed class SerialPosWire : IPosWire, IAsyncDisposable
	{
		private static readonly TimeSpan __reopenDelay = TimeSpan.FromSeconds(3);

		private readonly ComPortConfiguration _configuration;
		private readonly ILogger? _logger;
		private readonly Channel<PosWireEvent> _incoming = Channel.CreateUnbounded<PosWireEvent>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
		private readonly SemaphoreSlim _writeLock = new(1, 1);
		private readonly PosFrameAssembler _assembler = new();
		private readonly CancellationTokenSource _cts = new();
		private SerialPort? _port;
		private Task? _readTask;

		public SerialPosWire(ComPortConfiguration configuration, ILogger? logger = null)
		{
			_configuration = configuration;
			_logger = logger;
		}

		public string PortName => _configuration.PortName;

		public string Status { get; private set; } = "nije pokrenut";

		public event Action<string>? StatusChanged;

		public ChannelReader<PosWireEvent> Incoming => _incoming.Reader;

		public void Start() => _readTask ??= Task.Run(() => ReadLoopAsync(_cts.Token));

		public async ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
		{
			await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
			try {
				var port = _port;
				if (port == null || !port.IsOpen) {
					throw new InvalidOperationException($"Port {PortName} nije otvoren.");
				}
				await port.BaseStream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
				await port.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
			}
			finally {
				_writeLock.Release();
			}
		}

		private async Task ReadLoopAsync(CancellationToken cancellationToken)
		{
			var buffer = new byte[1024];
			while (!cancellationToken.IsCancellationRequested) {
				try {
					var port = new SerialPort(_configuration.PortName, _configuration.BaudRate, ParseParity(_configuration.Parity), _configuration.DataBits, _configuration.StopBits ?? StopBits.One) {
						ReceivedBytesThreshold = 1,
						Handshake = Handshake.None,
					};
					port.Open();
					_port = port;
					_assembler.Reset();
					SetStatus($"{PortName} otvoren");

					while (!cancellationToken.IsCancellationRequested) {
						int n = await port.BaseStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
						if (n == 0) {
							continue;
						}
						foreach (var ev in _assembler.Feed(buffer.AsMemory(0, n))) {
							_incoming.Writer.TryWrite(ev);
						}
					}
				}
				catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
					break;
				}
				catch (Exception ex) {
					string status = $"{PortName}: {ex.Message} — novi pokušaj za {__reopenDelay.TotalSeconds:0} s";
					if (status != Status) {
						// bez com0com para port ne postoji — ne ponavljamo istu poruku na svaka 3 s
						_logger?.LogWarning("PosSimulator[{Port}]: {Message}", PortName, ex.Message);
						SetStatus(status);
					}
				}
				finally {
					ClosePort();
				}
				try {
					await Task.Delay(__reopenDelay, cancellationToken).ConfigureAwait(false);
				}
				catch (OperationCanceledException) {
					break;
				}
			}
			_incoming.Writer.TryComplete();
		}

		private void ClosePort()
		{
			var port = _port;
			_port = null;
			try {
				port?.Dispose();
			}
			catch {
				// zatvaranje porta ne sme da obori petlju
			}
		}

		private void SetStatus(string status)
		{
			Status = status;
			StatusChanged?.Invoke(status);
		}

		private static Parity ParseParity(string? parity) => Enum.TryParse<Parity>(parity, out var p) ? p : Parity.None;

		public async ValueTask DisposeAsync()
		{
			_cts.Cancel();
			ClosePort();	// prekida ReadAsync koji ne reaguje na token
			if (_readTask != null) {
				try {
					await _readTask.ConfigureAwait(false);
				}
				catch {
				}
			}
			_cts.Dispose();
		}
	}
}
