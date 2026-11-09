using Microsoft.Extensions.Logging;
using Flatsoft.Common;

namespace TopPos.Client.WPF.ViewModels
{
	public class RecipientPackagePickupViewModel : ViewModelBase, IWorkflowViewModel
	{
        public RecipientPackagePickupViewModel(LanguageManager langManager, ILogger? logger)
			:base(langManager, logger)
		{}

		private string? _pickupInstructions;
		public string? PickupInstructions
		{
			get => _pickupInstructions;
			set => SetProperty(ref _pickupInstructions, value);
		}

		public void Activate() {
			_logger?.LogInformation($"{GetType().Name}: aktivacija.");
		}

		public void Deactivate() {
			_logger?.LogInformation($"{GetType().Name}: deaktivacija.");
		}
	}
}
