using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Flatsoft.Common;
using Flatsoft.Common.UI;

namespace TopPos.Kiosk.ViewModels
{
	public class CourierDepositConfirmationViewModel : ViewModelBase, IWorkflowViewModel
	{
        public CourierDepositConfirmationViewModel(LanguageManager langManager, ILogger? logger)
			:base(langManager, logger)
		{
			this.ConfirmCommand = new AsyncCommand<object?>(async (o, ct) =>
				{
					_logger?.LogDebug($"{nameof(CourierDepositConfirmationViewModel)}: executing {nameof(ConfirmCommand)}.");
					// v4: „Da“ odlučuje odmah, kao i „Ne“ — inače bi „Ne“ pritisnuto posle „Da“ u 200 ms odziva poništilo potvrdu.
					var handler = this.HandleConfirmClicked;
					if (handler != null) {
						await handler();
					}
					return null;
				});

			this.CancelCommand = new AsyncCommand<object?>(async (o, ct) =>
				{
					_logger?.LogDebug($"{nameof(CourierDepositConfirmationViewModel)}: executing {nameof(CancelCommand)}.");
					// v4: „Ne“ odlučuje odmah, bez čekanja odziva klika (200 ms): istek roka u tom razmaku je ranije slao potvrdu,
					// a prvo „Da“ ostaje odluka (TryDecide u aktivatoru).
					var handler = this.HandleCancelClicked;
					if (handler != null) {
						await handler();
					}
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
