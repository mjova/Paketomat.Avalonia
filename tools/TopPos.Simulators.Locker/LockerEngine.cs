using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.Logging;
using TopPos.LockController;
using TopPos.LockController.Serial;
using TopPos.LockController.NCU24L;
using TopPos.LockerSimulator.Pos;
using TopPos.LockerSimulator.ViewModels;

namespace TopPos.LockerSimulator
{
	public partial class MainWindow : Window
	{
		private readonly LockController? _lockController;
		private readonly TcpLockController? _tcpLockController;
		private readonly BarcodeScanner _barcodeScanner;
		private readonly LockerViewModel _lockerViewModel;
		private readonly LockerSimulatorConfiguration _configuration;
		private readonly PosTerminalViewModel? _posTerminalViewModel;
		private PosTerminalWindow? _posTerminalWindow;
		private readonly UnlockLog _unlockLog = new();
		private readonly LockFaults _lockFaults = new();
		private readonly ControlApiServer? _controlApi;
		private readonly ILogger? _logger;

		public MainWindow(LockerSimulatorConfiguration config, ILogger? logger)
		{
			_configuration = config;
			_logger = logger;

			bool isNcu24L = string.Equals(config.LockController.Type, "NCU24L", StringComparison.OrdinalIgnoreCase);

			_barcodeScanner = new BarcodeScanner(config.BarcodeScanner, logger: null);

			InitializeComponent();
			RestoreWindowPosition();
			Closing += (_, _) => SaveWindowPosition();

			int numModules = Math.Max(1, _configuration.Locker.ModuleCount);
			int numBoxesPerModule = Math.Max(1, _configuration.Locker.BoxCountPerModule);
			var locker = Locker.MakeLocker(numModules, numBoxesPerModule);
			_lockerViewModel = new LockerViewModel(locker);

			var oneStar = new GridLength(1, GridUnitType.Star);
			// Row with module on/off switches
			_gridLocker.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto});
			// Boxes
			for (int box = 0; box < numBoxesPerModule; box++) {
				_gridLocker.RowDefinitions.Add(new RowDefinition { Height = oneStar});
			}
			for (int module = 0; module < numModules; module++) {
				_gridLocker.ColumnDefinitions.Add(new ColumnDefinition { Width = oneStar});
			}

			// Controls
			// Module switches (checkboxes)
			var dataTemplate = (DataTemplate) FindResource("moduleOnOffTemplate");
			for (int m = 0; m < numModules; m++) {
				var c = new ContentControl() {
					Content = _lockerViewModel.Modules[m],
					ContentTemplate = dataTemplate
				};
				c.SetValue(Grid.RowProperty, 0);
				c.SetValue(Grid.ColumnProperty, m);
				_gridLocker.Children.Add(c);
			}
			// Box controls
			for (int m = 0; m < numModules; m++) {
				for (int b = 0; b < numBoxesPerModule; b++) {
					var c = new LockBoxControl() { DataContext = _lockerViewModel.Modules[m].Boxes[b] };
					c.SetValue(Grid.RowProperty, b + 1);	// +1 because of the module switches row
					c.SetValue(Grid.ColumnProperty, m);
					_gridLocker.Children.Add(c);
				}
			}

			// Start the appropriate controller
			if (isNcu24L)
			{
				int port = config.LockController.TcpController?.Port ?? NCU24LProtocol.DEFAULT_PORT;
				var listenAddress = string.IsNullOrWhiteSpace(config.LockController.ListenAddress)
										? System.Net.IPAddress.Any
										: System.Net.IPAddress.Parse(config.LockController.ListenAddress);
				_tcpLockController = new TcpLockController(listenAddress, port, HandleNcu24LCommand, GetNcu24LLockStates, CancellationToken.None, logger,
					isModuleOn: addr => Dispatcher.Invoke(() => addr < _lockerViewModel.Modules.Count && _lockerViewModel.Modules[addr].IsOn),
					networkModule: config.LockController.NetworkModule,
					dropUnlockResponse: () => _lockFaults.Take(LockFault.DropOpenResponse));
				this.Title = $"Locker Simulator [NCU24L TCP:{port}{(config.LockController.NetworkModule.Enabled ? ", ZLG" : "")}]";
			}
			else
			{
				_lockController = new LockController(config.LockController.ComPort, MakeResponse, CancellationToken.None, logger);
				this.Title = $"Locker Simulator [Serial {config.LockController.ComPort.PortName}]";
			}

			// POS terminal radi od starta (kiosk pri pokretanju proverava prisustvo terminala), prozor je samo prikaz.
			PosTerminalEngine? posEngine = null;
			if (config.PosTerminal != null) {
				var posWire = new SerialPosWire(config.PosTerminal.ComPort, logger);
				posEngine = new PosTerminalEngine(posWire, new PosSimulatorOptions {
					Pin = config.PosTerminal.Pin,
					Tid = config.PosTerminal.Tid,
					CardWaitTimeout = TimeSpan.FromSeconds(config.PosTerminal.CardWaitTimeoutSeconds),
					SpeedFactor = config.PosTerminal.SpeedFactor,
				}, logger);
				_posTerminalViewModel = new PosTerminalViewModel(posEngine, posWire, Dispatcher);
				posWire.Start();
				_ = posEngine.RunAsync(CancellationToken.None);
				this.Title += $" [POS {config.PosTerminal.ComPort.PortName}]";
			}
			else {
				_buttonPos.IsEnabled = false;
			}

			if (config.ControlApi != null) {
				_controlApi = new ControlApiServer(config.ControlApi.Port, _lockerViewModel, _barcodeScanner, posEngine, _posTerminalViewModel,
													_unlockLog, _lockFaults, Dispatcher, logger);
				_controlApi.Start();
				this.Title += $" [API :{config.ControlApi.Port}]";
				Closed += (_, _) => _controlApi.Stop();
			}
		}

		private void OnShowPosTerminalWindow(object sender, RoutedEventArgs e)
		{
			if (_posTerminalViewModel == null) {
				return;
			}
			if (_posTerminalWindow != null) {
				_posTerminalWindow.Activate();
				return;
			}
			// Nije modalan: gleda se i koristi dok kiosk plaća.
			_posTerminalWindow = new PosTerminalWindow(_posTerminalViewModel) { Owner = this };
			_posTerminalWindow.Closed += (_, _) => _posTerminalWindow = null;
			_posTerminalWindow.Show();
		}

		private void SetAllLockedState(bool isLocked)
		{
			foreach (var m in _lockerViewModel.Modules) {
				foreach (var b in m.Boxes) {
					b.IsLocked = isLocked;
				}
			}
		}

		private void SetAllBlockedState(bool isBlocked)
		{
			foreach (var m in _lockerViewModel.Modules) {
				foreach (var b in m.Boxes) {
					b.IsBlocked = isBlocked;
				}
			}
		}

		private void OnOpenAll(object sender, RoutedEventArgs e) => SetAllLockedState(false);

		private void OnCloseAll(object sender, RoutedEventArgs e) => SetAllLockedState(true);

		private void OnBlockAll(object sender, RoutedEventArgs e) => SetAllBlockedState(true);

		private void OnUnblockAll(object sender, RoutedEventArgs e) => SetAllBlockedState(false);

		private void OnShowBarcodeScannerWindow(object sender, RoutedEventArgs e)
		{
			var w = new BarCodeScannerWindow() 
			{ 
				DataContext = new BarcodeScannerViewModel(_barcodeScanner),
				Owner = this,
				WindowStartupLocation = WindowStartupLocation.CenterOwner,
				ShowInTaskbar = false
			};
			w.ShowDialog();
		}

		private LockBoxViewModel? FindLockBox(int number) 
			=> _lockerViewModel.Modules.SelectMany(m => m.Boxes).FirstOrDefault(box => box.Number == number);

		private LockBoxViewModel? FindLockBoxesInRange(int number) 
			=> _lockerViewModel.Modules.SelectMany(m => m.Boxes).FirstOrDefault(box => box.Number == number);

		public (int, IEnumerable<DoorStatusCode>) GetDoorStatusMultiple(int moduleNumber)
		{
			var totalBoxCount = _configuration.Locker.ModuleCount * _configuration.Locker.BoxCountPerModule;
			if (moduleNumber < 1 || moduleNumber > _configuration.Locker.ModuleCount) {
				return (-1, Enumerable.Empty<DoorStatusCode>());
			}

			var allBoxes = _lockerViewModel.Modules.SelectMany(m => m.Boxes).ToArray();

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

		// ==================== NCU24L Handlers ====================

		/// <summary>
		/// Handle NCU24L command. NCU address maps to module index, lockNum maps to box within module.
		/// </summary>
		private NCU24LResponseCode HandleNcu24LCommand(byte ncuAddress, byte lockNum, NCU24LCommand cmd)
		{
			return Dispatcher.Invoke(() =>
			{
				if (cmd == NCU24LCommand.Unlock)
				{
					if (lockNum == NCU24LProtocol.ALL_LOCKS)
					{
						// Unlock all locks on this NCU (skip blocked)
						if (ncuAddress < _lockerViewModel.Modules.Count)
						{
							foreach (var box in _lockerViewModel.Modules[ncuAddress].Boxes) {
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
			return Dispatcher.Invoke(() =>
			{
				var states = new byte[3];

				if (ncuAddress >= _lockerViewModel.Modules.Count)
					return states; // All zeros = all open

				var boxes = _lockerViewModel.Modules[ncuAddress].Boxes;
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

		// ==================== Serial Handlers ====================

		private void RestoreWindowPosition()
		{
			var settings = WindowSettings.Load();
			if (settings == null)
			{
				WindowStartupLocation = WindowStartupLocation.CenterScreen;
				return;
			}

			if (!settings.IsVisibleOnAnyScreen())
			{
				WindowStartupLocation = WindowStartupLocation.CenterScreen;
				return;
			}

			Left = settings.Left;
			Top = settings.Top;
			Width = settings.Width;
			Height = settings.Height;

			if (settings.WindowState == WindowState.Minimized)
				WindowState = WindowState.Normal;
			else
				WindowState = settings.WindowState;
		}

		private void SaveWindowPosition()
		{
			var settings = new WindowSettings();

			if (WindowState == WindowState.Normal)
			{
				settings.Left = Left;
				settings.Top = Top;
				settings.Width = Width;
				settings.Height = Height;
			}
			else
			{
				settings.Left = RestoreBounds.Left;
				settings.Top = RestoreBounds.Top;
				settings.Width = RestoreBounds.Width;
				settings.Height = RestoreBounds.Height;
			}
			settings.WindowState = WindowState;
			settings.Save();
		}

		private LockResponse? MakeResponse(LockCommand command)
		{
			// Isključen modul ne odgovara (kao NCU24L, isModuleOn): kiosk dobija istek vremena i prijavljuje stanje nepoznato ("K").
			int moduleIndex = command.Id == CommandId.CheckDoorStatusMultiple
								? command.Number - 1
								: (command.Number - 1) / _configuration.Locker.BoxCountPerModule;
			if (!Dispatcher.Invoke(() => moduleIndex >= 0 && moduleIndex < _lockerViewModel.Modules.Count && _lockerViewModel.Modules[moduleIndex].IsOn)) {
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
		}
	}
}