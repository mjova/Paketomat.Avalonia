using System.Net;
using Microsoft.Extensions.Logging;
using TopPos.LockController;
using TopPos.LockController.NCU24L;

namespace TopPos.LockerSimulator
{
	/// <summary>
	/// Simulator paketomata bez prikaza (v2 MainWindow bez WPF-a): vrata i moduli (<see cref="Locker"/>), NCU24L kontroler
	/// preko TCP-a sa ponašanjem ZLG modula i kontrolni API. Stanje je pod jednim lock-om (v2: WPF Dispatcher); komande
	/// kontrolera i kontrolni API ga dele.
	/// v4 faza A: samo NCU24L. Serijski kontroler, skener i POS terminal traže serijski par (faza B) — sekcije
	/// BarcodeScanner i PosTerminal se ignorišu uz upozorenje, a <c>LockController.Type</c> "Serial" se odbija.
	/// </summary>
	public sealed class LockerEngine : IAsyncDisposable
	{
		private readonly LockerSimulatorConfiguration _configuration;
		private readonly Locker _locker;
		private readonly Lock _stateLock = new();
		private readonly UnlockLog _unlockLog = new();
		private readonly LockFaults _lockFaults = new();
		private readonly CancellationTokenSource _cts = new();
		private readonly TcpLockController _tcpLockController;
		private readonly ControlApiServer? _controlApi;
		private readonly ILogger? _logger;

		public LockerEngine(LockerSimulatorConfiguration config, ILogger? logger)
		{
			_configuration = config;
			_logger = logger;

			if (!string.Equals(config.LockController.Type, "NCU24L", StringComparison.OrdinalIgnoreCase)) {
				throw new NotSupportedException($"Kontroler \"{config.LockController.Type}\" nije podržan; v4 faza A simulira samo NCU24L (serijski — faza B)");
			}
			if (config.BarcodeScanner != null) {
				_logger?.LogWarning("Sekcija BarcodeScanner se ignoriše: skener se još ne simulira (faza B)");
			}
			if (config.PosTerminal != null) {
				_logger?.LogWarning("Sekcija PosTerminal se ignoriše: POS terminal se još ne simulira (faza B); kiosk koristi SimulatedPosClient");
			}

			int numModules = Math.Max(1, _configuration.Locker.ModuleCount);
			int numBoxesPerModule = Math.Max(1, _configuration.Locker.BoxCountPerModule);
			_locker = Locker.MakeLocker(numModules, numBoxesPerModule);

			int port = config.LockController.TcpController?.Port ?? NCU24LProtocol.DEFAULT_PORT;
			var listenAddress = string.IsNullOrWhiteSpace(config.LockController.ListenAddress)
									? IPAddress.Any
									: IPAddress.Parse(config.LockController.ListenAddress);
			_tcpLockController = new TcpLockController(listenAddress, port, HandleNcu24LCommand, GetNcu24LLockStates, _cts.Token, logger,
				isModuleOn: addr => Locked(() => addr < _locker.Modules.Count && _locker.Modules[addr].IsOn),
				networkModule: config.LockController.NetworkModule,
				dropUnlockResponse: () => _lockFaults.Take(LockFault.DropOpenResponse));

			if (config.ControlApi != null) {
				_controlApi = new ControlApiServer(config.ControlApi.Port, _tcpLockController.Port, _locker, _stateLock, _unlockLog, _lockFaults, logger,
					onReset: _tcpLockController.ResetNetworkModule);
				try {
					_controlApi.Start();
				}
				catch {
					_cts.Cancel();
					throw;
				}
			}
		}

		/// <summary>Port NCU24L simulatora (stvarni i kad je u konfiguraciji 0).</summary>
		public int TcpPort => _tcpLockController.Port;

		/// <summary>Port kontrolnog API-ja, ili null bez sekcije ControlApi.</summary>
		public int? ControlApiPort => _controlApi?.Port;

		public async ValueTask DisposeAsync()
		{
			_controlApi?.Stop();
			_cts.Cancel();
			await _tcpLockController.Completion.ConfigureAwait(false);
			_cts.Dispose();
		}

		private T Locked<T>(Func<T> func)
		{
			lock (_stateLock) {
				return func();
			}
		}

		private LockBox? FindLockBox(int number)
			=> _locker.Modules.SelectMany(m => m.Boxes).FirstOrDefault(box => box.Number == number);

		// ==================== NCU24L Handlers ====================

		/// <summary>
		/// Handle NCU24L command. NCU address maps to module index, lockNum maps to box within module.
		/// </summary>
		private NCU24LResponseCode HandleNcu24LCommand(byte ncuAddress, byte lockNum, NCU24LCommand cmd)
		{
			return Locked(() =>
			{
				if (cmd == NCU24LCommand.Unlock)
				{
					if (lockNum == NCU24LProtocol.ALL_LOCKS)
					{
						// Unlock all locks on this NCU (skip blocked)
						if (ncuAddress < _locker.Modules.Count)
						{
							foreach (var box in _locker.Modules[ncuAddress].Boxes) {
								_unlockLog.Record(box.Number, unlocked: !box.IsBlocked);
								if (!box.IsBlocked)
									box.IsLocked = false;
							}
						}
						return NCU24LResponseCode.Success;
					}

					// Map NCU address + lock number to door number
					int doorNumber = ncuAddress * _configuration.Locker.BoxCountPerModule + lockNum + 1;
					var box2 = FindLockBox(doorNumber);
					if (box2 == null)
						return NCU24LResponseCode.Failed;

					_unlockLog.Record(doorNumber, unlocked: !box2.IsBlocked);
					if (!box2.IsBlocked)
						box2.IsLocked = false;
					return NCU24LResponseCode.Success;
				}

				return NCU24LResponseCode.UnknownCommand;
			});
		}

		/// <summary>
		/// Get lock states for an NCU address as 3 bytes (24 bits).
		/// Bit=1 means closed, bit=0 means open.
		/// </summary>
		private byte[] GetNcu24LLockStates(byte ncuAddress)
		{
			return Locked(() =>
			{
				var states = new byte[3];

				if (ncuAddress >= _locker.Modules.Count)
					return states; // All zeros = all open

				var boxes = _locker.Modules[ncuAddress].Boxes;
				for (int i = 0; i < boxes.Count && i < 24; i++)
				{
					if (boxes[i].IsLocked)
					{
						int byteIndex = i / 8;
						int bitIndex = i % 8;
						states[byteIndex] |= (byte)(1 << bitIndex);
					}
				}

				return states;
			});
		}
	}
}
