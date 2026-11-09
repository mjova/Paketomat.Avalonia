using System.ComponentModel;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Flatsoft.Common;
using Flatsoft.Common.UI;

namespace TopPos.Kiosk.ViewModels
{
	public class NumericKeypadViewModel : ViewModelBase
    {
		private string? _code;
		private readonly string? _placeholderTextLanguageId;	// key in the .lang file, optional
		private static readonly RepeatedStringGenerator __maskedStringsGenerator;

		static NumericKeypadViewModel()
		{
			__maskedStringsGenerator = new RepeatedStringGenerator('*');
		}

        public NumericKeypadViewModel(LanguageManager languageManager, string? placeholderTextLanguageId, bool maskedInput = false, ILogger? logger = null)
			: base(languageManager, logger) 
        {
			// Code property needs to hook into language changes
			languageManager.LanguageChanged += () => OnPropertyChanged(nameof(Code));
			_placeholderTextLanguageId = placeholderTextLanguageId;
			this.MaskedInput = maskedInput;

            this.KeyInputCommand = new RelayCommand(OnKeyPressed);

			// this.MaximumCodeLength = 10;
		}

		private bool _useGraphicalKeys = true;
		public bool UseGraphicalKeys
		{
			get => _useGraphicalKeys;
			set
			{
				if (_useGraphicalKeys != value) {
					_useGraphicalKeys = value;
					OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
				}
			}
		}

		public ICommand KeyInputCommand { get; }

        public string? Code 
		{ 
			get {
				if (!string.IsNullOrEmpty(_code)) {
					return !MaskedInput? _code : __maskedStringsGenerator.GetRepeatedString(_code.Length);
				}
				if (!string.IsNullOrEmpty(_placeholderTextLanguageId)) { 
					return this[_placeholderTextLanguageId]; 
				}
				return string.Empty;
			}
			set { 
				SetProperty(ref _code, value);	
				OnPropertyChanged(nameof(CanConfirm));
			}
		}

		private int? _minimumCodeLength;
		public int? MinimumCodeLength 
		{ 
			get => _minimumCodeLength;
			set
			{
				if (value != null) { 
					_requiredCodeLength = null;
				}
				SetProperty(ref _minimumCodeLength, value);	
			}
		}

		public int? _requiredCodeLength;
		public int? RequiredCodeLength 
		{ 
			get => _requiredCodeLength;
			set
			{
                if (value != null)  {
                    _minimumCodeLength = null;
                }
                SetProperty(ref _requiredCodeLength, value);
			}
		}

		private int? _maximumCodeLength;
		public int? MaximumCodeLength
		{
			get => _maximumCodeLength;
			set => SetProperty(ref _maximumCodeLength, value);
		}

		public bool MaskedInput { get; }

		public Func<string, ValueTask>? HandleKeyPressed {  get; set; }	

        public bool CanConfirm 
		{
			get {
				if (string.IsNullOrEmpty(_code)) {
					return false;
				}
				if (_requiredCodeLength.HasValue && _code.Length != _requiredCodeLength) {
					return false;
				}
				if (_minimumCodeLength.HasValue && _code.Length < _minimumCodeLength) {
					return false;
				}
				return true;	
			}
		}

        public Func<string, ValueTask<bool>>? HandleCodeConfirmed {  get; set; }	

		private async void OnKeyPressed(object? parameter)
		{
			// Regardless of whether it'll be accepted further below, this keypress is sent
			// out, just so that the app can reset inactivity counter.
			var handler = this.HandleKeyPressed;
			if (handler != null) { 
				await handler((string)parameter!).ConfigureAwait(false);
			}

			var input = parameter as string;
			if (input == "clear") {
				this.Code = null;
			}
			else if (input == "confirm" && !string.IsNullOrEmpty(_code) && HandleCodeConfirmed != null) {
				await HandleCodeConfirmed(_code).ConfigureAwait(false);
			}
			else {
				if (this.RequiredCodeLength != null && _code?.Length == this.RequiredCodeLength) {
					return;
				}
				if (this.MaximumCodeLength != null && _code?.Length >= this.MaximumCodeLength) {
					return;
				}
				this.Code = string.Concat(_code ?? string.Empty, input);
			}
		}

		public void Clear() => this.Code = null;

		protected override string OnTextTranslationComplete(string key, string translation)
		{
			if (UseGraphicalKeys) { 
				if (key == "NumericKeypad.Clear") {
					return "✕";
				}
				if (key == "NumericKeypad.Confirm") {
					return "✓";
				}
			}
			return translation;
		}
	}

	public class RepeatedStringGenerator
	{
		private readonly char _character;
		private readonly Dictionary<int, string> _cache = new();

		public RepeatedStringGenerator(char character)
		{
			_character = character;
		}

		public string GetRepeatedString(int length)
		{
			if (length < 1) {
				throw new ArgumentOutOfRangeException(nameof(length), "Length must be at least 1.");
			}
        
			if (!_cache.TryGetValue(length, out string? result))
			{
				result = new string(_character, length);
				_cache[length] = result;
			}

			return result;
		}
	}

}
