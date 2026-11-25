using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Flatsoft.Common;
using Flatsoft.Common.UI;

namespace TopPos.Kiosk.ViewModels
{
	public class SenderBarcodeScanViewModel : ViewModelBase, IWorkflowViewModel
	{
        public SenderBarcodeScanViewModel(LanguageManager langManager, ILogger? logger)
			:base(langManager, logger)
		{
			this.CancelCommand = new AsyncCommand<bool>(async (o, ct) => {
					_logger?.LogDebug($"{nameof(SenderBarcodeScanViewModel)}: executing {nameof(CancelCommand)}.");
					await InvokeAfterClickFeedback(this.HandleCancelClicked, ct);
					return true;
				});
		}

		private string? _barcode;
        public string? Barcode
		{
			get => _barcode;
			set
			{
				SetProperty(ref _barcode, value);
			}
		}

		public ICommand CancelCommand { get; set; }

		public Func<ValueTask>? HandleCancelClicked { get; set; }

		public void Activate() {
			_logger?.LogInformation($"{GetType().Name}: aktivacija.");
		}

		public void Deactivate() {
			_logger?.LogInformation($"{GetType().Name}: deaktivacija.");
			_barcode = null;
		}
	}
}
