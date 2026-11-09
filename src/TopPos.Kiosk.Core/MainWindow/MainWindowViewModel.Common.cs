using Flatsoft.Common;
using Flatsoft.Common.UI;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TopPos.Client.WPF.AgentCommunication;
using TopPos.Client.WPF.Pages;
using TopPos.Client.WPF.Windows;
using TopPos.Client.WPF.Workflow;
using TopPos.LockController;
using TopPos.PenPosService;
using TopPos.Service;
using static TopPos.LockController.DoorWatcher;

namespace TopPos.Client.WPF.ViewModels
{
	public partial class MainWindowViewModel : ViewModelBase
	{
		private readonly AppConfiguration _configuration;
		private readonly ILockControllerClient _lockControllerClient;
		private readonly TopPosServiceClient _serviceClient;
		private readonly BarcodeScannerClient _barcodeScannerClient;
		private readonly IPosClient? _posClient;
		private readonly PenPosServiceClient? _penPosServiceClient;
		private readonly LanguageManager _languageManager;
		private readonly StartViewModel _startViewModel;
		private readonly StartPage _startPage;
		private readonly CourierBarcodeScanViewModel _courierBarcodeScanViewModel;
		private readonly CourierBarcodeScanPage _courierBarcodeScanPage;
		private readonly OperationCountdownTimer _operationCountdownTimer;
		private readonly DoorStatusTracker _doorStatusTracker;
		private readonly BarcodeScanTracker _barcodeScanTracker;
		private IWorkflowViewModel? _currentWorkflowViewModel;
		private readonly DoorWatcher _doorWatcher;
		private readonly LockerStatusUpdater _lockerStatusUpdater;
		private readonly PaymentConfirmationSender _paymentConfirmationSender;
		private readonly ServerConfirmationSender _serverConfirmationSender;
		private readonly AudioPlayer _audioPlayer;
		private CancellationTokenSource? _ctsCheckDeviceStatus;

		private readonly WorkflowContext _workflowContext;
		private readonly WorkflowMachine _workflowMachine;
		private readonly KioskStatusSource _statusSource;
		private readonly KioskOperationCoordinator _coordinator;
		// DoorWatcher prati vlasništvo (radi samo dok kiosk nema vlasnika); Pause/Resume po vlasniku idu pod ovim lock-om.
		private readonly object _doorWatcherSync = new();
		private bool _doorWatcherStopped;
		// Poruka o stanju paketomata (inicijalizacija, van funkcije) — povratak operativnosti skida samo nju, ne poruku toka.
		private volatile string? _lockerStatusMessage;

		// Faza 5 — per-flow activatori (drže svoje stranice/VM-ove i OnEntry handlere).
		private readonly RecipientFlowActivator _recipientActivator;
		private readonly CourierLoginActivator _courierLoginActivator;
		private readonly CourierPickupActivator _courierPickupActivator;
		private readonly CourierDepositActivator _courierDepositActivator;
		private readonly SenderDepositActivator _senderDepositActivator;
		private readonly DepositConfirmationFailedActivator _depositConfirmationFailedActivator;
		private readonly RemoteServiceActivator _remoteServiceActivator;

		public MainWindowViewModel(AppConfiguration configuration,
									ILockControllerClient lockControllerClient,
									BarcodeScannerClient barcodeScannerClient,
									TopPosServiceClient serviceClient,
									IPosClient? posClient,
									PenPosServiceClient? penPosServiceClient,
									LanguageManager langManager,
									KioskStatusSource statusSource,
									KioskOperationCoordinator coordinator,
									ILogger? logger)
			: base(langManager, logger)
		{
			_configuration = configuration;
			_lockControllerClient = lockControllerClient;
			_barcodeScannerClient = barcodeScannerClient;
			_serviceClient = serviceClient;
			_posClient = posClient;
			_penPosServiceClient = penPosServiceClient;
			_languageManager = langManager;

			// Workflow state machine — jedinstveni izvor istine o tranzicijama. Side-effect logika
			// (otvaranje vrata, server pozivi, audio, timer) ostaje u GoTo* metodama; one se
			// zakače kao OnEntry handler-i u WireWorkflowMachine() koja se zove pre _doorWatcher.Resume().
			_workflowContext = new WorkflowContext();
			_workflowMachine = new WorkflowMachine(
									_workflowContext,
									enableLockerBoxAvailabilityPage: () => _configuration.General.EnableLockerBoxAvailabilityPage,
									confirmPackagePickup: () => _configuration.General.ConfirmPackagePickup,
									isPenPosDeviceAvailable: () => _configuration.General.IsPenPOSDeviceAvailable,
									logger);

			// Vlasnik je App (životni ciklus; iz njega se izvode status.lock i status za Agenta); ovde se samo ažurira na
			// prelaze i promene operativnosti.
			_statusSource = statusSource;
			_coordinator = coordinator;

			_operationCountdownTimer = new(TimeSpan.FromMilliseconds(_configuration.General.OperationTimeout), logger) {
				// Reset samo ako je to odbrojavanje i dalje važeće kad dođe na red: istek koji je krenuo baš kad ga je ekran
				// zaustavio (ili se sesija završila — Start ga zaustavlja) ne sme da prekine sledeći ekran ili sledeću sesiju.
				NotifyCountdownCompleted = stillCurrent => new(_workflowMachine.FireGuardedAsync(stillCurrent, WorkflowTrigger.Reset))
			};

			_doorStatusTracker = new(_lockControllerClient,
										TimeSpan.FromMilliseconds(_configuration.General.OperationTimeout),
										TimeSpan.FromMilliseconds(_configuration.General.DoorStatusResponseTimeout),
										logger);

			_barcodeScanTracker = new BarcodeScanTracker(_barcodeScannerClient, TimeSpan.FromMilliseconds(_configuration.BarcodeScanner.ScanTrackerTimeoutInMilliseconds),
														_configuration.BarcodeScanner.MinimumBarcodeLength, logger);

			_serviceClient.OnRequestStart = () => this.ShowProgressBar = true;
			_serviceClient.OnRequestEnd = () => this.ShowProgressBar = false;

			_steps = [ new StepViewModel(langManager, "MainArea.Step1"),
							new StepViewModel(langManager, "MainArea.Step2"),
							new StepViewModel(langManager, "MainArea.Step3"),
							new StepViewModel(langManager, "MainArea.Step4") ];
			SetCurrentStep(0);

			_startViewModel = SetUpStartPageViewModel();
			_startPage = new StartPage() { DataContext = _startViewModel };








			_courierBarcodeScanViewModel = SetUpCourierBarcodeScanViewModel();
			_courierBarcodeScanPage = new CourierBarcodeScanPage { DataContext = _courierBarcodeScanViewModel };


			_recipientActivator = new RecipientFlowActivator(this);
			_courierLoginActivator = new CourierLoginActivator(this);
			_courierPickupActivator = new CourierPickupActivator(this);
			_courierDepositActivator = new CourierDepositActivator(this);
			_senderDepositActivator = new SenderDepositActivator(this);
			_depositConfirmationFailedActivator = new DepositConfirmationFailedActivator(this);
			_remoteServiceActivator = new RemoteServiceActivator(this);

			this.LanguageSelectorViewModel = new LanguageSelectorViewModel(langManager,
															langImageResourceName => langImageResourceName != null ?
															(DrawingGroup?) Application.Current.TryFindResource(langImageResourceName) : null,
															logger);
			this.LanguageSelectorViewModel.HandleLanguageSelected = languageId => {
				this.ShowLanguageSelector = false;
				this.CurrentLanguageFlag = (DrawingGroup?) Application.Current.TryFindResource(_languageManager.CurrentLanguage!.ImageResourceName);
			};
			this.ShowLanguageSelectorCommand = new RelayCommand(o => { this.ShowLanguageSelector = true; });
			this.CurrentLanguageFlag = (DrawingGroup?) Application.Current.TryFindResource(_languageManager.CurrentLanguage!.ImageResourceName);

			this.GoToStartPageCommand = new AsyncCommand<bool>(async (o, ct) => {
				_logger?.LogDebug($"{nameof(MainWindowViewModel)}: executing {nameof(GoToStartPageCommand)}.");
				await FireResetAsync(); return true;
			});

			this.DisplayDeviceStatusCommand = new AsyncCommand<bool>(async (o, ct) => {
				_logger?.LogDebug($"{nameof(MainWindowViewModel)}: executing {nameof(DisplayDeviceStatusCommand)}.");
				await FireResetAsync();
				// Prozor je lokalna servisna operacija: kiosk je zauzet (uloga Service) dok je otvoren, a skener i Agent se odbijaju.
				if (!await _coordinator.WaitUntilFreeAsync(TimeSpan.FromSeconds(5))) {
					_logger?.LogWarning($"{nameof(DisplayDeviceStatusCommand)}: prozor statusa uređaja se ne otvara — prethodna sesija se nije završila za 5 s.");
					return true;
				}
				var operation = _coordinator.TryBegin(_workflowMachine, KioskLeaseMode.StandaloneOperation, WorkflowStatusRules.ServiceRole, "prozor statusa uređaja");
				if (operation == null || !_coordinator.TryActivate(operation)) {
					if (operation != null) {
						_coordinator.ReleaseIfReserved(operation);
					}
					_logger?.LogWarning($"{nameof(DisplayDeviceStatusCommand)}: prozor statusa uređaja se ne otvara — kiosk je zauzet.");
					return true;
				}
				try {
					// Provere uređaja su posao operacije: End posle zatvaranja oslobađa kiosk tek kad se (otkazane) provere završe.
					var deviceStatusWindow = new DeviceStatusWindow(_barcodeScannerClient, _posClient!, _languageManager, _logger!,
																	checks => _coordinator.TryRun(operation, checks), CancellationToken.None);
					deviceStatusWindow.Owner = Application.Current.MainWindow;
					deviceStatusWindow.ShowDialog();
				}
				finally {
					_coordinator.End(operation);
				}
				return true;
			});


			this.ScreenRotateCommand = new RelayCommand(o => {
#if DEBUG
				var mainWindow = Application.Current.MainWindow as MainWindow;
				mainWindow!.ToggleDisplayOrientation();
#endif
			});

			// Wire up together the door watcher and the locker status updater 
			_doorWatcher = new DoorWatcher(lockControllerClient,
											_configuration.Locker.LockerBoxCount,
											TimeSpan.FromMilliseconds(_configuration.Locker.DoorWatcherFrequency),
											TimeSpan.FromMilliseconds(_configuration.Locker.GetMultipleDoorStatusCooldown),
											TimeSpan.FromMilliseconds(_configuration.Locker.ReadWriteTimeout),
											statusUpdateHandler: null,
											startImmediately: false,
											logger, CancellationToken.None);

			_lockerStatusUpdater = new LockerStatusUpdater(_configuration.LockerStatusUpdater,
																_serviceClient,
																_configuration.Locker.LockerBoxCount,
																logger) {
				LockerStatusChanged = this.OnLockerStatusChanged
			};

			_doorWatcher.StatusUpdateHandler = _lockerStatusUpdater.UpdateStatuses;

			_paymentConfirmationSender = new PaymentConfirmationSender(_configuration.PaymentConfirmationSender, _serviceClient, _logger!);
			_serverConfirmationSender = new ServerConfirmationSender(_configuration.ServerConfirmationSender, _serviceClient, _logger!);

			this.IsBarcodeScannerOutOfOrder = !_configuration.General.IsBarcodeScannerAvailable;    // initial state, might change later
			this.IsBarcodeScannerOutOfOrder = false; // samo da bih isključio UI poruku da Barkod skener nije dostupan
			_barcodeScannerClient.OnBarcodeSent =  barcode => {
				UIThreadSwitcher.RunOnUiThread(() => this.IsBarcodeScannerOutOfOrder = false);
				return ValueTask.CompletedTask;
			};

			this.IsLockerOperational = null;    // Always force initial UI update

			_audioPlayer = new AudioPlayer(AppDomain.CurrentDomain.BaseDirectory, logger);

			if (_configuration.General.PlayButtonPressedSound) {
				Application.Current.MainWindow.AddHandler(Button.ClickEvent, new RoutedEventHandler((s, e) => _audioPlayer.Play(Path.Combine("Resources", "button-press.mp3"))));
			}

			// MORA pre _doorWatcher.Resume(): doorWatcher može pozvati OnLockerStatusChanged
			// koji okida Reset trigger; OnEntry handler za Start mora biti registrovan pre toga.
			WireWorkflowMachine();

			// Now's OK to start the door watcher — radi dok kiosk nema vlasnika (i u Idle: daje početni status i prvi Start)
			_coordinator.OwnerChanged += SyncDoorWatcherWithOwner;
			SyncDoorWatcherWithOwner();

			_lockerStatusMessage = this["General.LockerInitialization"];
			var _ = DisplayMessage(_lockerStatusMessage, MessageType.Information, displayTimeInSeconds: 0); // Infinite
		}

		/// <summary>
		/// Helper za okidanje Reset trigger-a — zove ga sa svih mesta gde je ranije bio
		/// <c>await GoToStartPage()</c> (timer, error path, success path, locker status).
		/// </summary>
		private ValueTask FireResetAsync() => new(_workflowMachine.FireAsync(WorkflowTrigger.Reset));

		/// <summary>Da li je sesija koju je OnEntry uhvatio i dalje aktivna (vidi <see cref="IWorkflowHost.CurrentSession"/>).</summary>
		private bool IsSessionActive(KioskLease? session) => session != null && _coordinator.IsActive(session);

#if DEBUG
		/// <summary>
		/// DEBUG-only: dump trenutni workflow konfiguraciju kao Graphviz DOT graf,
		/// generiše samostalan HTML viewer (sa uzidanim viz.js iz CDN-a) i otvara ga
		/// u default browser-u. Pozvati preko F12 keyboard shortcut-a (vidi MainWindow.xaml.cs).
		///
		/// Output u <c>{appdir}\Workflow\workflow.{dot,html}</c> (pored .exe-a). Folder se kreira
		/// po potrebi. Možeš više puta da klikneš workflow.html bez potrebe da se opet pokreće app.
		///
		/// DOT izlaz iz Stateless-a se post-procesira preko <see cref="BeautifyDotGraph"/>:
		/// horizontalan LR layout, dark mode, kategorisani clusteri sa obojenim okvirima,
		/// debljim forward edges + tanjim dashed back-to-Start edges.
		/// </summary>
		public void DumpWorkflowGraph()
		{
			try {
				var rawDot = _workflowMachine.ToDotGraph();
				var dot = BeautifyDotGraph(rawDot);

				var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..\..\doc\Workflow");
				Directory.CreateDirectory(dir);
				var dotPath = Path.Combine(dir, "workflow.dot");
				var htmlPath = Path.Combine(dir, "workflow.html");

				File.WriteAllText(dotPath, dot);

				var jsLiteral = System.Text.Json.JsonSerializer.Serialize(dot);
				var html = """
					<!DOCTYPE html>
					<html lang="en">
					<head>
					<meta charset="utf-8">
					<title>TopPos workflow graph</title>
					<style>
						html, body { margin: 0; padding: 0; font-family: -apple-system, Segoe UI, sans-serif; background: #1e1e1e; color: #d4d4d4; height: 100%; }
						body { display: flex; flex-direction: column; }
						header { padding: 8px 16px; background: #252526; border-bottom: 1px solid #3c3c3c; display: flex; align-items: center; gap: 16px; flex-shrink: 0; }
						h1 { font-size: 13px; color: #d4d4d4; margin: 0; font-weight: 500; }
						.legend { display: flex; gap: 12px; font-size: 11px; color: #b0b0b0; }
						.legend-item { display: flex; align-items: center; gap: 4px; }
						.legend-swatch { width: 12px; height: 12px; border-radius: 2px; border: 1px solid #555; }
						.hint { margin-left: auto; font-size: 11px; color: #888; }
						.graph { flex: 1; background: #1e1e1e; overflow: hidden; }
						.graph svg { width: 100%; height: 100%; }
						.err { color: #f48771; padding: 12px; background: #2d2424; }

						.node, .edge { transition: opacity 0.15s; }
						.node { cursor: pointer; }
						.dimmed { opacity: 0.12; }
						.node.selected polygon, .node.selected ellipse, .node.selected path { stroke: #ffd33d !important; stroke-width: 3px !important; }
						.edge.outgoing path, .edge.outgoing polygon { stroke: #4ec9b0 !important; fill: #4ec9b0 !important; stroke-width: 2px !important; }
						.edge.outgoing text { fill: #4ec9b0 !important; font-weight: 600; }
						.edge.incoming path, .edge.incoming polygon { stroke: #ce9178 !important; fill: #ce9178 !important; stroke-width: 2px !important; }
						.edge.incoming text { fill: #ce9178 !important; font-weight: 600; }
					</style>
					<script src="https://unpkg.com/viz.js@2.1.2/viz.js"></script>
					<script src="https://unpkg.com/viz.js@2.1.2/full.render.js"></script>
					<script src="https://cdn.jsdelivr.net/npm/svg-pan-zoom@3.6.1/dist/svg-pan-zoom.min.js"></script>
					</head>
					<body>
					<header>
						<h1>TopPos workflow — __TIMESTAMP__</h1>
						<div class="legend">
							<span class="legend-item"><span class="legend-swatch" style="background:#fff59d"></span>Start / virtual</span>
							<span class="legend-item"><span class="legend-swatch" style="background:#bbdefb"></span>Recipient</span>
							<span class="legend-item"><span class="legend-swatch" style="background:#c8e6c9"></span>Courier login</span>
							<span class="legend-item"><span class="legend-swatch" style="background:#b2dfdb"></span>Courier pickup</span>
							<span class="legend-item"><span class="legend-swatch" style="background:#ffcc80"></span>Courier deposit</span>
							<span class="legend-item"><span class="legend-swatch" style="background:#fff9c4"></span>Sender deposit</span>
						</div>
						<span class="hint">drag = pan · scroll = zoom · click node = highlight in/out</span>
					</header>
					<div id="graph" class="graph">Renderovanje...</div>
					<script>
						const dot = __DOT__;
						new Viz().renderSVGElement(dot)
							.then(el => {
								const g = document.getElementById('graph');
								g.innerHTML = '';
								g.appendChild(el);
								el.setAttribute('width', '100%');
								el.setAttribute('height', '100%');
								svgPanZoom(el, { controlIconsEnabled: true, fit: true, center: true, minZoom: 0.2, maxZoom: 10 });
								wireInteractivity(el);
							})
							.catch(err => { document.getElementById('graph').innerHTML = '<pre class="err">' + err + '</pre>'; });

						function wireInteractivity(svg) {
							let activeNode = null;
							svg.querySelectorAll('g.node').forEach(node => {
								node.addEventListener('click', e => {
									e.stopPropagation();
									const name = (node.querySelector('title')?.textContent || '').trim();
									if (activeNode === name) { clearHighlight(svg); activeNode = null; }
									else { highlightNode(svg, name); activeNode = name; }
								});
							});
							svg.addEventListener('click', () => { if (activeNode) { clearHighlight(svg); activeNode = null; } });
						}
						function clearHighlight(svg) {
							svg.querySelectorAll('.dimmed, .selected, .incoming, .outgoing').forEach(e => {
								e.classList.remove('dimmed', 'selected', 'incoming', 'outgoing');
							});
						}
						function highlightNode(svg, name) {
							clearHighlight(svg);
							const connectedNodes = new Set([name]);
							svg.querySelectorAll('g.edge').forEach(edge => {
								const title = (edge.querySelector('title')?.textContent || '').trim();
								const arrow = title.split('->').map(s => s.trim());
								if (arrow.length !== 2) return;
								const [src, dst] = arrow;
								if (src === name) { edge.classList.add('outgoing'); connectedNodes.add(dst); }
								else if (dst === name) { edge.classList.add('incoming'); connectedNodes.add(src); }
								else { edge.classList.add('dimmed'); }
							});
							svg.querySelectorAll('g.node').forEach(n => {
								const nname = (n.querySelector('title')?.textContent || '').trim();
								if (nname === name) n.classList.add('selected');
								else if (!connectedNodes.has(nname)) n.classList.add('dimmed');
							});
						}
					</script>
					</body>
					</html>
					""".Replace("__DOT__", jsLiteral)
					   .Replace("__TIMESTAMP__", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
				File.WriteAllText(htmlPath, html);

				System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
					FileName = htmlPath,
					UseShellExecute = true
				});

				_logger?.LogInformation("Workflow graph dumped: dot={DotPath} html={HtmlPath}", dotPath, htmlPath);
				var _ = DisplayMessage($"Workflow graf otvoren u browser-u.\n{htmlPath}", MessageType.Information, displayTimeInSeconds: 4);
			}
			catch (Exception ex) {
				_logger?.LogError(ex, "{Method}: dump-ovanje workflow grafa nije uspelo", nameof(DumpWorkflowGraph));
			}
		}

		/// <summary>
		/// Post-procesira DOT izlaz iz Stateless-a za bolju čitljivost: horizontalan LR layout
		/// (edges horizontalni, labels iznad linija), dark mode, zaobljeni node-ovi, boje stanja
		/// po kategoriji + clusteri sa obojenim okvirima, deblji forward edges, tanji dashed
		/// back-to-Start edges.
		/// </summary>
		private static string BeautifyDotGraph(string dot)
		{
			var stateInfo = new Dictionary<string, (string Color, string Category)>
			{
				{ "Idle", ("#e0e0e0", "Initial") },
				{ "Start", ("#fff59d", "Initial") },
				{ "RecipientCodeInput", ("#bbdefb", "Recipient") },
				{ "RecipientPhoneInput", ("#bbdefb", "Recipient") },
				{ "RecipientValidating", ("#fff59d", "Recipient") },
				{ "RecipientPayment", ("#bbdefb", "Recipient") },
				{ "RecipientFiscalReceipt", ("#bbdefb", "Recipient") },
				{ "RecipientPackagePickup", ("#bbdefb", "Recipient") },
				{ "CourierManualLogin", ("#c8e6c9", "CourierLogin") },
				{ "CourierManualAuthenticating", ("#fff59d", "CourierLogin") },
				{ "CourierAuthenticating", ("#fff59d", "CourierLogin") },
				{ "CourierLockerBoxAvailability", ("#c8e6c9", "CourierLogin") },
				{ "CourierPickupConfirmation", ("#c8e6c9", "CourierLogin") },
				{ "CourierFetchingExpiredPackages", ("#fff59d", "CourierLogin") },
				{ "CourierPickupPrompt", ("#b2dfdb", "CourierPickup") },
				{ "CourierPackagePickup", ("#b2dfdb", "CourierPickup") },
				{ "CourierPackagePickupBarcodeScan", ("#b2dfdb", "CourierPickup") },
				{ "CourierDepositPrompt", ("#ffcc80", "CourierDeposit") },
				{ "CourierPackageDepositBarcodeScan", ("#ffcc80", "CourierDeposit") },
				{ "CourierDepositBarcodeValidating", ("#fff59d", "CourierDeposit") },
				{ "CourierPackageDeposit", ("#ffcc80", "CourierDeposit") },
				{ "CourierDepositConfirmation", ("#ffcc80", "CourierDeposit") },
				{ "CourierDepositConfirmationFailed", ("#ffcc80", "CourierDeposit") },
				{ "SenderDepositBarcodeScan", ("#fff9c4", "Sender") },
				{ "SenderDepositBarcodeValidating", ("#fff59d", "Sender") },
				{ "SenderPackageDeposit", ("#fff9c4", "Sender") },
				{ "SenderDepositConfirmationFailed", ("#fff9c4", "Sender") },
				{ "ServiceDoorOpening", ("#fff59d", "Service") }
			};

			// 1. LR layout + dark mode + tighter spacing + default node/edge stil. penwidth=1.6 čini
			//    forward flow deblji; back-to-Start edges su override-ovani na tanji + dashed.
			dot = dot.Replace("rankdir=\"LR\"",
				"rankdir=\"LR\"\nbgcolor=\"#1e1e1e\"\nnewrank=true\nnodesep=\"0.35\"\nranksep=\"0.7\"\nsplines=\"spline\"");
			dot = dot.Replace("node [shape=Mrecord]",
				"node [shape=box, style=\"filled,rounded\", fillcolor=\"#3c3c3c\", fontcolor=\"#1a1a1a\", fontname=\"Segoe UI\", fontsize=11, margin=\"0.18,0.08\", color=\"#666\"];\n" +
				"edge [fontname=\"Segoe UI\", fontsize=10, color=\"#cccccc\", fontcolor=\"#dddddd\", penwidth=1.6]");

			// 2. Sve back-to-Start edges (Reset + RecipientPaymentDeclined + ...) → isti stil:
			//    tanji dashed + mute boja. Forward flow ostaje deblji i naglašen.
			dot = System.Text.RegularExpressions.Regex.Replace(dot,
				@"""([^""]+)"" -> ""Start"" \[style=""solid"", label=""([^""]+)""\];",
				@"""$1"" -> ""Start"" [style=""dashed"", color=""#666666"", fontcolor=""#888888"", penwidth=1, label=""$2""];");

			// 3. Boje stanja.
			foreach (var (state, info) in stateInfo) {
				dot = System.Text.RegularExpressions.Regex.Replace(dot,
					$@"""({System.Text.RegularExpressions.Regex.Escape(state)})"" \[label=""([^""]+)""\];",
					$@"""$1"" [label=""$2"", fillcolor=""{info.Color}""];");
			}

			// 4. Clusteri po kategoriji sa border bojom odgovarajućom kategoriji.
			var clusterStyle = new Dictionary<string, (string Label, string Border, string LabelColor)>
			{
				{ "Recipient",     ("Recipient pickup", "#64b5f6", "#90caf9") },
				{ "CourierLogin",  ("Courier login",    "#81c784", "#a5d6a7") },
				{ "CourierPickup", ("Courier pickup",   "#4db6ac", "#80cbc4") },
				{ "CourierDeposit",("Courier deposit",  "#ffa726", "#ffb74d") },
				{ "Sender",        ("Sender deposit",   "#fbc02d", "#fff176") },
				{ "Service",       ("Service",          "#ba68c8", "#ce93d8") }
			};
			var clustersBlock = new System.Text.StringBuilder();
			foreach (var (cat, style) in clusterStyle) {
				var members = stateInfo.Where(kv => kv.Value.Category == cat).Select(kv => kv.Key).ToList();
				if (members.Count == 0) continue;
				clustersBlock.AppendLine($"subgraph cluster_{cat} {{");
				clustersBlock.AppendLine($"  label=\"{style.Label}\"; labelloc=\"t\"; fontname=\"Segoe UI\"; fontsize=11; fontcolor=\"{style.LabelColor}\";");
				clustersBlock.AppendLine($"  style=\"dashed\"; color=\"{style.Border}\"; penwidth=1.5; margin=12;");
				foreach (var m in members) clustersBlock.AppendLine($"  \"{m}\";");
				clustersBlock.AppendLine("}");
			}
			var lastBrace = dot.LastIndexOf('}');
			if (lastBrace > 0) {
				dot = dot.Insert(lastBrace, clustersBlock.ToString());
			}

			return dot;
		}
#endif

		/// <summary>
		/// Registruje OnEntry handler-e za stanja koja su u Fazi 2 prebačena na state machine
		/// (Start + recipient flow). Courier flow se u Fazi 2 ne dira — i dalje radi direktnim
		/// pozivima <c>GoTo*</c> metoda. Phase 3/4 će dodati courier OnEntry mappinge.
		/// </summary>
		private void WireWorkflowMachine()
		{
			// Home dugme je aktivno svuda OSIM u stanjima nepovratne procedure (WorkflowMachine.IsHomeDisabled) — od plaćanja,
			// odnosno dodele vrata, do potvrde serveru. Tako Home ne može da prekine tok (otkup bi ostavio korisnika bez paketa
			// iako je platio; ostavljen paket bez potvrde server bi dodelio sledećem). OnTransitioned se okida pre OnEntry, pa je
			// dugme ugašeno pre nego što plaćanje krene da await-uje POS.
			_workflowMachine.OnTransitioned(destination => IsHomeButtonEnabled = !WorkflowMachine.IsHomeDisabled(destination));
			_workflowMachine.OnTransitioned(destination => IsPageChromeVisible = !WorkflowMachine.IsFullScreen(destination));

			// Status za spoljni servis (status.lock i pipe ka Agentu): Start/Idle = slobodno, svako drugo stanje = zauzeto
			// (WorkflowStatusRules). Početno slobodno stanje (Idle) je već upisao App.OnStartup (StatusSemaphore.InitializeSession),
			// pre uređaja i PRE provere da li je kontroler povezan (OnLockerStatusChanged, koji se okida iz DoorWatcher.Resume()
			// posle ove metode). Tako status.lock postoji i kad je paketomat "van funkcije" (kontroler nije povezan): spoljni
			// servis vidi Busy=false i zna da je bezbedno za update/restart.
			// Vlasništvo pre statusa stanja: na ulasku u Start sesija prelazi u završavanje, pa kiosk ostaje zauzet do kraja čišćenja.
			_workflowMachine.OnTransitioned((source, destination) => _coordinator.OnWorkflowTransition(source, destination));
			_workflowMachine.OnTransitioned(destination => _statusSource.SetWorkflowState(destination));

			_workflowMachine.OnEntry(WorkflowState.Start, GoToStartPage);
			_workflowMachine.OnEntry(WorkflowState.RecipientCodeInput, _recipientActivator.GoToRecipientCodeInputPage);
			// Kod putuje uz trigger → u kontekst PRE ekrana za telefon, odnosno PRE provere na serveru (redosled registracije =
			// redosled izvršavanja); handler ekrana stare sesije tako ne može da upiše kod u kontekst sledeće.
			_workflowMachine.OnEntryFrom(WorkflowState.RecipientPhoneInput, _workflowMachine.RecipientCodeAccepted, _recipientActivator.AcceptUnlockCode);
			_workflowMachine.OnEntry(WorkflowState.RecipientPhoneInput, _recipientActivator.GoToRecipientPhoneInputPage);
			_workflowMachine.OnEntryFrom(WorkflowState.RecipientValidating, _workflowMachine.RecipientCodeAcceptedWithDefaultPhone,
				_recipientActivator.AcceptUnlockCodeWithDefaultPhone);
			// Telefon putuje uz trigger → u kontekst PRE provere na serveru (redosled registracije = redosled izvršavanja).
			_workflowMachine.OnEntryFrom(WorkflowState.RecipientValidating, _workflowMachine.RecipientPhoneEntered,
				phoneNumber => {
					_workflowContext.RecipientPhoneNumber = phoneNumber;
					return ValueTask.CompletedTask;
				});
			_workflowMachine.OnEntry(WorkflowState.RecipientValidating, _recipientActivator.ValidateRecipientAndRoute);
			_workflowMachine.OnEntry(WorkflowState.RecipientPayment, _recipientActivator.GoToRecipientPaymentPage);
			_workflowMachine.OnEntry(WorkflowState.RecipientFiscalReceipt, _recipientActivator.GoToRecipientFiscalReceiptPage);
			_workflowMachine.OnEntry(WorkflowState.RecipientPackagePickup, _recipientActivator.GoToRecipientPackagePickupPage);
			// Preuzimanje na zahtev Agenta: proveru (RecipientRemoteValidating) i ishode registruje WorkflowRecipientPickupHost.
			_workflowMachine.OnEntry(WorkflowState.RecipientRemoteConfirmation, _recipientActivator.GoToRecipientRemoteConfirmationPage);

			// Faza 3 pod-korak 1 — courier login routing kroz machine.
			// Barkod za manual login putuje uz parametrizovan trigger.
			// Barkod u kontekst PRE prikaza ekrana (redosled registracije = redosled izvršavanja); ekran ga čita iz konteksta
			// i pri povratku posle neuspele provere koda.
			_workflowMachine.OnEntryFrom(WorkflowState.CourierManualLogin, _workflowMachine.CourierBarcodeScannedRequiringManualLogin,
				barcode => {
					_workflowContext.CourierLoginBarcode = barcode;
					return ValueTask.CompletedTask;
				});
			_workflowMachine.OnEntry(WorkflowState.CourierManualLogin, _courierLoginActivator.GoToCourierManualLoginPage);
			_workflowMachine.OnEntryFrom(WorkflowState.CourierManualAuthenticating, _workflowMachine.CourierManualCodeEntered, _courierLoginActivator.AuthenticateManualCourierAndRoute);
			_workflowMachine.OnEntryFrom(WorkflowState.CourierAuthenticating, _workflowMachine.CourierBarcodeScanned, _courierLoginActivator.AuthenticateCourierAndRoute);
			_workflowMachine.OnEntry(WorkflowState.CourierLockerBoxAvailability, _courierLoginActivator.GoToLockerBoxAvailabilityPage);
			_workflowMachine.OnEntry(WorkflowState.CourierPickupConfirmation, _courierLoginActivator.GoToCourierPickupConfirmationPage);
			_workflowMachine.OnEntry(WorkflowState.CourierFetchingExpiredPackages, _courierLoginActivator.FetchExpiredPackagesAndRoute);
			// Pod-korak 2 — courier pickup kroz machine.
			_workflowMachine.OnEntry(WorkflowState.CourierPickupPrompt, _courierPickupActivator.GoToCourierPickupPromptPage);
			_workflowMachine.OnEntry(WorkflowState.CourierPackagePickup, _courierPickupActivator.GoToCourierPackagePickupPage);
			_workflowMachine.OnEntry(WorkflowState.CourierPackagePickupBarcodeScan, _courierPickupActivator.GoToCourierPackagePickupBarcodeScanPage);

			// Faza 4 — courier deposit interna kroz machine.
			_workflowMachine.OnEntry(WorkflowState.CourierDepositPrompt, _courierDepositActivator.GoToCourierDepositPromptPage);
			_workflowMachine.OnEntry(WorkflowState.CourierPackageDepositBarcodeScan, _courierDepositActivator.GoToCourierPackageDepositBarcodeScanPage);
			// Skenirani barkod putuje uz parametrizovan trigger; dodela vrata na serveru je u virtuelnom stanju.
			_workflowMachine.OnEntryFrom(WorkflowState.CourierDepositBarcodeValidating, _workflowMachine.DepositBarcodeScanned, _courierDepositActivator.RequestCourierDepositDoorAndRoute);
			// barcode+doorNumber putuju uz parametrizovan trigger → upiši ih u kontekst PRE render OnEntry-ja.
			_workflowMachine.OnEntryFrom(WorkflowState.CourierPackageDeposit, _workflowMachine.DepositBarcodeAccepted,
				(barcode, doorNumber) => {
					_workflowContext.DepositBarcode = barcode;
					_workflowContext.DepositDoorNumber = doorNumber;
					_workflowContext.Logger = _workflowContext.Logger.ForContext("PackageId", barcode);
					return ValueTask.CompletedTask;
				});
			_workflowMachine.OnEntry(WorkflowState.CourierPackageDeposit, _courierDepositActivator.GoToCourierPackageDepositPage);
			_workflowMachine.OnEntry(WorkflowState.CourierDepositConfirmation, _courierDepositActivator.GoToCourierDepositConfirmationPage);
			// ishod neuspele potvrde putuje uz triger (ne kroz kontekst)
			_workflowMachine.OnEntryFrom(WorkflowState.CourierDepositConfirmationFailed, _workflowMachine.DepositConfirmationFailed,
										_depositConfirmationFailedActivator.GoToCourierDepositConfirmationFailedPage);

			// Sender deposit (pošiljalac) — paralelan deposit tok, ulaz sa Start dugmeta.
			_workflowMachine.OnEntry(WorkflowState.SenderDepositBarcodeScan, _senderDepositActivator.GoToSenderDepositBarcodeScanPage);
			_workflowMachine.OnEntryFrom(WorkflowState.SenderDepositBarcodeValidating, _workflowMachine.SenderBarcodeScanned, _senderDepositActivator.RequestSenderDepositDoorAndRoute);
			// barcode+doorNumber putuju uz parametrizovan trigger → upiši ih u kontekst PRE render OnEntry-ja.
			_workflowMachine.OnEntryFrom(WorkflowState.SenderPackageDeposit, _workflowMachine.SenderBarcodeAccepted,
				(barcode, doorNumber) => {
					_workflowContext.DepositBarcode = barcode;
					_workflowContext.DepositDoorNumber = doorNumber;
					_workflowContext.Logger = _workflowContext.Logger.ForContext("PackageId", barcode);
					return ValueTask.CompletedTask;
				});
			_workflowMachine.OnEntry(WorkflowState.SenderPackageDeposit, _senderDepositActivator.GoToSenderPackageDepositPage);
			_workflowMachine.OnEntryFrom(WorkflowState.SenderDepositConfirmationFailed, _workflowMachine.SenderConfirmationFailed,
										_depositConfirmationFailedActivator.GoToSenderDepositConfirmationFailedPage);

			// Servis (tehničar) — servisni kod sa Start ekrana.
			_workflowMachine.OnEntry(WorkflowState.ServiceDoorOpening, OpenServiceDoor);
		}

		private async ValueTask OnLockerStatusChanged(bool isLockerOperational)
		{
			try {
				this.IsLockerOperational = isLockerOperational; // updates UI
				_statusSource.SetOperational(isLockerOperational); // spoljni nadzor: operativnost u status.lock i statusu za Agenta
				_logger?.LogWarning($"{nameof(OnLockerStatusChanged)}: status paketomata je promenjen. Paketomat je u funkciji = [{isLockerOperational}]");
				if (isLockerOperational != true) {
					_lockerStatusMessage = this["General.LockerOutOfOrder"];
					await DisplayMessage(_lockerStatusMessage, MessageType.Error, displayTimeInSeconds: 0); // Infinite
				}
				else {
					RemoveLockerStatusMessage();
					// Reset samo za prvi Start (iz Idle) i osvežavanje slobodnog Start-a. Sesija u toku se ne prekida: završava se
					// svojim tokom ili rokom (ranije su vrata otvorena u toku ostajala bez praćenja).
					await _workflowMachine.FireGuardedAsync(() => _workflowMachine.State == WorkflowState.Idle
																|| (_workflowMachine.State == WorkflowState.Start && _coordinator.Owner == null),
															WorkflowTrigger.Reset).ConfigureAwait(false);
				}
			}
			catch (Exception ex) {
				_logger?.LogError($"{nameof(OnLockerStatusChanged)}: exception: {ex.Message}");
			}
		}

		private IReadOnlyList<StepViewModel> _steps;
		public IReadOnlyList<StepViewModel> Steps {
			get => _steps;
			set {
				if (_steps != value) {
					SetProperty(ref _steps, value);
				}
			}
		}

		private void SetCurrentStep(int stepNumber)
		{
			for (int i = 0; i < _steps.Count; i++) {
				_steps[i].IsSelected = i == stepNumber;
			}
		}

		private StartViewModel SetUpStartPageViewModel()
			=> new(_barcodeScannerClient,
				_configuration.General.ShowLockerIdQrCode ? _configuration.Locker.LockerId : string.Empty,
				_languageManager, _logger);


		private CourierBarcodeScanViewModel SetUpCourierBarcodeScanViewModel()
			=> new(_languageManager, _logger);

		private bool? _isLockerOperational;
		public bool? IsLockerOperational {
			get => _isLockerOperational;
			set {
				SetProperty(ref _isLockerOperational, value);
			}
		}

		private bool _isBarcodeScannerOutOfOrder;
		public bool IsBarcodeScannerOutOfOrder {
			get => _isBarcodeScannerOutOfOrder;
			set {
				SetProperty(ref _isBarcodeScannerOutOfOrder, value);
			}
		}

		private bool _isPosTerminalOutOfOrder;
		public bool IsPOSTerminalOutOfOrder {
			get => _configuration.PosTerminal.TerminalInstalled && _isPosTerminalOutOfOrder;
			set {
				SetProperty(ref _isPosTerminalOutOfOrder, value);
			}
		}

		public string TechnicalSupportContact =>
			string.Format(this["MainArea.TechnicalSupportContact"],
						  _configuration.Locker.LockerId);

		private bool _showProgressBar;
		public bool ShowProgressBar {
			get => _showProgressBar;
			set {
				SetProperty(ref _showProgressBar, value);
			}
		}

		public ICommand ShowLanguageSelectorCommand { get; }
		public LanguageSelectorViewModel LanguageSelectorViewModel { get; }

		private bool _showLanguageSelector;
		public bool ShowLanguageSelector {
			get => _showLanguageSelector;
			set {
				SetProperty(ref _showLanguageSelector, value);
			}
		}

		private DrawingGroup? _currentLanguageFlag;
		public DrawingGroup? CurrentLanguageFlag {
			get => _currentLanguageFlag;
			set => SetProperty(ref _currentLanguageFlag, value);
		}

		private string MakeCurrentLanguagePath(string filename)
			=> Path.Combine("Languages", _languageManager.CurrentLanguage!.LanguageId, filename);

		public ICommand ScreenRotateCommand { get; }

		public bool AllowScreenRotation {
			get {
#if DEBUG
				return true;
#else
				return false;
#endif
			}
		}

		public bool IsDebugMode {
			get {
#if DEBUG
				return true;
#else
				return false;
#endif
			}
		}

		public string CurrentPageName => _currentPage?.GetType().Name ?? "<null>";

		public IAsyncCommand GoToStartPageCommand { get; }

		public IAsyncCommand DisplayDeviceStatusCommand { get; }

		private bool _isHomeButtonEnabled = true;
		/// <summary>
		/// Da li je Home dugme (gornji desni ugao) aktivno. Gasi se u "committed" recipient stanjima
		/// (RecipientPayment, RecipientFiscalReceipt, RecipientPackagePickup); pali se nazad na Start.
		/// Bind-uje se na Button.IsEnabled u MainWindow layout-ima — onesposobljeno WPF dugme ne prima
		/// mouse evente, pa ni ButtonPressBehavior (short/long press) ne okida. Postavlja se iz
		/// <see cref="WireWorkflowMachine"/> preko OnTransitioned.
		/// </summary>
		public bool IsHomeButtonEnabled {
			get => _isHomeButtonEnabled;
			private set => SetProperty(ref _isHomeButtonEnabled, value);
		}

		private bool _isPageChromeVisible = true;
		/// <summary>
		/// Zaglavlje, koraci i podnožje oko tekuće stranice. Ugašeni su u stanjima <see cref="WorkflowMachine.IsFullScreen"/>;
		/// postavlja se iz <see cref="WireWorkflowMachine"/> preko OnTransitioned.
		/// </summary>
		public bool IsPageChromeVisible {
			get => _isPageChromeVisible;
			private set => SetProperty(ref _isPageChromeVisible, value);
		}

		private UserControl? _currentPage;

		public UserControl? CurrentPage {
			get { return _currentPage; }
			set {
				if (_currentPage != value) {
					OnDeactivatingPage(_currentPage);
					SetProperty(ref _currentPage, value);
					OnPropertyChanged(nameof(CurrentPageName));
					OnPageActivated(_currentPage);
				}
			}
		}

		private bool _showMessage;
		public bool ShowMessage {
			get => _showMessage;
			set {
				SetProperty(ref _showMessage, value);
			}
		}

		private string? _message;
		public string? Message {
			get => _message;
			set {
				SetProperty(ref _message, value);
			}
		}

		private int _messageDisplayTimeInSeconds;
		public int MessageDisplayTimeInSeconds {
			get => _messageDisplayTimeInSeconds;
			set {
				SetProperty(ref _messageDisplayTimeInSeconds, value);
			}
		}

		private DrawingGroup? _messageImage;
		public DrawingGroup? MessageImage {
			get => _messageImage;
			set {
				SetProperty(ref _messageImage, value);
			}
		}

		private async ValueTask DisplayMessage(string message, MessageType messageType, int? displayTimeInSeconds = null)
		{
			UIThreadSwitcher.RunOnUiThread(() => {
				this.Message = message;
				this.MessageImage = (DrawingGroup?) Application.Current.TryFindResource(
																			messageType switch {
																				MessageType.Success => "Success",
																				MessageType.Error => "Error",
																				_ => "Information"
																			});
				this.ShowMessage = true;
			});
			displayTimeInSeconds ??= _configuration.General.UserMessageDisplayTimeInSeconds;
			if (displayTimeInSeconds > 0) {
				await Task.Delay(TimeSpan.FromSeconds(displayTimeInSeconds.Value))
							.ContinueWith(t => RemoveMessage(), TaskContinuationOptions.OnlyOnRanToCompletion);
			}
		}

		/// <summary>Skida poruku o stanju paketomata, ako je ona i dalje prikazana (ne poruku toka koja ju je zamenila).</summary>
		private void RemoveLockerStatusMessage()
		{
			var statusMessage = _lockerStatusMessage;
			UIThreadSwitcher.RunOnUiThread(() => {
				if (statusMessage != null && this.Message == statusMessage) {
					this.Message = null;
					this.MessageImage = null;
					this.ShowMessage = false;
				}
			});
		}

		private void RemoveMessage()
		{
			UIThreadSwitcher.RunOnUiThread(() => {
				this.Message = null;
				this.MessageImage = null;
				this.ShowMessage = false;
			});
		}

		private void OnDeactivatingPage(UserControl? deactivatePage)
		{
			var pageName = deactivatePage?.GetType().Name ?? "<null>";
			_logger?.LogInformation($"{nameof(MainWindowViewModel)}: deaktiviranje tekuće strane {pageName}.");
			_operationCountdownTimer.Stop();
			if (deactivatePage == _startPage) {
				_ctsCheckDeviceStatus?.Cancel();
			}
			_audioPlayer.Reset();
		}

		/// <summary>
		/// Operacija Agenta (komanda preko kanala, npr. ShutdownApplication): atomski prijem kroz koordinatora, kiosk ostaje
		/// na Start-u. Vraća null ako je kiosk zauzet, nije spreman ili se gasi. Bezbedno sa bilo koje niti.
		/// </summary>
		public KioskLease? TryBeginAgentOperation(string source)
			=> _coordinator.TryBeginOperation(_workflowMachine, WorkflowStatusRules.AgentRole, source);

		/// <summary>
		/// Veza servisne sesije Agenta sa workflow-om i DoorWatcher-om. Registruje OnEntry za <see cref="WorkflowState.RemoteService"/>,
		/// pa se pravi jednom, pri startu.
		/// </summary>
		public IRemoteServiceHost CreateRemoteServiceHost()
			=> new WorkflowRemoteServiceHost(_workflowMachine, _coordinator, _doorWatcher.GetUnavailableModules, PauseDoorWatcherAsync, _logger,
											 _remoteServiceActivator.GoToRemoteServicePage);

		/// <summary>
		/// Veza preuzimanja na zahtev Agenta sa workflow-om i serverom. Registruje OnEntry za
		/// <see cref="WorkflowState.RecipientRemoteValidating"/>, pa se pravi jednom, pri startu.
		/// </summary>
		public IRecipientPickupHost CreateRecipientPickupHost()
			=> new WorkflowRecipientPickupHost(_workflowMachine, _coordinator, _workflowContext,
											   (phone, code) => _serviceClient.RecipientRequestPackagePickupDetailed(phone, code, CancellationToken.None).AsTask(),
											   _recipientActivator.ShowRemotePickupNotice, _logger);

		/// <summary>
		/// Izlazak iz aplikacije: provera statusa vrata se zaustavlja pre zatvaranja veze sa kontrolerom, da neuspelo
		/// očitavanje posle zatvaranja ne bi bilo prijavljeno serveru kao nedostupan modul.
		/// </summary>
		public void StopDoorWatcher()
		{
			lock (_doorWatcherSync) {
				_doorWatcherStopped = true;     // oslobađanje vlasnika ga posle ovoga ne pokreće ponovo
				_doorWatcher.Pause();
			}
		}

		/// <summary>
		/// DoorWatcher radi samo dok kiosk nema vlasnika (Idle, slobodan Start): pauziran je od rezervacije do kraja čišćenja,
		/// i dok Start ostaje prikazan (prijava kurira, servisna vrata, prozor statusa). Čita trenutnog vlasnika pod lock-om,
		/// pa spojena ili zakasnela obaveštenja ne ostavljaju pogrešno stanje.
		/// </summary>
		private void SyncDoorWatcherWithOwner()
		{
			lock (_doorWatcherSync) {
				if (_doorWatcherStopped) {
					return;
				}
				if (_coordinator.Owner == null) {
					_doorWatcher.Resume();
				}
				else {
					_doorWatcher.Pause();
				}
			}
		}

		private Task PauseDoorWatcherAsync()
		{
			lock (_doorWatcherSync) {
				return _doorWatcher.PauseAsync();
			}
		}

		private void OnPageActivated(UserControl? activatedPage)
		{
			var pageName = activatedPage?.GetType().Name ?? "<null>";
			bool onUiThread = activatedPage?.Dispatcher.CheckAccess() ?? true;
			_logger?.LogInformation($"{nameof(MainWindowViewModel)}: tekuća strana je sada {pageName}{(onUiThread ? "" : " (sa pozadinske niti)")}.");
			// Dijagnostika: na probi 27.10.2026 ekran je ostao na servisnom meniju iako je log javio StartPage. UI nit, posle
			// rasporeda i iscrtavanja, potvrđuje da je stranica u prikazu; ako nije, to je upozorenje.
			activatedPage?.Dispatcher.InvokeAsync(() => LogPageShown(activatedPage, pageName), System.Windows.Threading.DispatcherPriority.ContextIdle);
		}

		private void LogPageShown(UserControl page, string pageName)
		{
			if (this.CurrentPage != page) {
				return;     // u međuvremenu je prikazana druga stranica
			}
			if (page.IsLoaded && page.IsVisible) {
				_logger?.LogDebug($"{nameof(MainWindowViewModel)}: strana {pageName} je u prikazu.");
			}
			else {
				_logger?.LogWarning($"{nameof(MainWindowViewModel)}: strana {pageName} nije u prikazu (IsLoaded={page.IsLoaded}, IsVisible={page.IsVisible}).");
			}
		}

		private ValueTask GoToStartPage()
		{
			Task barcodeTrackerStopped = Task.CompletedTask;
			Task doorTrackerStopped = Task.CompletedTask;
			try {
				if (this.CurrentPage == _startPage) {
					// Start je ostao prikazan (virtuelna stanja, ili izuzetak u toku koji je krenuo sa Start-a pre promene stranice):
					// prikaz se ne menja, ali takav tok je možda već deaktivirao StartViewModel (barkod handler) i pokrenuo tracker.
					// Bez promene stranice OnDeactivatingPage ne zaustavlja ni opšti tajmer.
					_operationCountdownTimer.Stop();
					barcodeTrackerStopped = _barcodeScanTracker.StopAsync();
					doorTrackerStopped = _doorStatusTracker.StopAsync();
					_startViewModel.Activate();
					_currentWorkflowViewModel = _startViewModel;
					return ValueTask.CompletedTask;
				}

				_currentWorkflowViewModel?.Deactivate();
				barcodeTrackerStopped = _barcodeScanTracker.StopAsync();
				doorTrackerStopped = _doorStatusTracker.StopAsync();

				_startViewModel.HandleScannedBarcode = TryHandleCourierOrServiceBarcode;
				// Start ekran ostaje prikazan i dok kurir čeka prijavu ili listu isteklih paketa, kao i dok se otvaraju
				// servisna vrata — klik tada odbija koordinator (kiosk ima vlasnika).
				_startViewModel.BeginRecipientPickUpPackage = () => TryBeginSession(WorkflowState.RecipientCodeInput, "dugme Preuzmi paket",
																					(guard, rejected) => _workflowMachine.FireGuardedAsync(guard, WorkflowTrigger.RecipientPickupRequested, rejected));
				_startViewModel.IsPackageDepositionEnabled = _configuration.General.EnablePackageDeposition;
				_startViewModel.BeginSenderDepositPackage = () => TryBeginSession(WorkflowState.SenderDepositBarcodeScan, "dugme Ostavi paket",
																				(guard, rejected) => _workflowMachine.FireGuardedAsync(guard, WorkflowTrigger.SenderDepositRequested, rejected));
				_startViewModel.Activate();
				_currentWorkflowViewModel = _startViewModel;
				this.CurrentPage = _startPage;
				SetCurrentStep(0);

				_ctsCheckDeviceStatus?.Cancel();
				_ctsCheckDeviceStatus = new CancellationTokenSource();
				/*
				Prema dogovoru s Batom, ovo nije potrebno. Ako je uređaj ne bude dostupan kada se bude potreban - prijaviće grešku.
				if (_configuration.PosTerminal.TerminalInstalled == true)
					Task.Run(async () => UpdatePosTerminalStatus(_ctsCheckDeviceStatus.Token), _ctsCheckDeviceStatus.Token);
				*/
				return ValueTask.CompletedTask;
			}
			finally {
				// Sesija koja se završava oslobađa kiosk kad stvarno stanu i trackeri. Ovde se ništa ne čeka: callback trackera je
				// možda baš poslao ovaj Reset. Predaje se i kad gornji kod baci izuzetak — inače bi kiosk ostao zauzet.
				_coordinator.CompleteCleanup(Task.WhenAll(barcodeTrackerStopped, doorTrackerStopped));
			}
		}

		/// <summary>
		/// Početak lokalne sesije sa Start-a: kiosk se rezerviše odmah (atomski, kroz red mašine), a triger se predaje kasnije
		/// (posle odziva dugmeta), sa uslovom koji se proverava kad dođe na red. Null = kiosk je zauzet; ulaz se odbacuje.
		/// </summary>
		private PendingStart? TryBeginSession(WorkflowState destination, string source, Func<Func<bool>, Action, Task> fire)
		{
			var lease = _coordinator.TryBegin(_workflowMachine, KioskLeaseMode.WorkflowSession, WorkflowStatusRules.ResolveRole(destination), source);
			if (lease == null) {
				return null;
			}
			return new PendingStart(
				async () => {
					try {
						await fire(() => _coordinator.IsReserved(lease) && _workflowMachine.State == WorkflowState.Start,
									() => _coordinator.ReleaseIfReserved(lease)).ConfigureAwait(false);
					}
					catch {
						_coordinator.ReleaseIfReserved(lease);  // samo ako triger nije ni počeo; aktivna sesija se završava svojim tokom
						throw;
					}
				},
				() => _coordinator.ReleaseIfReserved(lease));
		}

		private ValueTask<bool> TryHandleCourierOrServiceBarcode(string barcode)
		{
			if (this.IsLockerOperational != true) {
				_logger?.LogWarning($"Barkod {barcode} je ignorisan zato sto je paketomat u stanju greske ili je njegova inicijalizacija u toku.");
				return new ValueTask<bool>(false);
			}
			PendingStart? pending;
			if (barcode == _configuration.Locker.ServiceDoorCode) {
				// Otvaranje je u OnEntry stanja ServiceDoorOpening (kiosk je za to vreme zauzet).
				pending = TryBeginSession(WorkflowState.ServiceDoorOpening, "servisni barkod",
										(guard, rejected) => _workflowMachine.FireGuardedAsync(guard, WorkflowTrigger.ServiceBarcodeScanned, rejected));
			}
			else if (_configuration.General.EnableCourierManualLogin) {
				// In this mode, any scanned barcode is itself ignored and serves only as a trigger to display manual courier login page.
				// Barkod putuje uz parametrizovan trigger u CourierManualLogin (u kontekst); provera koda je u CourierManualAuthenticating.
				pending = TryBeginSession(WorkflowState.CourierManualLogin, "barkod kurira (ručna prijava)",
										(guard, rejected) => _workflowMachine.FireGuardedAsync(guard, _workflowMachine.CourierBarcodeScannedRequiringManualLogin, barcode, rejected));
			}
			else {
				// Scanned barcode is considered to be a courier code which should be validated as-is.
				// Provera na serveru je u OnEntry stanja CourierAuthenticating (kiosk je za to vreme zauzet).
				pending = TryBeginSession(WorkflowState.CourierAuthenticating, "barkod kurira",
										(guard, rejected) => _workflowMachine.FireGuardedAsync(guard, _workflowMachine.CourierBarcodeScanned, barcode, rejected));
			}
			if (pending == null) {
				_logger?.LogWarning($"Barkod {barcode} je ignorisan — kiosk je zauzet.");
				return new ValueTask<bool>(false);
			}
			// Lanac (provera na serveru, otvaranje servisnih vrata) se ne čeka: skener odmah čita dalje, pa barkod skeniran dok
			// je kiosk zauzet biva odbijen, a ne pročitan tek posle i prihvaćen kao nov.
			_ = ObserveSessionStartAsync(pending);
			return new ValueTask<bool>(true);
		}

		private async Task ObserveSessionStartAsync(PendingStart pending)
		{
			try {
				await pending.StartAsync().ConfigureAwait(false);
			}
			catch (Exception ex) {
				_logger?.LogError($"{nameof(MainWindowViewModel)}: {nameof(TryHandleCourierOrServiceBarcode)} exception: {ex.Message}");
			}
		}

		/// <summary>
		/// OnEntry stanja ServiceDoorOpening: otvara servisna vrata; neuspeh prikaže i prijavi serveru.
		/// Uvek završava sa ServiceDoorHandled — bez izlaznog trigera kiosk bi ostao zaglavljen u zauzetom stanju.
		/// </summary>
		private async ValueTask OpenServiceDoor()
		{
			var doorNumber = _configuration.Locker.ServiceDoorNumber;
			try {
				await PauseDoorWatcherAsync().ConfigureAwait(false);    // poll koji je već krenuo se ne meša u otvaranje
				var serviceDoorStatusCode = await _lockControllerClient.OpenDoorSingle(doorNumber,
																						TimeSpan.FromMilliseconds(_configuration.General.DoorStatusResponseTimeout),
																						CancellationToken.None).ConfigureAwait(false);
				if (serviceDoorStatusCode?.IsOpen != true) {
					await DisplayMessage(this["General.ServiceDoorOpeningFailed.Template"].Replace("{doorNumber}", doorNumber.ToString()),
										MessageType.Error, _configuration.General.CourierMessageDisplayTimeInSeconds)
							.ConfigureAwait(false);
					await HandleDoorActionFailure(doorNumber,
												serviceDoorStatusCode,
												_configuration.General.CourierMessageDisplayTimeInSeconds,
												null,
												Serilog.Log.ForContext("ServiceDoor", doorNumber),
												CancellationToken.None)
						.ConfigureAwait(false);
				}
			}
			catch (Exception ex) {
				_logger?.LogError($"{nameof(MainWindowViewModel)}: {nameof(OpenServiceDoor)} exception: {ex.Message}");
			}
			await _workflowMachine.FireAsync(WorkflowTrigger.ServiceDoorHandled).ConfigureAwait(false);
		}

		private async Task UpdatePosTerminalStatus(CancellationToken cancellationToken)
		{
			try {
				var ctsTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
				var ctsLinked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, ctsTimeout.Token);
				bool connected = await _posClient!.CheckPresence(ctsLinked.Token).ConfigureAwait(false);
				if (!connected) {
					_logger?.LogWarning("{Method}: POS terminal nije dostupan", nameof(UpdatePosTerminalStatus));
				}
				UIThreadSwitcher.RunOnUiThread(() => this.IsPOSTerminalOutOfOrder = !connected);
				this.IsPOSTerminalOutOfOrder= false;    // samo da bih isključio UI poruku da POS terminal nije dostupan 
			}
			catch (Exception ex) {
				_logger?.LogError(ex, "{Method}: provera statusa POS terminala nije uspela", nameof(UpdatePosTerminalStatus));
			}
		}

		private async ValueTask HandleDoorActionFailure(int doorNumber,
														DoorStatusCode? unexpectedStatusCode,
														int messageDisplayTime,
														Func<ValueTask>? gotoPage,
														Serilog.ILogger contextLogger,
														CancellationToken cancellationToken)
		{
			try {
				contextLogger.Error("{Method}: problem sa otvaranjem vrata {DoorNumber}. Vrata imaju status {UnexpectedStatus} koji nije u skladu sa ocekivanim.",
									nameof(HandleDoorActionFailure), doorNumber, unexpectedStatusCode);
				await _lockerStatusUpdater.SetFailedStatuses(_doorWatcher.IsLockerOperational(),
															Enumerable.Repeat(new DoorStatus(doorNumber, unexpectedStatusCode), 1), cancellationToken);
			}
			catch (Exception ex) {
				contextLogger.Error($"{nameof(MainWindowViewModel)}: {nameof(HandleDoorActionFailure)} exception: {ex.Message}");
			}
			if (gotoPage != null) {
				await gotoPage().ConfigureAwait(false);
			}
		}

		/// <summary>
		/// Praćenje pripada sesiji koja ga pokreće. Van aktivne sesije se ne pokreće (npr. callback koji ponovo pokreće praćenje
		/// posle čišćenja Start-a — novo praćenje više niko ne bi zaustavio), a callback se ne zove ako se sesija u međuvremenu
		/// završila: bez poruka, prijava serveru i trigera u sledećoj sesiji.
		/// </summary>
		private Task TrackDoorStatus(int doorNumber, DoorStatusCode targetStatusCode, Func<DoorStatusCode?, ValueTask> notifyComplete)
		{
			var session = _coordinator.ActiveSession;
			if (session == null) {
				_logger?.LogWarning($"{nameof(MainWindowViewModel)}: praćenje vrata {doorNumber} nije pokrenuto — nema aktivne sesije.");
				return Task.CompletedTask;
			}

			// Invalidiraj status ovih vrata, prvo ih proveri kad se nastavi monitorisanje.
			_doorWatcher.InvalidateDoorStatus(doorNumber);

			return _doorStatusTracker.Start(doorNumber, targetStatusCode, statusCode => {
				if (!IsSessionActive(session)) {
					_logger?.LogInformation($"{nameof(MainWindowViewModel)}: ishod praćenja vrata {doorNumber} se ne obrađuje — sesija {session} je završena.");
					return ValueTask.CompletedTask;
				}
				return notifyComplete(statusCode);
			});
		}

		protected override void OnLanguageChanged()
		{
			base.OnLanguageChanged();
			OnPropertyChanged(nameof(TechnicalSupportContact));
		}
	}
}
