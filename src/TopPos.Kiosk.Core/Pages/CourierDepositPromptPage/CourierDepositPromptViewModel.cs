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
				OnPropertyChanged(nameof(AllExpiredPackagesPickedUp));
			}
		}

        public bool NoExpiredPackages => !_expiredPackagesPickedUp;

		private bool _expiredPickupIncomplete;
		/// <summary>v4: preuzimanje isteklih paketa nije potpuno (vidi <c>WorkflowContext.ExpiredPickupIncomplete</c>).</summary>
		public bool ExpiredPickupIncomplete
		{
			get => _expiredPickupIncomplete;
			set
			{
				SetProperty(ref _expiredPickupIncomplete, value);
				OnPropertyChanged(nameof(AllExpiredPackagesPickedUp));
			}
		}

		/// <summary>v4: „Svi paketi su uspešno preuzeti“ samo posle potpunog preuzimanja (v2: posle svakog izlaska iz preuzimanja).</summary>
		public bool AllExpiredPackagesPickedUp => _expiredPackagesPickedUp && !_expiredPickupIncomplete;

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
