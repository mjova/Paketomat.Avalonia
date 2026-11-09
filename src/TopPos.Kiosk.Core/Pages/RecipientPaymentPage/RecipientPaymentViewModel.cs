using Microsoft.Extensions.Logging;
using Flatsoft.Common;

namespace TopPos.Client.WPF.ViewModels
{
	public class RecipientPaymentViewModel : ViewModelBase, IWorkflowViewModel
	{
		private readonly string _infoMessageLanguageKey;
		private readonly string _amountDetailsLanguageKey;
		private readonly string _formatDecimalPlaces;
		private readonly string _currencyDisplayName;

		public RecipientPaymentViewModel(LanguageManager langManager, 
										string infoMessageLanguageKey,
										string amountDetailsLanguageKey,
										int amountDecimalPlaces,
										string currencyDisplayName,
										ILogger? logger)
			:base(langManager, logger)
		{
			_infoMessageLanguageKey = infoMessageLanguageKey;
			_amountDetailsLanguageKey = amountDetailsLanguageKey;
			_formatDecimalPlaces = $"F{amountDecimalPlaces}";
			
			_currencyDisplayName = currencyDisplayName;
		}

        private string? _infoMessage;
        public string? InfoMessage 
		{ 
			get => _infoMessage;
			set
			{
				SetProperty(ref _infoMessage, value);
			}
		}

		private string? _amountDetails;
        public string? AmountDetails 
		{ 
			get => _amountDetails;
			set
			{
				SetProperty(ref _amountDetails, value);
			}
		}

		private decimal? _amount;
		public  decimal? Amount { 
			get => _amount; 
			set
			{
				if (_amount != value)
				{
					_amount = value;
					UpdateAmountDetails();
				}
			}
		}

		protected override void OnLanguageChanged()
		{
			base.OnLanguageChanged();   // updates all indexer properties

			UpdateInfoMessage();
		}

		private void UpdateInfoMessage() =>	this.InfoMessage = this[_infoMessageLanguageKey];

		private void UpdateAmountDetails()
		{
			this.AmountDetails = this[_amountDetailsLanguageKey]
									.Replace("{amount}", _amount?.ToString(_formatDecimalPlaces) ?? "?")
									.Replace("{currencyDisplayName}", _currencyDisplayName);
		}

		public void Activate()
		{
			_logger?.LogInformation($"{GetType().Name}: aktivacija.");
			UpdateInfoMessage();
			UpdateAmountDetails();
		}

		public void Deactivate() {
			_logger?.LogInformation($"{GetType().Name}: deaktivacija.");
			_amount = null;
		}

		public Func<string, ValueTask>? HandlePhoneConfirmed { get; set; }
	}
}
