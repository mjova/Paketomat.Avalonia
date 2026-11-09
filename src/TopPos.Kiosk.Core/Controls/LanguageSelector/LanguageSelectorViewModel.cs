using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Extensions.Logging;
using Flatsoft.Common;
using Flatsoft.Common.UI;

namespace TopPos.Client.WPF.ViewModels
{
	public class LanguageSelectorItem
	{
		public LanguageSelectorItem(LanguageData languageData, DrawingGroup? laguageImage) 
		{ 
			this.LanguageData = languageData;
			this.LanguageImage = laguageImage;
		}

		public LanguageData LanguageData { get; }	

		public DrawingGroup? LanguageImage { get; }

		public ICommand? SelectLanguageCommand { get; set; }
	}

	public class LanguageSelectorViewModel : ViewModelBase
    {
        public LanguageSelectorViewModel(LanguageManager languageManager, Func<string?, DrawingGroup?> imageFinder, ILogger? logger)
			: base(languageManager, logger) 
        {
			this.Languages = languageManager.Languages.Select(
				lang => new LanguageSelectorItem(lang, imageFinder(lang.ImageResourceName))
							{
								SelectLanguageCommand = new RelayCommand(p => {
										var languageId = (string)p!;
										languageManager.SetCurrentLanguage(languageId);
										this.HandleLanguageSelected?.Invoke(languageId);
								})
							}
			);
		}

		public IEnumerable<LanguageSelectorItem> Languages { get; }

		public Action<string>? HandleLanguageSelected { get; set; }
    }
}
