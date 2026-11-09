using Microsoft.Extensions.Logging;
using TopPos.Client.WPF.ViewModels;
using Flatsoft.Common;

namespace TopPos.Client.WPF
{
	public class StepViewModel : ViewModelBase
	{
		private readonly string _textId;
		public StepViewModel(LanguageManager languageManager, string textId, ILogger? logger = null)
			: base(languageManager, logger)
		{
			_textId = textId;
			languageManager.LanguageChanged += () => OnPropertyChanged(nameof(Name));
		}

		public string Name => this[_textId];

		private bool _isSelected;
		public bool IsSelected
		{
			get => _isSelected;
			set
			{
				if (_isSelected != value) {
					SetProperty(ref _isSelected, value);
				}
			}
		}
	}
}
