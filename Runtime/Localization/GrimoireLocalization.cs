using System;
using System.Collections.Generic;
using UnityEngine;

namespace Grimoire.PluginV2
{
    /// <summary>
    /// Shared language source for all plugin-owned localized data. Editor code
    /// supplies the Connect locale; builds use the language set by
    /// <see cref="GrimoireBootstrap"/>.
    /// </summary>
    public static class GrimoireLocalization
    {
        private static string _runtimeLanguage = "";

        public static event Action LanguageChanged;

#if UNITY_EDITOR
        public static Func<string> EditorLanguageResolver;
#endif

        public static string ActiveLanguage => ResolveLanguage(null);

        public static string ResolveLanguage(string languageOverride)
        {
            if (!string.IsNullOrWhiteSpace(languageOverride))
            {
                return languageOverride.Trim();
            }

#if UNITY_EDITOR
            if (Application.isPlaying && !string.IsNullOrEmpty(_runtimeLanguage))
            {
                return _runtimeLanguage;
            }

            if (EditorLanguageResolver != null)
            {
                return Normalize(EditorLanguageResolver.Invoke());
            }
#endif

            return _runtimeLanguage;
        }

        public static void SetRuntimeLanguage(string languageCode)
        {
            var next = Normalize(languageCode);
            if (string.Equals(_runtimeLanguage, next, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _runtimeLanguage = next;
            LanguageChanged?.Invoke();
        }

        /// <summary>Notify listeners after an external language source changed.</summary>
        public static void NotifyLanguageChanged() => LanguageChanged?.Invoke();

        public static string Resolve(
            string sourceText,
            IReadOnlyList<GrimoireLocalizedText> translations,
            string languageCode = null)
        {
            var language = ResolveLanguage(languageCode);
            if (!string.IsNullOrEmpty(language) && translations != null)
            {
                for (var i = 0; i < translations.Count; i++)
                {
                    var row = translations[i];
                    if (row != null &&
                        string.Equals(row.LanguageCode, language, StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrEmpty(row.Text))
                    {
                        return row.Text;
                    }
                }
            }

            return sourceText ?? "";
        }

        private static string Normalize(string languageCode) =>
            string.IsNullOrWhiteSpace(languageCode) ? "" : languageCode.Trim();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            _runtimeLanguage = "";
        }
    }

    /// <summary>A serializable localized text row used by non-dialog runtime data.</summary>
    [Serializable]
    public class GrimoireLocalizedText
    {
        [SerializeField] private string _languageCode = "";
        [SerializeField] private string _text = "";

        public GrimoireLocalizedText()
        {
        }

        public GrimoireLocalizedText(string languageCode, string text)
        {
            _languageCode = languageCode ?? "";
            _text = text ?? "";
        }

        public string LanguageCode
        {
            get => _languageCode;
            set => _languageCode = value ?? "";
        }

        public string Text
        {
            get => _text;
            set => _text = value ?? "";
        }
    }
}
