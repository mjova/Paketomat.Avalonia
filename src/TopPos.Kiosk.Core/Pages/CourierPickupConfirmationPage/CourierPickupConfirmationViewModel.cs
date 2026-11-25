using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Flatsoft.Common;
using Flatsoft.Common.UI;

namespace TopPos.Kiosk.ViewModels
{
	public class CourierPickupConfirmationViewModel : ViewModelBase, IWorkflowViewModel
	{
        public CourierPickupConfirmationViewModel(LanguageManager langManager, ILogger? logger)
			:base(langManager, logger)
		{
			this.ConfirmCommand = new AsyncCommand<object?>(async (o, ct) =>
				{
					_logger?.LogDebug($"{nameof(CourierPickupConfirmationViewModel)}: executing {nameof(ConfirmCommand)}.");
					await InvokeAfterClickFeedback(this.HandleConfirmClicked, ct);
					return null;
				});

			this.CancelCommand = new AsyncCommand<object?>(async (o, ct) =>
				{
					_logger?.LogDebug($"{nameof(CourierPickupConfirmationViewModel)}: executing {nameof(CancelCommand)}.");
					await InvokeAfterClickFeedback(this.HandleCancelClicked, ct);
					return null;
				});
		}

        public IAsyncCommand ConfirmCommand { get; set; } 

		public ICommand CancelCommand { get; set; }

		public void Activate() {
			_logger?.LogInformation($"{GetType().Name}: aktivacija.");
		}

		public void Deactivate() {
			_logger?.LogInformation($"{GetType().Name}: deaktivacija.");
		}

		public Func<ValueTask>? HandleConfirmClicked { get; set; }

		public Func<ValueTask>? HandleCancelClicked { get; set; }
	}
}
