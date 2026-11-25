using System.Collections.Concurrent;
using Flatsoft.Common;
using Flatsoft.Common.UI;
using Microsoft.Extensions.Logging;
using Stateless;
using TopPos.Kiosk.ViewModels;
using TopPos.Kiosk.Workflow;
using TopPos.LockController;
using TopPos.PenPosService;
using TopPos.Service;

namespace TopPos.Kiosk.Tests
{
	/// <summary>
	/// v4: host za testove aktivatora — pravi servisi prema stub serveru, mašina bez OnEntry handlera (aktivator se zove ručno).
	/// Poruke i prikazane stranice se beleže umesto prikaza, lokalizacija vraća ključ. Vrata: otvaranje vraća
	/// <see cref="OpenDoorResult"/>, praćenje se beleži (<see cref="DoorTracks"/>) i test sam javlja ishod; neuspeh vrata
	/// se beleži i odmah ide na zadatu stranicu. Skener: pravi <see cref="BarcodeScanTracker"/>, sken kroz <see cref="ScanAsync"/>.
	/// Ono što test ne koristi baca <see cref="NotSupportedException"/>.
	/// </summary>
	internal sealed class FakeWorkflowHost : IWorkflowHost, IDisposable
	{
		private readonly List<(string, MessageType)> _messages = [];
		private readonly List<IWorkflowViewModel> _pages = [];

		public FakeWorkflowHost(string baseUrl, string directory, IPosClient? pos = null, bool penPos = false,
								bool enableLockerBoxAvailabilityPage = false, bool confirmPackagePickup = false)
		{
			Configuration = new AppConfiguration {
				General = new GeneralConfiguration {
					MinimumUnlockCodeLength = 4, MaximumUnlockCodeLength = 10, CurrencyDecimalPlaces = 2, CurrencyDisplayName = "RSD",
					OperationTimeout = 60_000
				},
				BarcodeScanner = new BarcodeScannerConfiguration { ComPort = new ComPortConfiguration { PortName = "COM_TEST_NONE" } }
			};
			ServiceClient = new TopPosServiceClient(
				new TopPosServiceConfiguration { BaseUrl = baseUrl, PathPrefix = "api", RequestTimeout = 2000, DisableHeartbeats = true },
				"111", null, CancellationToken.None);
			PaymentConfirmationFile = Path.Combine(directory, "PaymentConfirmations.json");
			PaymentConfirmationSender = new PaymentConfirmationSender(
				new PaymentConfirmationSenderConfiguration { PaymentConfirmationFilename = PaymentConfirmationFile, ProcessingDelayInMilliseconds = 600_000 },
				ServiceClient, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
			BarcodeScannerClient = new BarcodeScannerClient(Configuration.BarcodeScanner, null, CancellationToken.None);
			PosClient = pos;
			LanguageManager = new LanguageManager(null);
			OperationCountdownTimer = new OperationCountdownTimer(TimeSpan.FromMinutes(1), null);
			WorkflowContext = new WorkflowContext();
			WorkflowMachine = Mk.Machine(WorkflowContext, enableLockerBoxAvailabilityPage, confirmPackagePickup, penPos);
		}

		public void Dispose()
		{
			_barcodeScanTracker?.Stop();
			PaymentConfirmationSender.Dispose();
		}

		public string PaymentConfirmationFile { get; }
		public IEnumerable<string> Messages => _messages.Select(m => m.Item1);
		public IEnumerable<(string, MessageType)> MessagesWithType => _messages;
		/// <summary>Stranice redom kojim ih je aktivator prikazao.</summary>
		public IReadOnlyList<IWorkflowViewModel> Pages
		{
			get {
				lock (_pages) {
					return [.. _pages];
				}
			}
		}

		public AppConfiguration Configuration { get; }
		public ILockControllerClient LockControllerClient => _lockController ??= new FakeLockController(this);
		private FakeLockController? _lockController;

		/// <summary>Ishod otvaranja vrata po broju (null = kontroler nije odgovorio).</summary>
		public Func<int, DoorStatusCode?> OpenDoorResult { get; set; } = _ => DoorStatusCode.Open;
		public ConcurrentQueue<int> OpenedDoors { get; } = new();
		/// <summary>
		/// Pokrenuta praćenja vrata; test javlja ishod kroz <c>Notify</c> (callback aktivatora, pa kraj praćenja — Task koji je
		/// <see cref="TrackDoorStatus"/> vratio).
		/// </summary>
		public ConcurrentQueue<(int Door, DoorStatusCode Target, Func<DoorStatusCode?, ValueTask> Notify)> DoorTracks { get; } = new();
		public ConcurrentQueue<(int Door, DoorStatusCode? Status)> DoorFailures { get; } = new();

		/// <summary>Rok čekanja barkoda (pre prvog korišćenja <see cref="BarcodeScanTracker"/>).</summary>
		public TimeSpan BarcodeScanLimit { get; set; } = TimeSpan.FromSeconds(30);
		/// <summary>Sken kroz handler koji je postavio <see cref="BarcodeScanTracker"/> (kao pravi skener; i u Release build-u).</summary>
		public async ValueTask ScanAsync(string barcode)
		{
			var handler = BarcodeScannerClient.ProcessBarcode;
			if (handler != null) {
				await handler(barcode);
			}
		}

		/// <summary>Ako je postavljen, prikaz poruke traje dok se ne završi (kao rok prikaza u kiosku).</summary>
		public Task? MessageShown { get; set; }
		public Task PauseDoorWatcherAsync() => Task.CompletedTask;
		public TopPosServiceClient ServiceClient { get; }
		public BarcodeScannerClient BarcodeScannerClient { get; }
		public IPosClient? PosClient { get; }
		public PenPosServiceClient? PenPosServiceClient => null;
		public LanguageManager LanguageManager { get; }
		public IAudioPlayer AudioPlayer { get; } = new SilentAudio();
		public PaymentConfirmationSender PaymentConfirmationSender { get; }
		public ServerConfirmationSender ServerConfirmationSender => throw new NotSupportedException();
		public BarcodeScanTracker BarcodeScanTracker => _barcodeScanTracker ??= new BarcodeScanTracker(BarcodeScannerClient, BarcodeScanLimit, 3, null);
		private BarcodeScanTracker? _barcodeScanTracker;
		public OperationCountdownTimer OperationCountdownTimer { get; }
		public WorkflowContext WorkflowContext { get; }
		public WorkflowMachine WorkflowMachine { get; }
		public ILogger? Logger => null;
		public CourierBarcodeScanViewModel CourierBarcodeScanViewModel => _courierBarcodeScan ??= new(LanguageManager, null);
		private CourierBarcodeScanViewModel? _courierBarcodeScan;

		public string Localize(string key) => key;

		public ValueTask DisplayMessage(string message, MessageType messageType, int? displayTimeInSeconds = null)
		{
			lock (_messages) {
				_messages.Add((message, messageType));
			}
			return MessageShown == null ? ValueTask.CompletedTask : new ValueTask(MessageShown);
		}

		public void RemoveMessage() { }
		public void RemoveMessage(string message) => RemovedMessages.Enqueue(message);
		public ConcurrentQueue<string> RemovedMessages { get; } = new();
		public void SetCurrentPage(IWorkflowViewModel page)
		{
			lock (_pages) {
				_pages.Add(page);
			}
		}
		public IWorkflowViewModel? CurrentWorkflowViewModel { get; set; }
		public void SetCurrentStep(int stepNumber) { }
		public string MakeCurrentLanguagePath(string filename) => filename;
		public ValueTask FireResetAsync() => new(WorkflowMachine.FireAsync(WorkflowTrigger.Reset));
		public KioskLease? CurrentSession => null;
		public bool IsSessionActive(KioskLease? session) => true;
		public Task FireInSession(KioskLease? session, WorkflowTrigger trigger) => WorkflowMachine.FireAsync(trigger);
		public Task FireInSession<TArg>(KioskLease? session, StateMachine<WorkflowState, WorkflowTrigger>.TriggerWithParameters<TArg> trigger, TArg arg)
			=> WorkflowMachine.FireAsync(trigger, arg);
		public Task? RunInSession(KioskLease? session, Func<Task> work) => work();
		public Task TrackDoorStatus(int doorNumber, DoorStatusCode targetStatusCode, Func<DoorStatusCode?, ValueTask> notifyComplete)
		{
			var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			DoorTracks.Enqueue((doorNumber, targetStatusCode, async status => {
				try {
					await notifyComplete(status);
				}
				finally {
					done.TrySetResult();
				}
			}));
			return done.Task;
		}

		public async ValueTask HandleDoorActionFailure(int doorNumber, DoorStatusCode? unexpectedStatusCode, int messageDisplayTime,
														Func<ValueTask>? gotoPage, Serilog.ILogger contextLogger, CancellationToken cancellationToken)
		{
			DoorFailures.Enqueue((doorNumber, unexpectedStatusCode));
			if (gotoPage != null) {
				await gotoPage();
			}
		}

		private sealed class FakeLockController(FakeWorkflowHost host) : ILockControllerClient
		{
			public int NumberOfDoorsPerController => 8;

			public ValueTask<DoorStatusCode?> OpenDoorSingle(int doorNumber, TimeSpan? sendTimeout, TimeSpan? receiveTimeout, CancellationToken cancellationToken)
			{
				host.OpenedDoors.Enqueue(doorNumber);
				return ValueTask.FromResult(host.OpenDoorResult(doorNumber));
			}

			public ValueTask<DoorStatusCode?> OpenDoorSingle(int doorNumber, CancellationToken cancellationToken)
				=> OpenDoorSingle(doorNumber, null, null, cancellationToken);
			public ValueTask<DoorStatusCode?> OpenDoorSingle(int doorNumber, TimeSpan? receiveTimeout, CancellationToken cancellationToken)
				=> OpenDoorSingle(doorNumber, null, receiveTimeout, cancellationToken);
			public ValueTask<DoorOpenAttempt> OpenDoorOnce(int doorNumber, TimeSpan? receiveTimeout, CancellationToken cancellationToken) => throw new NotSupportedException();
			public ValueTask<DoorStatusCode?> CheckDoorSingle(int doorNumber, CancellationToken cancellationToken) => throw new NotSupportedException();
			public ValueTask<DoorStatusCode?> CheckDoorSingle(int doorNumber, TimeSpan? receiveTimeout, CancellationToken cancellationToken) => throw new NotSupportedException();
			public ValueTask<DoorStatusCode?> CheckDoorSingle(int doorNumber, TimeSpan? sendTimeout, TimeSpan? receiveTimeout, CancellationToken cancellationToken) => throw new NotSupportedException();
			public ValueTask<DoorStatusCode[]?> CheckDoorMultiple(int moduleNumber, CancellationToken cancellationToken) => throw new NotSupportedException();
			public ValueTask<DoorStatusCode[]?> CheckDoorMultiple(int moduleNumber, TimeSpan? receiveTimeout, CancellationToken cancellationToken) => throw new NotSupportedException();
			public ValueTask<DoorStatusCode[]?> CheckDoorMultiple(int moduleNumber, TimeSpan? sendTimeout, TimeSpan? receiveTimeout, CancellationToken cancellationToken) => throw new NotSupportedException();
		}

		private sealed class SilentAudio : IAudioPlayer
		{
			public void Play(string audioFileName) { }
			public void Reset() { }
		}
	}
}