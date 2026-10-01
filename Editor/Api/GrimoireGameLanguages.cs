using System;
using System.Collections.Generic;
using UnityEditor;

namespace Grimoire.PluginV2.Editor
{
    /// <summary>
    /// The supported languages of the selected game (Grimoire game settings),
    /// read from <c>GET /api/v1/games</c>.
    ///
    /// Cached per game id so language dropdowns can be drawn from OnGUI without
    /// a request per repaint. A failed fetch is not retried automatically;
    /// <see cref="Refresh"/> forces one.
    /// </summary>
    [InitializeOnLoad]
    public static class GrimoireGameLanguages
    {
        /// <summary>Empty code: show source text rather than a translation.</summary>
        public const string SourceLanguage = "";

        private static string[] _codes = Array.Empty<string>();
        private static string _defaultLanguage = "";
        private static string _loadedGameId = "";
        private static string _attemptedGameId;
        private static bool _loading;
        private static string _error;
        private static int _generation;

        static GrimoireGameLanguages()
        {
            // Switching game (or signing out) invalidates the cached list.
            GrimoireSettings.Changed += Invalidate;
        }

        /// <summary>Raised when the cached list, loading state, or error changes.</summary>
        public static event Action Changed;

        /// <summary>
        /// Supported language codes, source language first. Empty until loaded.
        /// </summary>
        public static IReadOnlyList<string> Codes => _codes;

        /// <summary>The game's source language code; empty when unset.</summary>
        public static string DefaultLanguage => _defaultLanguage;

        public static bool IsLoading => _loading;

        public static string Error => _error;

        public static bool IsLoaded =>
            !string.IsNullOrEmpty(_loadedGameId) && _loadedGameId == GrimoireSettings.GameId;

        /// <summary>True when the game has at least one language to choose from.</summary>
        public static bool HasLanguages => IsLoaded && _codes.Length > 0;

        /// <summary>
        /// Start a fetch when the cache is empty or belongs to another game.
        /// Safe to call every repaint.
        /// </summary>
        public static void EnsureLoaded()
        {
            if (_loading || !GrimoireSettings.IsConfigured)
            {
                return;
            }

            var gameId = GrimoireSettings.GameId;
            if (_attemptedGameId == gameId)
            {
                return;
            }

            LoadAsync(gameId);
        }

        /// <summary>Re-fetch, including after a failure.</summary>
        public static void Refresh()
        {
            _attemptedGameId = null;
            EnsureLoaded();
        }

        public static void Invalidate()
        {
            if (_loadedGameId == GrimoireSettings.GameId)
            {
                return;
            }

            _generation++;
            _codes = Array.Empty<string>();
            _defaultLanguage = "";
            _loadedGameId = "";
            _attemptedGameId = null;
            _loading = false;
            _error = null;
            Changed?.Invoke();
        }

        public static bool Supports(string languageCode)
        {
            if (string.IsNullOrWhiteSpace(languageCode))
            {
                return false;
            }

            var code = languageCode.Trim();
            for (var i = 0; i < _codes.Length; i++)
            {
                if (string.Equals(_codes[i], code, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Human label for a code, e.g. <c>"German (de)"</c>. Unknown codes show
        /// as-is. Reads well inside a sentence.
        /// </summary>
        public static string DisplayName(string languageCode)
        {
            if (string.IsNullOrWhiteSpace(languageCode))
            {
                return "";
            }

            var code = languageCode.Trim();
            var name = GrimoireLanguageNames.Lookup(code);
            return string.IsNullOrEmpty(name) ? code : $"{name} ({code})";
        }

        /// <summary>
        /// Label for choosing no translation. Names the game's source language
        /// when it is known, since that is what an untranslated link shows.
        /// </summary>
        public static string SourceLanguageLabel =>
            string.IsNullOrEmpty(_defaultLanguage)
                ? "Source text (no translation)"
                : $"{DisplayName(_defaultLanguage)}  ·  source text";

        /// <summary>True for the game's source language, which has no translation rows.</summary>
        public static bool IsDefault(string languageCode) =>
            !string.IsNullOrWhiteSpace(_defaultLanguage) &&
            string.Equals(languageCode?.Trim(), _defaultLanguage, StringComparison.OrdinalIgnoreCase);

        private static async void LoadAsync(string gameId)
        {
            var generation = ++_generation;
            _loading = true;
            _attemptedGameId = gameId;
            _error = null;
            Changed?.Invoke();

            var hasSession = await GrimoireAuthSession.EnsureFreshTokenAsync();
            if (generation != _generation)
            {
                return;
            }

            if (!hasSession)
            {
                Finish(generation, "Your session expired. Sign in again.");
                return;
            }

            var result = await GrimoireApiClient.ListGamesAsync(gameId: gameId);
            if (generation != _generation)
            {
                return;
            }

            if (!result.Success)
            {
                Finish(generation, result.Error);
                return;
            }

            GameDirectoryEntry entry = null;
            var games = result.Data ?? Array.Empty<GameDirectoryEntry>();
            for (var i = 0; i < games.Length; i++)
            {
                if (games[i] != null && games[i].id == gameId)
                {
                    entry = games[i];
                    break;
                }
            }

            if (entry == null)
            {
                Finish(generation, "The selected game is no longer available on this account.");
                return;
            }

            _defaultLanguage = entry.default_language?.Trim() ?? "";
            _codes = Order(entry.languages, _defaultLanguage);
            _loadedGameId = gameId;
            Finish(generation, null);
        }

        /// <summary>Source language first, the rest alphabetically by label.</summary>
        private static string[] Order(string[] languages, string defaultLanguage)
        {
            var ordered = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrEmpty(defaultLanguage))
            {
                ordered.Add(defaultLanguage);
                seen.Add(defaultLanguage);
            }

            var rest = new List<string>();
            if (languages != null)
            {
                for (var i = 0; i < languages.Length; i++)
                {
                    var code = languages[i]?.Trim();
                    if (!string.IsNullOrEmpty(code) && seen.Add(code))
                    {
                        rest.Add(code);
                    }
                }
            }

            rest.Sort((left, right) => string.Compare(
                GrimoireLanguageNames.Lookup(left) ?? left,
                GrimoireLanguageNames.Lookup(right) ?? right,
                StringComparison.OrdinalIgnoreCase));

            ordered.AddRange(rest);
            return ordered.ToArray();
        }

        private static void Finish(int generation, string error)
        {
            if (generation != _generation)
            {
                return;
            }

            _loading = false;
            _error = error;
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Language code to English display name, mirroring the name table the
    /// Grimoire platform shows for game languages. Regional codes fall back to
    /// their base language so <c>pt-BR</c> still reads as Portuguese.
    /// </summary>
    public static class GrimoireLanguageNames
    {
        private static readonly Dictionary<string, string> Names =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["af"] = "Afrikaans",
                ["ar"] = "Arabic",
                ["be"] = "Belarusian",
                ["bg"] = "Bulgarian",
                ["bn"] = "Bengali",
                ["ca"] = "Catalan",
                ["cs"] = "Czech",
                ["da"] = "Danish",
                ["de"] = "German",
                ["el"] = "Greek",
                ["en"] = "English",
                ["en-GB"] = "English (UK)",
                ["en-US"] = "English (US)",
                ["es"] = "Spanish",
                ["es-419"] = "Spanish (Latin America)",
                ["et"] = "Estonian",
                ["eu"] = "Basque",
                ["fa"] = "Persian",
                ["fi"] = "Finnish",
                ["fil"] = "Filipino",
                ["fo"] = "Faroese",
                ["fr"] = "French",
                ["fr-CA"] = "French (Canada)",
                ["ga"] = "Irish",
                ["gl"] = "Galician",
                ["he"] = "Hebrew",
                ["hi"] = "Hindi",
                ["hr"] = "Croatian",
                ["hu"] = "Hungarian",
                ["id"] = "Indonesian",
                ["is"] = "Icelandic",
                ["it"] = "Italian",
                ["ja"] = "Japanese",
                ["ka"] = "Georgian",
                ["kk"] = "Kazakh",
                ["km"] = "Khmer",
                ["ko"] = "Korean",
                ["lt"] = "Lithuanian",
                ["lv"] = "Latvian",
                ["mk"] = "Macedonian",
                ["ms"] = "Malay",
                ["mt"] = "Maltese",
                ["nb"] = "Norwegian Bokmal",
                ["nl"] = "Dutch",
                ["no"] = "Norwegian",
                ["pl"] = "Polish",
                ["pt"] = "Portuguese",
                ["pt-BR"] = "Portuguese (Brazil)",
                ["pt-PT"] = "Portuguese (Portugal)",
                ["ro"] = "Romanian",
                ["ru"] = "Russian",
                ["sk"] = "Slovak",
                ["sl"] = "Slovenian",
                ["sq"] = "Albanian",
                ["sr"] = "Serbian",
                ["sv"] = "Swedish",
                ["sw"] = "Swahili",
                ["ta"] = "Tamil",
                ["th"] = "Thai",
                ["tr"] = "Turkish",
                ["uk"] = "Ukrainian",
                ["ur"] = "Urdu",
                ["vi"] = "Vietnamese",
                ["zh"] = "Chinese",
                ["zh-CN"] = "Chinese (Simplified)",
                ["zh-TW"] = "Chinese (Traditional)",
            };

        /// <summary>Display name for a code, or null when unknown.</summary>
        public static string Lookup(string languageCode)
        {
            if (string.IsNullOrWhiteSpace(languageCode))
            {
                return null;
            }

            var code = languageCode.Trim();
            if (Names.TryGetValue(code, out var name))
            {
                return name;
            }

            var separator = code.IndexOfAny(new[] { '-', '_' });
            if (separator > 0 && Names.TryGetValue(code.Substring(0, separator), out var baseName))
            {
                return baseName;
            }

            return null;
        }
    }
}
