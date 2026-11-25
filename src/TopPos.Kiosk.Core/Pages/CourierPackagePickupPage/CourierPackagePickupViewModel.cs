using Microsoft.Extensions.Logging;
using Flatsoft.Common;

namespace TopPos.Kiosk.ViewModels
{
	public class CourierPackagePickupViewModel : ViewModelBase, IWorkflowViewModel
	{
        public CourierPackagePickupViewModel(LanguageManager langManager, ILogger? logger)
			:base(langManager, logger)
		{}

        public bool ShowInstructions => !ShowCloseDoorMessage;

        private bool _showCloseDoorMessage;
        public bool ShowCloseDoorMessage 
		{ 
			get => _showCloseDoorMessage; 
			set {
				SetProperty(ref _showCloseDoorMessage, value);
				OnPropertyChanged(nameof(ShowInstructions));
			}
		}

        public void Activate() {
			_logger?.LogInformation($"{GetType().Name}: aktivacija.");
			ShowCloseDoorMessage = false;
		}

		public void Deactivate() {
			_logger?.LogInformation($"{GetType().Name}: deaktivacija.");
		}
	}
}
