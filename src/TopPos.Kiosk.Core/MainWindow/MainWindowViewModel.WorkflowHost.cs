using Flatsoft.Common;
using Flatsoft.Common.UI;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using TopPos.Client.WPF.Pages;
using TopPos.Client.WPF.Workflow;
using TopPos.LockController;
using TopPos.PenPosService;
using Stateless;
using TopPos.Service;

namespace TopPos.Client.WPF.ViewModels
{
	/// <summary>
	/// <see cref="IWorkflowHost"/> implementacija — izlaže MWVM-ova polja/operacije activator klasama
	/// (Faza 5). Sve eksplicitne implementacije (ne šire javni API MWVM-a); delegiraju na postojeće
	/// private članove.
	/// </summary>
	public partial class MainWindowViewModel : IWorkflowHost
	{
		AppConfiguration IWorkflowHost.Configuration => _configuration;
		ILockControllerClient IWorkflowHost.LockControllerClient => _lockControllerClient;
		Task IWorkflowHost.PauseDoorWatcherAsync() => PauseDoorWatcherAsync();
		TopPosServiceClient IWorkflowHost.ServiceClient => _serviceClient;
		BarcodeScannerClient IWorkflowHost.BarcodeScannerClient => _barcodeScannerClient;
		IPosClient? IWorkflowHost.PosClient => _posClient;
		PenPosServiceClient? IWorkflowHost.PenPosServiceClient => _penPosServiceClient;
		LanguageManager IWorkflowHost.LanguageManager => _languageManager;
		AudioPlayer IWorkflowHost.AudioPlayer => _audioPlayer;
		PaymentConfirmationSender IWorkflowHost.PaymentConfirmationSender => _paymentConfirmationSender;
		ServerConfirmationSender IWorkflowHost.ServerConfirmationSender => _serverConfirmationSender;
		BarcodeScanTracker IWorkflowHost.BarcodeScanTracker => _barcodeScanTracker;
		OperationCountdownTimer IWorkflowHost.OperationCountdownTimer => _operationCountdownTimer;
		WorkflowContext IWorkflowHost.WorkflowContext => _workflowContext;
		WorkflowMachine IWorkflowHost.WorkflowMachine => _workflowMachine;
		ILogger? IWorkflowHost.Logger => _logger;

		CourierBarcodeScanViewModel IWorkflowHost.CourierBarcodeScanViewModel => _courierBarcodeScanViewModel;
		CourierBarcodeScanPage IWorkflowHost.CourierBarcodeScanPage => _courierBarcodeScanPage;

		string IWorkflowHost.Localize(string key) => this[key];
		ValueTask IWorkflowHost.DisplayMessage(string message, MessageType messageType, int? displayTimeInSeconds)
			=> DisplayMessage(message, messageType, displayTimeInSeconds);
		void IWorkflowHost.RemoveMessage() => RemoveMessage();
		void IWorkflowHost.SetCurrentPage(UserControl page) => this.CurrentPage = page;
		IWorkflowViewModel? IWorkflowHost.CurrentWorkflowViewModel
		{
			get => _currentWorkflowViewModel;
			set => _currentWorkflowViewModel = value;
		}
		void IWorkflowHost.SetCurrentStep(int stepNumber) => SetCurrentStep(stepNumber);
		string IWorkflowHost.MakeCurrentLanguagePath(string filename) => MakeCurrentLanguagePath(filename);
		ValueTask IWorkflowHost.FireResetAsync() => FireResetAsync();
		KioskLease? IWorkflowHost.CurrentSession => _coordinator.ActiveSession;
		bool IWorkflowHost.IsSessionActive(KioskLease? session) => IsSessionActive(session);
		Task IWorkflowHost.FireInSession(KioskLease? session, WorkflowTrigger trigger)
			=> _workflowMachine.FireGuardedAsync(() => IsSessionActive(session), trigger);
		Task IWorkflowHost.FireInSession<TArg>(KioskLease? session, StateMachine<WorkflowState, WorkflowTrigger>.TriggerWithParameters<TArg> trigger, TArg arg)
			=> _workflowMachine.FireGuardedAsync(() => IsSessionActive(session), trigger, arg);
		Task? IWorkflowHost.RunInSession(KioskLease? session, Func<Task> work)
			=> session != null ? _coordinator.TryRun(session, work) : null;
		Task IWorkflowHost.TrackDoorStatus(int doorNumber, DoorStatusCode targetStatusCode, Func<DoorStatusCode?, ValueTask> notifyComplete)
			=> TrackDoorStatus(doorNumber, targetStatusCode, notifyComplete);
		ValueTask IWorkflowHost.HandleDoorActionFailure(int doorNumber, DoorStatusCode? unexpectedStatusCode, int messageDisplayTime,
												Func<ValueTask>? gotoPage, Serilog.ILogger contextLogger, CancellationToken cancellationToken)
			=> HandleDoorActionFailure(doorNumber, unexpectedStatusCode, messageDisplayTime, gotoPage, contextLogger, cancellationToken);
	}
}
