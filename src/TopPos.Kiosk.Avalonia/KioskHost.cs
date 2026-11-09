using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Flatsoft.Common;
using Flatsoft.Common.UI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;
using Serilog.Formatting.Compact;
using TopPos.Kiosk.Logging;
using TopPos.Kiosk.ViewModels;
using TopPos.Kiosk.Workflow;
using TopPos.LockController;
using TopPos.LockController.NCU16L;
using TopPos.LockController.NCU24L;
using TopPos.LockController.SerialDailyExpress;
using TopPos.PenPosService;
using TopPos.Platform;
using TopPos.Service;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace TopPos.Kiosk.UI
{
	/// <summary>
	/// Kompozicija i životni ciklus kioska — v2 <c>App.OnStartup</c>, <c>OnSessionEnding</c>/<c>OnExit</c> i <c>CompleteShutdown</c>,
	/// istim redosledom. Razlike: putanje iz <see cref="IAppPaths"/>, provere uređaja van UI niti (v2: <c>.Result</c> na UI niti,
	/// prozor tek posle njih), kraj procesa i sa SIGTERM-a (<see cref="ShutdownSignal"/>). Agent kanal dolazi u F2.
	/// </summary>
	internal sealed class KioskHost(IClassicDesktopStyleApplicationLifetime lifetime, MainWindow window)
	{
		private readonly IAppPaths _paths = PlatformServices.AppPaths;

		private ILogger? _logger;
		private IDisposable? _singleInstance;
		private ShutdownSignal? _shutdownSignal;
		private StatusSemaphore? _statusSemaphore;
		private KioskStatusSource? _statusSource;
		private KioskOperationCoordinator? _coordinator;
		private ILockControllerClient? _lockClient;
		private MainWindowViewModel? _viewModel;
		private IDisposable? _audioPlayer;
		// Neuspela inicijalizacija se završava kroz CompleteShutdown, ali nije uredno gašenje — CleanShutdown ostaje false.
		private bool _startupFailed;
		// Gašenje je završeno; samo UI nit.
		private bool _shutdownCompleted;
		// Zatvaranje veze sa kontrolerom je počelo; samo UI nit.
		private LockControllerShutdown? _lockControllerShutdown;

		public async Task StartAsync()
		{
			try {
				await StartCoreAsync();
			}
			catch (Exception ex) {
				_startupFailed = true;
				Log.Fatal(ex, $"Inicijalizacija aplikacije nije uspela. Detalji: {ex.GetExceptionMessages()}");
				window.ShowStartupMessage($"Inicijalizacija aplikacije nije uspela: {ex.Message}");
				// Kiosk ne ostaje u stanju bez toka: izlaz sa greškom, ponovni start je posao nadzora (systemd, Agent).
				await Task.Delay(TimeSpan.FromSeconds(15));
				lifetime.Shutdown(1);
			}
		}

		private async Task StartCoreAsync()
		{
			var appConfiguration = Utility.ReadConfiguration<AppConfiguration>(ConfigFile("app-configuration.json"), out var exception) ??
									throw new Exception("Citanje konfiguracije nije uspelo.", exception);

			// Obavezne sekcije se validiraju, a opcione (koje starije konfiguracije nemaju) dobijaju podrazumevane vrednosti.
			var defaultedConfigSections = appConfiguration.NormalizeAndValidate();
			ResolveStatePaths(appConfiguration);

#if DEBUG
			// Simulatori (samo Debug): app-configuration.Simulator.json postoji → uključeni po sekcijama (Simulators/).
			const string simulatorFile = "app-configuration.Simulator.json";
			SimulatorConfiguration? simulators = null;
			if (File.Exists(ConfigFile(simulatorFile))) {
				simulators = Utility.ReadConfiguration<SimulatorConfiguration>(ConfigFile(simulatorFile), out var simException) ??
								throw new Exception($"Citanje {simulatorFile} nije uspelo.", simException);
			}
			const string screenResolutionFile = "app-configuration.ScreenResolution.json";
			if (File.Exists(ConfigFile(screenResolutionFile))) {
				appConfiguration.ScreenResolution = Utility.ReadConfiguration<ScreenResolutionConfiguration>(ConfigFile(screenResolutionFile), out var srException) ??
													throw new Exception($"Citanje {screenResolutionFile} nije uspelo.", srException);
			}
#endif

			// Kontekst pre loga: upisuje se na početku svakog log fajla.
			var appContext = CreateApplicationContext(appConfiguration);
			SerilogHooks.RollingFileHeaderWriter.ApplicationContext = appContext;
			// Referencirano iz serilog.json
			Environment.SetEnvironmentVariable("TOPPOS_LOGFILE_TAG", $"{appContext.LockerConfiguration.LockerId}-{appContext.GitHash?[^Math.Min(16, appContext.GitHash.Length)..]}");
			Environment.SetEnvironmentVariable("TOPPOS_LOG_DIR", _paths.LogDirectory);

			_singleInstance = PlatformServices.SingleInstanceGuard.TryAcquire();

			var serilogLogger = CreateSerilogLogger();
			Log.Logger = serilogLogger;

			if (_singleInstance == null) {
				Log.Warning("Druga instanca aplikacije je pokusala da se pokrene. Gasi se.");
				Log.CloseAndFlush();
				_startupFailed = true;
				lifetime.Shutdown(0);
				return;
			}

			Log.Information("Inicijalizacija.");
			Log.Information("Folderi: program {Program}, konfiguracija {Configuration}, stanje {State}, logovi {Logs}.",
							_paths.ProgramDirectory, _paths.ConfigurationDirectory, _paths.StateDirectory, _paths.LogDirectory);
			if (defaultedConfigSections.Count > 0) {
				Log.Warning("Konfiguracija: sekcije [{Sections}] ne postoje u app-configuration.json (verovatno starija konfiguracija) — koriste se podrazumevane vrednosti.",
							string.Join(", ", defaultedConfigSections));
			}

			// Broj pretinaca ne mora biti deljiv sa brojem vrata po modulu, ali ne sme preći granicu tipa kontrolera.
			string? boxCountError = LockControllerTypes.ValidateDoorCount(appConfiguration.Locker.ControllerType, appConfiguration.Locker.LockerBoxCount);
			if (boxCountError != null) {
				throw new Exception($"Neispravan broj pretinaca u konfiguraciji: {appConfiguration.Locker.LockerBoxCount}. {boxCountError}");
			}

			var loggerFactory = new SerilogLoggerFactory(serilogLogger);
			ILogger microsoftLogger = loggerFactory.CreateLogger(string.Empty);
			_logger = microsoftLogger;

			// ECR log uz ostale logove (v2: Log\ uz program).
			ECR.Configuration.logDirectory = Path.Combine(_paths.LogDirectory, "ECR");

			// status.lock: ishod prethodne sesije u log, pa početni status ove — pre uređaja, da fajl postoji i kad kontroler
			// nije povezan. Agent kanal je u F2 isključen (izjava bez kanala).
			var statusDeclaration = StatusDeclaration.ForCurrentProcess(agentPipeName: null, microsoftLogger);
			var statusSemaphore = new StatusSemaphore(_paths.StateDirectory, microsoftLogger, statusDeclaration);
			statusSemaphore.InitializeSession();
			_statusSemaphore = statusSemaphore;

			var statusSource = new KioskStatusSource(appConfiguration.Locker.LockerId, microsoftLogger);
			statusSource.Subscribe(status => statusSemaphore.Update(status.Busy, status.WorkflowState, status.Operational, status.Role));
			_statusSource = statusSource;
			var coordinator = new KioskOperationCoordinator(statusSource, microsoftLogger);
			_coordinator = coordinator;

			ILockControllerClient lockClient = appConfiguration.Locker.ControllerType switch {
				LockControllerTypes.NCU16L => CreateNcu16LController(appConfiguration, microsoftLogger),
				LockControllerTypes.NCU24L => CreateNcu24LController(appConfiguration, microsoftLogger),
				LockControllerTypes.Serial => CreateSerialController(appConfiguration, microsoftLogger),
				LockControllerTypes.SerialDailyExpress => CreateSerialDailyExpressController(appConfiguration, microsoftLogger),
				_ => throw new Exception($"Nepoznat tip kontrolera: {appConfiguration.Locker.ControllerType}")
			};
			_lockClient = lockClient;

#if DEBUG
			// Uz Debug pokrenuti TopPos.Debug.Heartbeat (v2) ako heartbeat treba serveru.
			appConfiguration.TopPosService.DisableHeartbeats = true;
#endif
			var serviceClient = new TopPosServiceClient(appConfiguration.TopPosService, appConfiguration.Locker.LockerId, microsoftLogger, CancellationToken.None);

			// Skener je podrazumevano u komandnom režimu (ne prima barkodove do AcceptBarcodes).
			var barcodeScannerClient = new BarcodeScannerClient(appConfiguration.BarcodeScanner, microsoftLogger, CancellationToken.None);
			using (var ctsCheckScanner = new CancellationTokenSource(TimeSpan.FromSeconds(10))) {
				bool scannerPresent = await Task.Run(async () => await barcodeScannerClient.CheckPresence(ctsCheckScanner.Token));
				if (!scannerPresent) {
					microsoftLogger.LogError($"Barkod skener nije dostupan na portu {appConfiguration.BarcodeScanner.ComPort.PortName}. Barkod funkcionalnost nece biti na raspolaganju.");
				}
				else {
					appConfiguration.General.IsBarcodeScannerAvailable = true;
				}
			}
			barcodeScannerClient.AcceptBarcodes();

			IPosClient? posClient = null;
			if (appConfiguration.PosTerminal.TerminalInstalled) {
#if DEBUG
				if (simulators?.Pos != null) {
					posClient = new SimulatedPosClient(simulators.Pos, microsoftLogger);
					microsoftLogger.LogWarning($"POS terminal: koristi se SIMULATOR iz {simulatorFile} (scenario={simulators.Pos.Scenario}). Pravi ECR/serijski terminal NIJE u upotrebi.");
				}
				else
#endif
				{
					var realPosClient = new PosClient(appConfiguration.PosTerminal, microsoftLogger);
					using var ctsCheckPos = new CancellationTokenSource(TimeSpan.FromSeconds(10));
					bool posPresent = await Task.Run(async () => await realPosClient.CheckPresence(ctsCheckPos.Token));
					if (!posPresent) {
						microsoftLogger.LogError($"POS uredjaj trenutno nije dostupan na konfigurisanom portu {appConfiguration.PosTerminal.ComPort.PortName}. POS funkcionalnost ce zavisiti od raspolozivosti uredjaja u datom trenutku.");
					}
					posClient = realPosClient;
				}
			}

			// PenPOS je opciona funkcionalnost — bez sekcije PenPosService je isključen.
			var penPosClient = appConfiguration.PenPosService?.IsEnabled == true ? new PenPosServiceClient(appConfiguration.PenPosService, microsoftLogger, CancellationToken.None) : null;
			if (penPosClient != null) {
				using var ctsCheckPenPos = new CancellationTokenSource(TimeSpan.FromSeconds(30));
				var result = await Task.Run(async () => await penPosClient.Initialize(ctsCheckPenPos.Token));
				if (result == ProcessorResultCode.PinOK) {
					appConfiguration.General.IsPenPOSDeviceAvailable = true;
					microsoftLogger.LogInformation("PenPOS uredjaj inicijalizovan.");
				}
				else if (result?.Severity == ProcessorResultCode.CodeSeverity.Error) {   // npr. pogrešan PIN, zaključan sigurnosni element
					penPosClient = null;
					microsoftLogger.LogError($"PenPOS uredjaj nije dostupan [{result.Code}]. PenPOS funkcionalnost nece biti na raspolaganju.");
				}
			}

			var langManager = new LanguageManager(microsoftLogger);
			langManager.LoadLanguageResources(Path.Combine(_paths.ProgramDirectory, "Languages"), ".lang", "sr");
			string brandDirectory = Path.Combine(_paths.ProgramDirectory, "Brands", appConfiguration.General.Brand);
			langManager.LoadBrandOverrides(brandDirectory, ".lang");

			var audioPlayer = PlatformServices.CreateAudioPlayer(_paths.ProgramDirectory, microsoftLogger);
			_audioPlayer = audioPlayer as IDisposable;

			AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
			TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
			Dispatcher.UIThread.UnhandledException += OnDispatcherUnhandledException;

			// Prikaz: tema brenda i ViewModel na UI niti (posle await-a smo na njoj — StartAsync je pozvan sa UI niti).
			BrandTheme.Apply(brandDirectory, App.Current!.Resources);
			_viewModel = new MainWindowViewModel(appConfiguration, lockClient, barcodeScannerClient, serviceClient, posClient, penPosClient,
												 langManager, statusSource, coordinator, audioPlayer, App.FindLanguageImage, microsoftLogger);
			window.Attach(_viewModel, appConfiguration.ScreenResolution);

			// systemd stop (SIGTERM) i Ctrl+C: uredan izlazak na UI niti, kao kraj Windows sesije u v2.
			_shutdownSignal = new ShutdownSignal(signal => Dispatcher.UIThread.Post(() => {
				_logger?.LogInformation("Signal {Signal}: gašenje programa.", signal);
				lifetime.Shutdown(0);
			}));

			statusSource.SetLifecycle(Agent.Contracts.KioskLifecycles.Ready);
		}

		/// <summary>
		/// Zahtev za gašenje od sistema (Windows: <c>WM_QUERYENDSESSION</c> — kraj sesije; zatvaranje prozora). Kao v2
		/// <c>OnSessionEnding</c>: gašenje se završi sinhrono, pre odgovora Windows-u, jer posle <c>WM_ENDSESSION</c> proces sme da
		/// nestane u svakom trenutku.
		/// </summary>
		public void OnShutdownRequested(ShutdownRequestedEventArgs e)
		{
			_logger?.LogInformation("Zahtev sistema za gašenje (kraj sesije ili zatvaranje prozora): gašenje programa pre odgovora.");
			CompleteShutdown();
		}

		/// <summary>
		/// Uredan izlazak: prvo gašenje u status, pa zaustavljanje provere vrata, pa zatvaranje veze sa kontrolerom (posle
		/// odgovora na komandu u toku, <see cref="LockControllerShutdown"/>), pa završni upis urednog gašenja u status.lock dok
		/// logger još radi. Ponovljen poziv ne radi ništa; ugnežđen (iz čekanja koje obrađuje poruke) završava preostale korake.
		/// </summary>
		public void CompleteShutdown()
		{
			if (_shutdownCompleted) {
				return;
			}

			_statusSource?.SetLifecycle(Agent.Contracts.KioskLifecycles.ShuttingDown);
			_coordinator?.Close();      // nove sesije i operacije se više ne primaju

			if (_lockClient != null) {
				if (_lockControllerShutdown == null) {
					_logger?.LogInformation("Izlazak iz aplikacije: zatvaranje veze sa kontrolerom vrata.");
				}
				_viewModel?.StopDoorWatcher();
				_lockControllerShutdown ??= new LockControllerShutdown(_lockClient, _logger);
				_lockControllerShutdown.Close();
			}

			if (!_startupFailed) {
				_statusSemaphore?.MarkCleanShutdown();
			}

			_audioPlayer?.Dispose();
			_shutdownSignal?.Dispose();
			Log.CloseAndFlush();
			_singleInstance?.Dispose();
			_shutdownCompleted = true;
		}

		private string ConfigFile(string fileName) => Path.Combine(_paths.ConfigurationDirectory, fileName);

		/// <summary>Relativna imena fajlova stanja iz konfiguracije → folder stanja (v2: radni folder = folder programa).</summary>
		private void ResolveStatePaths(AppConfiguration configuration)
		{
			string state = _paths.StateDirectory;
			configuration.PaymentConfirmationSender.PaymentConfirmationFilename = Path.Combine(state, configuration.PaymentConfirmationSender.PaymentConfirmationFilename);
			configuration.ServerConfirmationSender.ServerConfirmationFilename = Path.Combine(state, configuration.ServerConfirmationSender.ServerConfirmationFilename);
			configuration.LockerStatusUpdater.ServerStatusFilename = Path.Combine(state, configuration.LockerStatusUpdater.ServerStatusFilename);
			configuration.AgentCommunication.OperationJournalFolder = Path.Combine(state, configuration.AgentCommunication.OperationJournalFolder);
		}

		private ApplicationContext CreateApplicationContext(AppConfiguration configuration)
		{
			var entryAssembly = Assembly.GetEntryAssembly()!;
			// Sadržaj paketomat.ver (uz program) se ispisuje iza imena programa u zagradama.
			string? versionLabel = null;
			string versionFilePath = Path.Combine(_paths.ProgramDirectory, "paketomat.ver");
			if (File.Exists(versionFilePath)) {
				versionLabel = File.ReadAllText(versionFilePath).Trim();
			}
			return new ApplicationContext {
				ApplicationName = entryAssembly.GetName().Name,
				VersionLabel = versionLabel,
				ApplicationVersion = entryAssembly.GetName().Version!.ToString(),
				GitHash = entryAssembly.GetCustomAttribute<GitHashAttribute>()?.Hash ?? "unknown",
				NetFrameworkDescription = RuntimeInformation.FrameworkDescription,
				LockerConfiguration = configuration.Locker,
				TopPosServiceConfiguration = configuration.TopPosService,
				PosClientConfiguration = configuration.PosTerminal,
				BarcodeScannerConfiguration = configuration.BarcodeScanner
			};
		}

		private Serilog.ILogger CreateSerilogLogger()
		{
			var configuration = new ConfigurationBuilder()
									.AddJsonFile(ConfigFile("serilog.json"), optional: false, reloadOnChange: true)
									.Build();
			return new LoggerConfiguration()
						.ReadFrom.Configuration(configuration)
						.Enrich.With(new RemoveSourceContextEnricher())
#if DEBUG
						.WriteTo.Seq("http://localhost:5341", payloadFormatter: new CompactJsonFormatter())
#endif
						.Enrich.FromLogContext()
						.CreateLogger();
		}

		private void OnDispatcherUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
			=> Log.Error($"{nameof(OnDispatcherUnhandledException)}: UI thread exception: {e.Exception.GetExceptionMessages()}");

		private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
		{
			if (e.ExceptionObject is Exception ex) {
				Log.Error($"{nameof(OnUnhandledException)}: non-UI thread exception: {ex.GetExceptionMessages()}");
			}
			else {
				Log.Error($"{nameof(OnUnhandledException)}: unknown error occured on a non-UI thread: {e.ExceptionObject}");
			}
		}

		private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
		{
			Log.Error($"{nameof(OnUnobservedTaskException)}: an unobserved task exception: {e.Exception.GetExceptionMessages()}");
			e.SetObserved();    // proces ne pada
		}

		private static ILockControllerClient CreateNcu16LController(AppConfiguration appConfiguration, ILogger logger)
		{
			Log.Information($"Kreiranje Ncu16L lock kontrolera: {appConfiguration.Locker.TcpController.IpAddress}:{appConfiguration.Locker.TcpController.Port}");
			return new LockControllerClientNCU16L(appConfiguration.Locker.TcpController, logger);
		}

		private static ILockControllerClient CreateNcu24LController(AppConfiguration appConfiguration, ILogger logger)
		{
			Log.Information($"Kreiranje Ncu24L lock kontrolera: {appConfiguration.Locker.TcpController.IpAddress}:{appConfiguration.Locker.TcpController.Port}");
			return new LockControllerClientNCU24L(appConfiguration.Locker.TcpController, logger);
		}

		private static ILockControllerClient CreateSerialController(AppConfiguration appConfiguration, ILogger logger)
		{
			Log.Information($"Kreiranje serijskog lock kontrolera: {appConfiguration.Locker.ComPort.PortName}");
			return new LockController.Serial.LockControllerClientSerial(appConfiguration.Locker.ComPort, logger);
		}

		private static ILockControllerClient CreateSerialDailyExpressController(AppConfiguration appConfiguration, ILogger logger)
		{
			Log.Information($"Kreiranje DailyExpress serijskog lock kontrolera: {appConfiguration.Locker.ComPort.PortName}");
			return new LockControllerClientSerialDailyExpress(appConfiguration.Locker.ComPort, logger);
		}
	}
}