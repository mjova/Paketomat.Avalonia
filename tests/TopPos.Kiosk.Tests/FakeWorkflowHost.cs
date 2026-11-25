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
	/// Poruke i prikazane stranice se beleže umesto prikaza, lokalizacija vraća ključ. Ono što test ne koristi baca
	/// <see cref="NotSupportedException"/>.
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

		public void Dispose() => PaymentConfirmationSender.Dispose();

		public string PaymentConfirmationFile { get; }
		public IEnumerable<string> Messages => _messages.Select(m => m.Item1);
		public IEnumerable<(string, MessageType)> MessagesWithType => _messages;
		/// <summary>Stranice redom kojim ih je aktivator prikazao.</summary>
		public IReadOnlyList<IWorkflowViewModel> Pages => _pages;

		public AppConfiguration Configuration { get; }
		public ILockControllerClient LockControllerClient => throw new NotSupportedException();
		public Task PauseDoorWatcherAsync() => Task.CompletedTask;
		public TopPosServiceClient ServiceClient { get; }
		public BarcodeScannerClient BarcodeScannerClient { get; }
		public IPosClient? PosClient { get; }
		public PenPosServiceClient? PenPosServiceClient => null;
		public LanguageManager LanguageManager { get; }
		public IAudioPlayer AudioPlayer { get; } = new SilentAudio();
		public PaymentConfirmationSender PaymentConfirmationSender { get; }
		public ServerConfirmationSender ServerConfirmationSender => throw new NotSupportedException();
		public BarcodeScanTracker BarcodeScanTracker => throw new NotSupportedException();
		public OperationCountdownTimer OperationCountdownTimer { get; }
		public WorkflowContext WorkflowContext { get; }
		public WorkflowMachine WorkflowMachine { get; }
		public ILogger? Logger => null;

		public string Localize(string key) => key;

		public ValueTask DisplayMessage(string message, MessageType messageType, int? displayTimeInSeconds = null)
		{
			lock (_messages) {
				_messages.Add((message, messageType));
			}
			return ValueTask.CompletedTask;
		}

		public void RemoveMessage() { }
		public void SetCurrentPage(IWorkflowViewModel page) => _pages.Add(page);
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
			=> throw new NotSupportedException();
		public ValueTask HandleDoorActionFailure(int doorNumber, DoorStatusCode? unexpectedStatusCode, int messageDisplayTime,
												Func<ValueTask>? gotoPage, Serilog.ILogger contextLogger, CancellationToken cancellationToken)
			=> throw new NotSupportedException();

		private sealed class SilentAudio : IAudioPlayer
		{
			public void Play(string audioFileName) { }
			public void Reset() { }
		}
	}
}