using Microsoft.Extensions.Logging;
using Flatsoft.Common;

namespace TopPos.Kiosk.ViewModels
{
	public class CourierManualLoginViewModel : ViewModelBase, IWorkflowViewModel
	{
		public CourierManualLoginViewModel(int minimumUnlockCodeLength, int maximumUnlockCodeLength, LanguageManager langManager, ILogger? logger)
			:base(langManager, logger)
		{
			this.Keypad = new NumericKeypadViewModel(langManager, "CourierManualLoginPage.NumericKeypad.CodePlaceholder", true) 
								{
									MinimumCodeLength = minimumUnlockCodeLength,
									MaximumCodeLength = maximumUnlockCodeLength
								};	
		}

        public NumericKeypadViewModel Keypad { get; }

        public void Activate() {
			_logger?.LogInformation($"{GetType().Name}: aktivacija.");
			this.Keypad.HandleCodeConfirmed = this.HandleCodeConfirmed;
			this.Keypad.HandleKeyPressed = this.HandleKeyPressed;
			this.Keypad.Clear();
		}

		public void Deactivate() {
			_logger?.LogInformation($"{GetType().Name}: deaktivacija.");
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
