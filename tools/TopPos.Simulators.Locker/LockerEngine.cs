using System.Net;
using Flatsoft.Common;
using Microsoft.Extensions.Logging;
using TopPos.LockController;
using TopPos.LockController.NCU24L;
using TopPos.LockController.Serial;
using TopPos.LockerSimulator.Pos;
using TopPos.Simulators.Serial;

namespace TopPos.LockerSimulator
{
	/// <summary>
	/// Simulator paketomata bez prikaza (v2 MainWindow bez WPF-a): vrata i moduli (<see cref="Locker"/>), kontroler brava
	/// (NCU24L preko TCP-a sa ponašanjem ZLG modula ili serijski), skener, POS terminal i kontrolni API. Stanje je pod jednim
	/// lock-om (v2: WPF Dispatcher); komande kontrolera i kontrolni API ga dele.
	/// Serijski uređaji: na Linuxu, uz <c>KioskPortName</c>, simulator sam pravi virtuelni par (<see cref="PtySerialPair"/>);
	/// na Windows-u par je u com0com-u, a <c>ComPort.PortName</c> je strana simulatora (kao v2).
	/// </summary>
	public sealed class LockerEngine : IAsyncDisposable
	{
		private readonly LockerSimulatorConfiguration _configuration;
		private readonly Locker _locker;
		private readonly Lock _stateLock = new();
		private readonly UnlockLog _unlockLog = new();
		private readonly LockFaults _lockFaults = new();
		private readonly CancellationTokenSource _cts = new();
		private readonly List<PtySerialPair> _serialPairs = [];
		private readonly TcpLockController? _tcpLockController;
		private readonly LockController? _lockController;
		private readonly BarcodeScanner? _barcodeScanner;
		private readonly SerialPosWire? _posWire;
		private readonly PosTerminalEngine? _posEngine;
		private readonly Task _posRun = Task.CompletedTask;
		private readonly ControlApiServer? _controlApi;
		private readonly ILogger? _logger;

		public LockerEngine(LockerSimulatorConfiguration config, ILogger? logger)
		{
			_configuration = config;
			_logger = logger;

			bool isNcu24L = string.Equals(config.LockController.Type, "NCU24L", StringComparison.OrdinalIgnoreCase);
			if (!isNcu24L && !string.Equals(config.LockController.Type, "Serial", StringComparison.OrdinalIgnoreCase)) {
				throw new NotSupportedException($"Kontroler \"{config.LockController.Type}\" nije podržan (NCU24L ili Serial)");
			}

			int numModules = Math.Max(1, _configuration.Locker.ModuleCount);
			int numBoxesPerModule = Math.Max(1, _configuration.Locker.BoxCountPerModule);
			_locker = Locker.MakeLocker(numModules, numBoxesPerModule);

			try {
				if (isNcu24L) {
					int port = config.LockController.TcpController?.Port ?? NCU24LProtocol.DEFAULT_PORT;
					var listenAddress = string.IsNullOrWhiteSpace(config.LockController.ListenAddress)
											? IPAddress.Any
											: IPAddress.Parse(config.LockController.ListenAddress);
					_tcpLockController = new TcpLockController(listenAddress, port, HandleNcu24LCommand, GetNcu24LLockStates, _cts.Token, logger,
						isModuleOn: addr => Locked(() => addr < _locker.Modules.Count && _locker.Modules[addr].IsOn),
						networkModule: config.LockController.NetworkModule,
						dropUnlockResponse: () => _lockFaults.Take(LockFault.DropOpenResponse));
				}
				else {
					LockControllerPort = OpenSerial(config.LockController.ComPort, config.LockController.KioskPortName);
					_lockController = new LockController(config.LockController.ComPort, MakeResponse, _cts.Token, logger);
				}

				if (config.BarcodeScanner != null) {
					BarcodeScannerPort = OpenSerial(config.BarcodeScanner.ComPort, config.BarcodeScanner.KioskPortName);
					_barcodeScanner = new BarcodeScanner(config.BarcodeScanner, logger);
				}

				// POS terminal radi od starta (kiosk pri pokretanju proverava prisustvo terminala).
				if (config.PosTerminal != null) {
					PosTerminalPort = OpenSerial(config.PosTerminal.ComPort, config.PosTerminal.KioskPortName);
					_posWire = new SerialPosWire(config.PosTerminal.ComPort, logger);
					_posEngine = new PosTerminalEngine(_posWire, new PosSimulatorOptions {
						Pin = config.PosTerminal.Pin,
						Tid = config.PosTerminal.Tid,
						CardWaitTimeout = TimeSpan.FromSeconds(config.PosTerminal.CardWaitTimeoutSeconds),
						SpeedFactor = config.PosTerminal.SpeedFactor,
					}, logger);
					_posWire.Start();
					_posRun = _posEngine.RunAsync(_cts.Token);
				}

				// READY tek kad pumpa vidi otvorene strane simulatora: bajtovi kioska poslati ranije bi se odbacili
				foreach (var pair in _serialPairs) {
					if (!pair.WaitForSimulator(TimeSpan.FromSeconds(10))) {
						throw new IOException($"Simulator nije otvorio svoju stranu para {pair.KioskPortName} ({pair.SimulatorPortName})");
					}
				}

				if (config.ControlApi != null) {
					_controlApi = new ControlApiServer(config.ControlApi.Port, _tcpLockController?.Port, _locker, _stateLock, _unlockLog, _lockFaults,
						_barcodeScanner, _posEngine, logger, onReset: OnReset);
					_controlApi.Start();
				}
			}
			catch {
				DisposeAsync().AsTask().GetAwaiter().GetResult();
				throw;
			}
		}

		/// <summary>Port NCU24L simulatora (stvarni i kad je u konfiguraciji 0), ili null za serijski kontroler.</summary>
		public int? TcpPort => _tcpLockController?.Port;

		/// <summary>Port kontrolnog API-ja, ili null bez sekcije ControlApi.</summary>
		public int? ControlApiPort => _controlApi?.Port;

		/// <summary>Port koji otvara kiosk za serijski kontroler (Linux: symlink pty para; Windows: strana simulatora u com0com paru).</summary>
		public string? LockControllerPort { get; }

		/// <summary>Port koji otvara kiosk za skener.</summary>
		public string? BarcodeScannerPort { get; }

		/// <summary>Port koji otvara kiosk za POS terminal.</summary>
		public string? PosTerminalPort { get; }

		/// <summary>
		/// Na Linuxu uz <paramref name="kioskPortName"/>: pty par, a simulator otvara svoju stranu (upisuje se u
		/// <paramref name="comPort"/>). Vraća port koji otvara kiosk; bez para je to <c>ComPort.PortName</c> simulatora
		/// (com0com: kiosk otvara drugu stranu para, koju simulator ne zna).
		/// </summary>
		private string OpenSerial(ComPortConfiguration comPort, string? kioskPortName)
		{
			if (string.IsNullOrWhiteSpace(kioskPortName)) {
				return comPort.PortName;
			}
			if (!OperatingSystem.IsLinux()) {
				throw new PlatformNotSupportedException($"KioskPortName ({kioskPortName}) radi samo na Linuxu; na Windows-u par se pravi u com0com-u");
			}
			var pair = new PtySerialPair(kioskPortName, _logger);
			_serialPairs.Add(pair);
			comPort.PortName = pair.SimulatorPortName;
			return pair.KioskPortName;
		}

		private async Task OnReset()
		{
			_tcpLockController?.ResetNetworkModule();
			if (_posEngine != null) {
				await _posEngine.ResetAsync().ConfigureAwait(false);
			}
		}

		public async ValueTask DisposeAsync()
		{
			_controlApi?.Stop();
			_cts.Cancel();
			if (_tcpLockController != null) {
				await _tcpLockController.Completion.ConfigureAwait(false);
			}
			if (_lockController != null) {
				await _lockController.DisposeAsync().ConfigureAwait(false);
			}
			if (_barcodeScanner != null) {
				await _barcodeScanner.DisposeAsync().ConfigureAwait(false);
			}
			if (_posWire != null) {
				await _posWire.DisposeAsync().ConfigureAwait(false);
			}
			try {
				await _posRun.ConfigureAwait(false);
			}
			catch (OperationCanceledException) { }
			foreach (var pair in _serialPairs) {
				pair.Dispose();
			}
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

		// ==================== Serial Handlers ====================

		public (int, IEnumerable<DoorStatusCode>) GetDoorStatusMultiple(int moduleNumber)
		{
			var totalBoxCount = _configuration.Locker.ModuleCount * _configuration.Locker.BoxCountPerModule;
			if (moduleNumber < 1 || moduleNumber > _configuration.Locker.ModuleCount) {
				return (-1, Enumerable.Empty<DoorStatusCode>());
			}

			var allBoxes = _locker.Modules.SelectMany(m => m.Boxes).ToArray();

			var doorStatuses = new List<DoorStatusCode>();
			int rangeStart = (moduleNumber - 1) * _configuration.Locker.BoxCountPerModule + 1;
			for (int i = 0; i < _configuration.Locker.BoxCountPerModule; i++)
			{
				int n = rangeStart + i;
				if (n <= totalBoxCount) {
					var doorStatus = allBoxes.Single(box => box.Number == n).IsLocked? DoorStatusCode.Closed : DoorStatusCode.Open;
					doorStatuses.Add(doorStatus);
				}
				else {
					doorStatuses.Add(DoorStatusCode.Closed);
				}
			}
			return (moduleNumber, doorStatuses);
		}

		private DoorStatusCode? GetDoorStatusSingle(int doorNumber)
		{
			var box = FindLockBox(doorNumber);
			return box?.IsLocked == true? DoorStatusCode.Closed : DoorStatusCode.Open;
		}

		private DoorStatusCode? OpenDoorSingle(int doorNumber)
		{
			var box = FindLockBox(doorNumber);
			if (box == null) {
				return null;
			}
			_unlockLog.Record(doorNumber, unlocked: !box.IsBlocked);
			if (box.IsBlocked) {
				return box.IsLocked ? DoorStatusCode.Closed : DoorStatusCode.Open;
			}
			box.IsLocked = false;
			return DoorStatusCode.Open;
		}

		private LockResponse? MakeResponse(LockCommand command)
		{
			return Locked<LockResponse?>(() =>
			{
				// Isključen modul ne odgovara (kao NCU24L, isModuleOn): kiosk dobija istek vremena i prijavljuje stanje nepoznato ("K").
				int moduleIndex = command.Id == CommandId.CheckDoorStatusMultiple
									? command.Number - 1
									: (command.Number - 1) / _configuration.Locker.BoxCountPerModule;
				if (!(moduleIndex >= 0 && moduleIndex < _locker.Modules.Count && _locker.Modules[moduleIndex].IsOn)) {
					return null;
				}
				if (command.Id == CommandId.OpenDoorSingle) {
					var doorStatusCode = OpenDoorSingle(command.Number);
					if (doorStatusCode.HasValue && _lockFaults.Take(LockFault.DropOpenResponse)) {
						_logger?.LogWarning($"Kvar {LockFault.DropOpenResponse}: vrata {command.Number} su otvorena, odgovor se ne šalje.");
						return null;
					}
					return doorStatusCode.HasValue? new OpenDoorSingleResponse(command.Number, doorStatusCode.Value) : null;
				}
				if (command.Id == CommandId.CheckDoorStatusSingle) {
					var doorStatusCode = GetDoorStatusSingle(command.Number);
					return doorStatusCode.HasValue? new CheckDoorStatusSingleResponse(command.Number, doorStatusCode.Value) : null;
				}
				if (command.Id == CommandId.CheckDoorStatusMultiple) {
					var (module, doorStatusCodes) = GetDoorStatusMultiple(command.Number);
					return doorStatusCodes.Count() == _configuration.Locker.BoxCountPerModule? new CheckDoorStatusMultipleResponse(module, doorStatusCodes.ToArray()) : null;
				}
				return null;
			});
		}

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
