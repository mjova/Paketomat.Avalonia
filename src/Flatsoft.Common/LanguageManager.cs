using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Flatsoft.Common
{
	public class LanguageData
	{
		public string LanguageId { get; set; } = default!;
		public string LanguageDisplayName { get; set; } = default!;
		public string? ImageResourceName { get; set; } 
		public IReadOnlyDictionary<string, string> TextEntries { get; set; } = default!;
	}

	public class LanguageManager
	{
		private readonly ILogger? _logger;
		private List<LanguageData>? _languages;	
		public delegate void LanguageChangedHandler();
		public event LanguageChangedHandler? LanguageChanged;

		public LanguageManager(ILogger? logger)
		{
			_logger = logger;
		}

		public void LoadLanguageResources(string rootLanguageDirectoryPath, string fileExtension = ".json", string? setLanguageId = null)
		{
			_logger?.LogInformation($"{nameof(LanguageManager)}: učitavanje jezičkih resursa ({fileExtension}) iz {rootLanguageDirectoryPath}");

			_languages = new List<LanguageData>();

			foreach (var languageDir in Directory.GetDirectories(rootLanguageDirectoryPath)) 
			{ 
				_logger?.LogInformation($"{nameof(LanguageManager)}: {nameof(LoadLanguageResources)}: učitavanje jezičkih resursa iz {languageDir}");
				try
				{
					var langFilePath = Path.Combine(languageDir, $"{Path.GetFileName(languageDir)}{fileExtension}");
					if (!File.Exists(langFilePath)) {
						throw new FileNotFoundException(langFilePath);
					}
					string json = File.ReadAllText(langFilePath);
					LanguageData? langData = JsonSerializer.Deserialize<LanguageData>(json);
					if (langData != null && !string.IsNullOrEmpty(langData.LanguageId)) {
						_languages.Add(langData);
					}
				}
				catch (Exception ex)
				{
					_logger?.LogError($"{nameof(LanguageManager)}: exception: {ex.Message}");	
				}
			}
			if (!string.IsNullOrEmpty(setLanguageId)) 
			{
				if (!SetCurrentLanguage(setLanguageId))
				{
					// Fallback: take the first one, if available
					var first = _languages?.FirstOrDefault();
					if (first != null) {
						SetCurrentLanguage(first.LanguageId);	// triggers the event
					}
					
				}
			}
		}

		public IEnumerable<LanguageData> Languages => _languages ?? Enumerable.Empty<LanguageData>();

		public bool SetCurrentLanguage(string languageId)
		{
			var language = _languages?.FirstOrDefault(ld => ld.LanguageId == languageId);
			if (language == null) {
				_logger?.LogError($"{nameof(LanguageManager)}: jezik [{languageId}] nepoznat, aktivacija nije uspela.");
				return false;
			}
			if (this.CurrentLanguage?.LanguageId != languageId) { 
				_logger?.LogInformation($"{nameof(LanguageManager)}: aktiviranje jezika [{languageId}]");
				this.CurrentLanguage = language;
				this.LanguageChanged?.Invoke();
				_logger?.LogInformation($"{nameof(LanguageManager)}: jezik [{languageId}] je aktivan.");
			}
			return true;
		}

		public LanguageData? CurrentLanguage { get; protected set; }

		public void LoadBrandOverrides(string brandOverridePath, string fileExtension = ".json")
		{
			if (!Directory.Exists(brandOverridePath))
			{
				_logger?.LogInformation($"{nameof(LanguageManager)}: {nameof(LoadBrandOverrides)}: brand override direktorijum ne postoji: {brandOverridePath}");
				return;
			}

			_logger?.LogInformation($"{nameof(LanguageManager)}: {nameof(LoadBrandOverrides)}: učitavanje brand-specific override fajlova iz {brandOverridePath}");

			// Iterate through all already loaded languages
			foreach (var language in _languages ?? Enumerable.Empty<LanguageData>())
			{
				var overrideFilePath = Path.Combine(brandOverridePath, $"{language.LanguageId}{fileExtension}");
				var fullOverrideFilePath = Path.GetFullPath(overrideFilePath);
				if (!File.Exists(overrideFilePath))
				{
					_logger?.LogDebug($"{nameof(LanguageManager)}: {nameof(LoadBrandOverrides)}: override fajl ne postoji: {overrideFilePath}");
					continue;
				}

				try
				{
					_logger?.LogInformation($"{nameof(LanguageManager)}: {nameof(LoadBrandOverrides)}: učitavanje override-a za jezik [{language.LanguageId}] iz {overrideFilePath}");
					string json = File.ReadAllText(overrideFilePath);
					LanguageData? overrideData = JsonSerializer.Deserialize<LanguageData>(json);

					if (overrideData?.TextEntries != null)
					{
						// Merge override entries into existing language
						var existingEntries = new Dictionary<string, string>(language.TextEntries);
						foreach (var overrideEntry in overrideData.TextEntries)
						{
							existingEntries[overrideEntry.Key] = overrideEntry.Value;
							_logger?.LogInformation($"{nameof(LanguageManager)}: {nameof(LoadBrandOverrides)}: override [{language.LanguageId}]: '{overrideEntry.Key}' = '{overrideEntry.Value}'");
						}

						// Update language TextEntries with merged values
						var languageType = language.GetType();
						var textEntriesProperty = languageType.GetProperty(nameof(LanguageData.TextEntries));
						textEntriesProperty?.SetValue(language, existingEntries);

						_logger?.LogInformation($"{nameof(LanguageManager)}: {nameof(LoadBrandOverrides)}: uspešno učitano {overrideData.TextEntries.Count} override unosa za jezik [{language.LanguageId}]");
					}
				}
				catch (Exception ex)
				{
					_logger?.LogError($"{nameof(LanguageManager)}: {nameof(LoadBrandOverrides)}: greška pri učitavanju override fajla {overrideFilePath}: {ex.Message}");
				}
			}
		}
	}
}
