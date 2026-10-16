using System;
using System.Collections.Generic;

namespace Grimoire.PluginV2.Internal
{
    /// <summary>
    /// Picks translated dialog copy. Uses the same language source as
    /// <see cref="GrimoireTextLink"/>: an explicit override, then the Connect
    /// language in the editor, then the export TranslationKey.CurrentLanguage.
    /// </summary>
    public static class GrimoireDialogLocalization
    {
        public static string ResolveLanguage(string languageOverride)
        {
            return GrimoireLocalization.ResolveLanguage(languageOverride);
        }

        /// <summary>Translated text, falling back to the source text when missing.</summary>
        public static string Text(string sourceText, List<GrimoireDialogTranslation> translations, string language)
        {
            var row = Find(translations, language);
            return row != null && !string.IsNullOrEmpty(row.text) ? row.text : sourceText ?? "";
        }

        public static string Voice(string sourceVoiceUrl, List<GrimoireDialogTranslation> translations, string language)
        {
            var row = Find(translations, language);
            return row != null && !string.IsNullOrEmpty(row.voiceUrl) ? row.voiceUrl : sourceVoiceUrl ?? "";
        }

        private static GrimoireDialogTranslation Find(List<GrimoireDialogTranslation> translations, string language)
        {
            if (string.IsNullOrEmpty(language) || translations == null)
            {
                return null;
            }

            foreach (var row in translations)
            {
                if (row != null && string.Equals(row.languageCode, language, StringComparison.OrdinalIgnoreCase))
                {
                    return row;
                }
            }

            return null;
        }
    }
}
