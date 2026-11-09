
using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Flatsoft.Common;

namespace TopPos.Client.WPF.ViewModels
{
	public abstract class ViewModelBase : Flatsoft.Common.UI.ViewModelBase
	{
		private static readonly PropertyChangedEventArgs __indexerChangedArgs = new PropertyChangedEventArgs("Item[]");

		private readonly LanguageManager _languageManager;
		protected readonly ILogger? _logger;

        protected ViewModelBase(LanguageManager languageManager, ILogger? logger)
        {
            _languageManager = languageManager;
			_languageManager.LanguageChanged += OnLanguageChanged;
			_logger = logger;
        }

		protected virtual void OnLanguageChanged() => OnPropertyChanged(__indexerChangedArgs);

		// Language translation lookup. UI controls bind to this property using "Binding [<key>]" syntax.
		public string this[string key] 
		{ 
			get { 
				var translation = _languageManager.CurrentLanguage?.TextEntries?.TryGetValue(key, out var text) == true? text : $"<{key}>?";
				return OnTextTranslationComplete(key, translation);
			}
		}

		protected virtual string OnTextTranslationComplete(string key, string translation) => translation;

		// Daje click pulse animaciji vremena da se vidi pre nego što Command pređe
		// na sledeću stranicu. Pozvati na početku Command handler-a koji navigira.
		protected static Task DelayForClickFeedback(CancellationToken ct = default)
			=> Task.Delay(200, ct);

		/// <summary>
		/// Odziv klika pa handler. Handler se čita pre odziva (pozivalac ga prosleđuje): posle 200 ms pripada ekranu i sesiji na
		/// kojoj je kliknuto, a ne sledećoj koja ga je u međuvremenu zamenila.
		/// </summary>
		protected static async Task InvokeAfterClickFeedback(Func<ValueTask>? handler, CancellationToken ct)
		{
			await DelayForClickFeedback(ct);
			if (handler != null) {
				await handler();
			}
		}
	}
}
