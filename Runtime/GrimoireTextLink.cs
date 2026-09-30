using System;
using System.Collections.Generic;
using UnityEngine;

namespace Grimoire.PluginV2
{
    /// <summary>
    /// Links a GameObject to a Grimoire source string through its code (abbrev or id).
    /// Resolved copy for the active locale is cached on this component for builds;
    /// the editor assembly fetches and pushes translations via the Public API.
    /// </summary>
    [AddComponentMenu("Grimoire/Grimoire Text Link")]
    [DisallowMultipleComponent]
    public class GrimoireTextLink : MonoBehaviour
    {
        /// <summary>Editor preview stand-in for a locale that has no copy yet.</summary>
        public const string NotTranslatedPlaceholder = "not translated";

        [SerializeField]
        [Tooltip("Grimoire string code — typically the abbrev (e.g. WELCOME) or string UUID.")]
        private string _textCode = "";

        [SerializeField]
        [HideInInspector]
        private string _cachedStringId = "";

        [SerializeField]
        [Tooltip("When empty, uses the Connect locale in the editor and export language at runtime.")]
        private string _previewLanguage = "";

        [SerializeField]
        [Tooltip("Push resolved copy onto Unity UI Text / TextMesh Pro on this GameObject.")]
        private bool _applyToTarget = true;

        [SerializeField]
        private string _sourceText = "";

        [SerializeField]
        [HideInInspector]
        private string _grimoireSourceText = "";

        [SerializeField]
        [HideInInspector]
        private int _characterLimit;

        [SerializeField]
        [HideInInspector]
        private List<GrimoireLinkedTranslation> _translations = new List<GrimoireLinkedTranslation>();

        /// <summary>Raised when cached text changes so the editor can mark dirty state.</summary>
        public static event Action<GrimoireTextLink> TextChanged;

#if UNITY_EDITOR
        /// <summary>
        /// Supplies the Edit Mode preview locale. The editor assembly installs
        /// this so previews follow the scene language chosen in Grimoire
        /// Connect, including after a domain reload.
        /// </summary>
        public static Func<string> EditorLanguageResolver;
#endif

        /// <summary>The Grimoire string code (abbrev or UUID) for this link.</summary>
        public string TextCode
        {
            get => _textCode;
            set
            {
                var trimmed = value?.Trim() ?? "";
                if (trimmed == _textCode)
                {
                    return;
                }

                _textCode = trimmed;
                _cachedStringId = "";
                ClearCachedText();
            }
        }

        public string CachedStringId
        {
            get => _cachedStringId;
            set => _cachedStringId = value ?? "";
        }

        public string PreviewLanguage
        {
            get => _previewLanguage;
            set => _previewLanguage = value?.Trim() ?? "";
        }

        public bool ApplyToTarget
        {
            get => _applyToTarget;
            set => _applyToTarget = value;
        }

        public bool HasCode => !string.IsNullOrWhiteSpace(_textCode);

        public int CharacterLimit
        {
            get => _characterLimit;
            set => _characterLimit = Math.Max(0, value);
        }

        public string SourceText
        {
            get => _sourceText;
            set => _sourceText = value ?? "";
        }

        public string GrimoireSourceText
        {
            get => _grimoireSourceText;
            set => _grimoireSourceText = value ?? "";
        }

        public bool IsSourceDeviated =>
            !string.Equals(_sourceText ?? "", _grimoireSourceText ?? "", StringComparison.Ordinal);

        public IReadOnlyList<GrimoireLinkedTranslation> Translations => _translations;

        public bool HasTranslationDeviations
        {
            get
            {
                if (_translations == null)
                {
                    return false;
                }

                for (var i = 0; i < _translations.Count; i++)
                {
                    if (_translations[i] != null && _translations[i].IsDeviated)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        /// <summary>True when any pushable translation differs from Grimoire.</summary>
        public bool HasPushableDeviations => HasTranslationDeviations;

        public int TranslationDeviationCount
        {
            get
            {
                if (_translations == null)
                {
                    return 0;
                }

                var count = 0;
                for (var i = 0; i < _translations.Count; i++)
                {
                    if (_translations[i] != null && _translations[i].IsDeviated)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        /// <summary>Resolved copy for <paramref name="languageCode"/> (empty = source text).</summary>
        public string GetResolvedText(string languageCode = null)
        {
            var locale = string.IsNullOrWhiteSpace(languageCode) ? null : languageCode.Trim();
            if (string.IsNullOrEmpty(locale))
            {
                return _sourceText ?? "";
            }

            if (_translations != null)
            {
                for (var i = 0; i < _translations.Count; i++)
                {
                    var row = _translations[i];
                    if (row != null &&
                        string.Equals(row.LanguageCode, locale, StringComparison.OrdinalIgnoreCase))
                    {
                        return row.LocalText ?? "";
                    }
                }
            }

            return _sourceText ?? "";
        }

        /// <summary>
        /// Editor preview copy. Where <see cref="GetResolvedText"/> falls back to
        /// the source text, this returns <see cref="NotTranslatedPlaceholder"/> so
        /// an untranslated locale is visible instead of looking already localized.
        /// </summary>
        public string GetPreviewText(string languageCode = null)
        {
            var locale = string.IsNullOrWhiteSpace(languageCode) ? "" : languageCode.Trim();
            if (locale.Length == 0)
            {
                return _sourceText ?? "";
            }

            return HasTranslationFor(locale) ? GetResolvedText(locale) : NotTranslatedPlaceholder;
        }

        /// <summary>True when <paramref name="languageCode"/> has non-empty copy cached here.</summary>
        public bool HasTranslationFor(string languageCode)
        {
            var locale = string.IsNullOrWhiteSpace(languageCode) ? "" : languageCode.Trim();
            return TryGetTranslation(locale, out var row) && !string.IsNullOrEmpty(row.LocalText);
        }

        public bool TryGetTranslation(string languageCode, out GrimoireLinkedTranslation translation)
        {
            translation = null;
            if (string.IsNullOrEmpty(languageCode) || _translations == null)
            {
                return false;
            }

            for (var i = 0; i < _translations.Count; i++)
            {
                var row = _translations[i];
                if (row != null &&
                    string.Equals(row.LanguageCode, languageCode, StringComparison.OrdinalIgnoreCase))
                {
                    translation = row;
                    return true;
                }
            }

            return false;
        }

        public void ReplaceTranslations(List<GrimoireLinkedTranslation> translations)
        {
            _translations = translations ?? new List<GrimoireLinkedTranslation>();
        }

        public void ClearCachedText()
        {
            _sourceText = "";
            _grimoireSourceText = "";
            _characterLimit = 0;
            _translations?.Clear();
        }

        /// <summary>
        /// Apply <see cref="GetResolvedText"/> to UI components on this GameObject.
        /// Edit Mode previews use <see cref="GetPreviewText"/> instead, so a missing
        /// translation is visible while authoring without shipping the placeholder
        /// to players.
        /// </summary>
        public void ApplyResolvedText(string languageCode = null)
        {
            if (!_applyToTarget)
            {
                return;
            }

            var locale = languageCode;
            if (string.IsNullOrWhiteSpace(locale))
            {
                locale = ResolveActiveLanguage();
            }

#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                GrimoireTextTarget.TryApply(gameObject, GetPreviewText(locale));
                return;
            }
#endif

            GrimoireTextTarget.TryApply(gameObject, GetResolvedText(locale));
        }

        /// <summary>Preview override, Connect locale in editor, or export language at runtime.</summary>
        public string ResolveActiveLanguage()
        {
            if (!string.IsNullOrWhiteSpace(_previewLanguage))
            {
                return _previewLanguage.Trim();
            }

#if UNITY_EDITOR
            var editorLanguage = EditorLanguageResolver?.Invoke();
            return string.IsNullOrWhiteSpace(editorLanguage) ? "" : editorLanguage.Trim();
#else
            var tkType = GrimoireReflect.FindType("TranslationKey");
            if (tkType != null)
            {
                var prop = tkType.GetProperty("CurrentLanguage",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                var value = prop?.GetValue(null) as string;
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }

            return "";
#endif
        }

        public void NotifyTextChanged() => TextChanged?.Invoke(this);

        private void OnEnable()
        {
            if (_applyToTarget)
            {
                ApplyResolvedText();
            }
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (!_applyToTarget || !isActiveAndEnabled)
            {
                return;
            }

            ApplyResolvedText(_previewLanguage);
        }
#endif
    }
}
