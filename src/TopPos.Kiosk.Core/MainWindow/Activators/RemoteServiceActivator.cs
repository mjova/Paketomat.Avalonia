using TopPos.Kiosk.AgentCommunication;

namespace TopPos.Kiosk.ViewModels
{
	/// <summary>
	/// OnEntry za <see cref="Workflow.WorkflowState.RemoteService"/>: stranica servisne sesije Agenta. Sesiju vodi
	/// <see cref="RemoteServiceSession"/> (rokovi, otvaranje, kraj); kraj sesije vraća workflow na Start, čiji OnEntry
	/// deaktivira ovu stranicu. Opšti rok ekrana se ne koristi — sesija ima svoje rokove, a Reset se u njoj ignoriše.
	/// v4: stranica je ViewModel (bez WPF Page objekata).
	/// </summary>
	internal sealed class RemoteServiceActivator : WorkflowActivatorBase
	{
		private readonly RemoteServiceViewModel _remoteServiceViewModel;

		public RemoteServiceActivator(IWorkflowHost host) : base(host)
		{
			_remoteServiceViewModel = new RemoteServiceViewModel(_barcodeScannerClient, _languageManager, _configuration.Locker.LockerId, _logger);
		}

		public ValueTask GoToRemoteServicePage(RemoteServiceSession session)
		{
			_currentWorkflowViewModel?.Deactivate();
			_remoteServiceViewModel.Bind(session);
			_remoteServiceViewModel.Activate();
			_currentWorkflowViewModel = _remoteServiceViewModel;
			this.CurrentPage = _remoteServiceViewModel;
			SetCurrentStep(0);
			return ValueTask.CompletedTask;
		}
	}
}
