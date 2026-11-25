using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Flatsoft.Common;
using Flatsoft.Common.UI;

namespace TopPos.Kiosk.ViewModels
{
	public class CourierDepositPromptViewModel : ViewModelBase, IWorkflowViewModel
	{
        public CourierDepositPromptViewModel(LanguageManager langManager, ILogger? logger)
			:base(langManager, logger)
		{
			this.DepositCommand = new AsyncCommand<object?>(async (o, ct) =>
				{
					_logger?.LogDebug($"{nameof(CourierDepositPromptViewModel)}: executing {nameof(DepositCommand)}.");
					await InvokeAfterClickFeedback(this.HandleDepositPackageClicked, ct);
					return null;
				});

			this.CancelCommand = new AsyncCommand<object?>(async (o, ct) =>
				{
					_logger?.LogDebug($"{nameof(CourierDepositPromptViewModel)}: executing {nameof(CancelCommand)}.");
					await InvokeAfterClickFeedback(this.HandleCancelClicked, ct);
					return null;
				});
		}

		private bool _expiredPackagesPickedUp;
        public bool ExpiredPackagesPickedUp 
		{ 
			get => _expiredPackagesPickedUp; 
			set
			{
				SetProperty(ref _expiredPackagesPickedUp, value);
				OnPropertyChanged(nameof(NoExpiredPackages));
			}
		}

        public bool NoExpiredPackages => !_expiredPackagesPickedUp;

        public IAsyncCommand DepositCommand { get; set; } 

		public ICommand CancelCommand { get; set; }

		public void Activate() {
			_logger?.LogInformation($"{GetType().Name}: aktivacija.");
		}

		public void Deactivate() {
			_logger?.LogInformation($"{GetType().Name}: deaktivacija.");
		}

		public Func<ValueTask>? HandleDepositPackageClicked { get; set; }

		public Func<ValueTask>? HandleCancelClicked { get; set; }
	}
}
