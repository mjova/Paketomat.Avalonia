using Microsoft.Extensions.Logging;
using Flatsoft.Common;

namespace TopPos.Kiosk.ViewModels
{
	public class RecipientPhoneInputViewModel : ViewModelBase, IWorkflowViewModel
	{
        public RecipientPhoneInputViewModel(int minimumPhoneLength, int maximumPhoneLength, LanguageManager langManager, ILogger? logger)
			:base(langManager, logger)
		{
			this.Keypad = new NumericKeypadViewModel(langManager, "NumericKeypad.PhonePlaceholder", maskedInput: true) {
				MinimumCodeLength = minimumPhoneLength,
				MaximumCodeLength = maximumPhoneLength
			};
		}

        public NumericKeypadViewModel Keypad { get; }

        public void Activate() {
			_logger?.LogInformation($"{GetType().Name}: aktivacija.");
			this.Keypad.HandleCodeConfirmed = this.HandlePhoneConfirmed;
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

		public Func<string, ValueTask<bool>>? HandlePhoneConfirmed { get; set; }
	}
}
