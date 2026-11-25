using Microsoft.Extensions.Logging;
using Flatsoft.Common;
using Flatsoft.Common.UI;
using TopPos.Service;

namespace TopPos.Kiosk.ViewModels
{
	public class CourierPickupPromptViewModel : ViewModelBase, IWorkflowViewModel
	{
        public CourierPickupPromptViewModel(LanguageManager langManager, ILogger? logger)
			:base(langManager, logger)
		{
			this.PickUpCommand = new AsyncCommand<object?>(async (o, ct) =>
				{
					_logger?.LogDebug($"{nameof(CourierPickupPromptViewModel)}: executing {nameof(PickUpCommand)}.");
					await InvokeAfterClickFeedback(this.HandlePickUpExpiredPackageClicked, ct);
					return null;
				});

			this.CancelCommand = new AsyncCommand<object?>(async (o, ct) =>
				{
					_logger?.LogDebug($"{nameof(CourierPickupPromptViewModel)}: executing {nameof(CancelCommand)}.");
					await InvokeAfterClickFeedback(this.HandleCancelClicked, ct);
					return null;
				});
		}

		private int _remainingExpiredPackagesCount = 0;
		public int RemainingExpiredPackagesCount {  
			get => _remainingExpiredPackagesCount;
			set => SetProperty(ref _remainingExpiredPackagesCount, value); 
		}

		public BarkodPozicija? CurrentPackage { get; set; }

		public IAsyncCommand PickUpCommand { get; } 

		public IAsyncCommand CancelCommand { get; }

		public void Activate() {
			_logger?.LogInformation($"{GetType().Name}: aktivacija.");
		}

		public void Deactivate() {
			_logger?.LogInformation($"{GetType().Name}: deaktivacija.");
			this.CurrentPackage = null;
		}

		public Func<ValueTask>? HandlePickUpExpiredPackageClicked { get; set; }

		public Func<ValueTask>? HandleCancelClicked { get; set; }
	}
}
