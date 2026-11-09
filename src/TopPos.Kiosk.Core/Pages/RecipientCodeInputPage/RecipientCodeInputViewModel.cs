using Microsoft.Extensions.Logging;
using Flatsoft.Common;

namespace TopPos.Kiosk.ViewModels
{
	public class RecipientCodeInputViewModel : ViewModelBase, IWorkflowViewModel
	{
		private readonly BarcodeScannerClient _barcodeScannerClient;
        public RecipientCodeInputViewModel(BarcodeScannerClient barcodeScannerClient, int minimumUnlockCodeLength, int maximumUnlockCodeLength, LanguageManager langManager, ILogger? logger)
			:base(langManager, logger)
		{
			_barcodeScannerClient = barcodeScannerClient;

			this.Keypad = new NumericKeypadViewModel(langManager, "NumericKeypad.CodePlaceholder") 
								{
									MinimumCodeLength = minimumUnlockCodeLength,
									MaximumCodeLength = maximumUnlockCodeLength
								};	
		}

        public NumericKeypadViewModel Keypad { get; }

        public void Activate() {
			_logger?.LogInformation($"{GetType().Name}: aktivacija.");
			_logger?.LogInformation($"{GetType().Name}: postavljanje barkod hendlera.");
			_barcodeScannerClient.ProcessBarcode = this.HandleScannedBarcode;
			this.Keypad.HandleCodeConfirmed = this.HandleCodeConfirmed;
			this.Keypad.HandleKeyPressed = this.HandleKeyPressed;
			this.Keypad.Clear();
		}

		public void Deactivate() {
			_logger?.LogInformation($"{GetType().Name}: deaktivacija.");
			_logger?.LogInformation($"{GetType().Name}: uklanjanje barkod hendlera.");
			_barcodeScannerClient.ProcessBarcode = null;
			this.Keypad.HandleCodeConfirmed = null;
			this.Keypad.HandleKeyPressed = null;
			this.Keypad.Clear();
		}

		public Func<string, ValueTask>? HandleKeyPressed { get; set; }

		public Func<string, ValueTask<bool>>? HandleCodeConfirmed { get; set; }

		public Func<string, ValueTask<bool>>? HandleScannedBarcode { get; set; }

		public Action? HandleCancelClicked { get; set; }
	}
}
