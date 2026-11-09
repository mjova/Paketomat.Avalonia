using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Flatsoft.Common;
using Microsoft.Extensions.Logging;

namespace TopPos.Client.WPF
{
    /// <summary>
    /// Prevodi sirove statusne/greške poruke koje stižu od spoljnih sistema
    /// (POS terminal, server) u tekst na trenutno izabranom jeziku. Mapiranje je
    /// u jezičkim fajlovima pod ključem <c>"&lt;prefiks&gt;&lt;normalizovana poruka&gt;"</c>
    /// (npr. <c>"PosTerminalMessage.PROVUCI/UBACI KARTICU"</c>, <c>"ServerMessage.NEISPRAVAN KOD ZA OTVARANJE"</c>).
    /// <para>
    /// Ako poruka nije prepoznata (nema ključa), vraća se <b>originalni tekst nepromenjen</b>
    /// i poruka se loguje — tako se nepoznate poruke lako otkriju i dodaju u jezičke fajlove.
    /// </para>
    /// </summary>
    internal sealed class ExternalMessageLocalizer
    {
        private readonly LanguageManager _languageManager;
        private readonly string _keyPrefix;
        private readonly ILogger? _logger;
        private static readonly Regex _whitespace = new(@"\s+", RegexOptions.Compiled);

        /// <param name="keyPrefix">Prefiks ključa u jezičkim fajlovima, npr. "PosTerminalMessage." ili "ServerMessage.".</param>
        public ExternalMessageLocalizer(LanguageManager languageManager, string keyPrefix, ILogger? logger = null)
        {
            _languageManager = languageManager;
            _keyPrefix = keyPrefix;
            _logger = logger;
        }

        /// <summary>
        /// Vraća lokalizovan tekst za prepoznatu poruku, inače originalni tekst (uz log).
        /// <c>null</c>/prazno se vraća nepromenjeno (pogodno za <c>Localize(x) ?? fallback</c>).
        /// </summary>
        [return: NotNullIfNotNull(nameof(rawMessage))]
        public string? Localize(string? rawMessage)
        {
            if (string.IsNullOrWhiteSpace(rawMessage)) {
                return rawMessage;
            }

            var key = _keyPrefix + Normalize(rawMessage);
            var entries = _languageManager.CurrentLanguage?.TextEntries;
            if (entries != null && entries.TryGetValue(key, out var localized) && !string.IsNullOrEmpty(localized)) {
                return localized;
            }

            _logger?.LogInformation($"{nameof(ExternalMessageLocalizer)}: nepoznata poruka '{rawMessage}' (ključ '{key}'), prikazuje se original.");
            return rawMessage;
        }

        /// <summary>Trim + sažimanje uzastopnih belina u jedan razmak + velika slova (invariant).</summary>
        internal static string Normalize(string message)
            => _whitespace.Replace(message.Trim(), " ").ToUpperInvariant();
    }
}
