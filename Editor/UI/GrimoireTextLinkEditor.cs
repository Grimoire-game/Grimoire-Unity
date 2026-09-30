using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Grimoire.PluginV2.Editor
{
    /// <summary>
    /// Inspector for <see cref="GrimoireTextLink"/>: resolve a string code,
    /// preview localized copy on the GameObject, edit translations, and push
    /// changes to Grimoire.
    /// </summary>
    [CustomEditor(typeof(GrimoireTextLink))]
    public class GrimoireTextLinkEditor : UnityEditor.Editor
    {
        private const float FieldLabelWidth = 120f;

        private string _validationMessage;
        private MessageType _validationType = MessageType.None;
        private bool _busy;
        private bool _textFoldout = true;
        private bool _translationsFoldout = true;

        private void OnEnable()
        {
            GrimoireTextLinkStore.Changed += OnStoreChanged;
        }

        private void OnDisable()
        {
            GrimoireTextLinkStore.Changed -= OnStoreChanged;
        }

        private void OnStoreChanged() => Repaint();

        public override void OnInspectorGUI()
        {
            var link = (GrimoireTextLink)target;

            EditorGUI.BeginChangeCheck();
            var code = EditorGUILayout.TextField(
                new GUIContent("String code", "Grimoire string abbrev (e.g. WELCOME), section/abbrev, or UUID."),
                link.TextCode);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(link, "Change Grimoire string code");
                link.TextCode = code;
                EditorUtility.SetDirty(link);
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();

            using (new EditorGUI.DisabledScope(_busy || Application.isPlaying))
            {
                if (GUILayout.Button("Pick from Grimoire..."))
                {
                    OpenPicker(link);
                }

                if (GUILayout.Button("Refresh from Grimoire"))
                {
                    Refresh(link, preserveLocalEdits: true);
                }
            }

            EditorGUILayout.EndHorizontal();

            DrawPreviewSection(link);
            DrawTextSection(link);
            DrawTranslationsSection(link);
            DrawActions(link);

            if (_busy)
            {
                EditorGUILayout.HelpBox("Talking to Grimoire…", MessageType.Info);
            }
            else if (!string.IsNullOrEmpty(_validationMessage))
            {
                EditorGUILayout.HelpBox(_validationMessage, _validationType);
            }
        }

        private void DrawPreviewSection(GrimoireTextLink link)
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Preview", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            link.ApplyToTarget = EditorGUILayout.Toggle(
                new GUIContent("Apply to UI text", "Write resolved copy to Unity UI Text or TextMesh Pro on this GameObject."),
                link.ApplyToTarget);
            var previewLanguage = GrimoireLanguagePopup.Draw(
                new GUIContent(
                    "Preview language",
                    "Override the scene language for this object only. Choose from the game's languages."),
                link.PreviewLanguage,
                "Follow scene language");
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(link, "Change Grimoire text preview");
                link.PreviewLanguage = previewLanguage;
                link.ApplyResolvedText(GrimoireTextLinkStore.ResolvePreviewLanguage(link));
                EditorUtility.SetDirty(link);
            }

            if (link.ApplyToTarget && !GrimoireTextTarget.HasSupportedTarget(link.gameObject))
            {
                EditorGUILayout.HelpBox(
                    "No Unity UI Text or TextMesh Pro component found on this GameObject.",
                    MessageType.Warning);
            }

            var locale = GrimoireTextLinkStore.ResolvePreviewLanguage(link);
            var resolved = link.GetPreviewText(locale);
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.TextField(
                    new GUIContent(
                        "Shown on object",
                        string.IsNullOrEmpty(locale) ? "Source text" : $"Locale: {locale}"),
                    resolved);
            }

            var linked = link.HasCode || !string.IsNullOrEmpty(link.CachedStringId);
            if (linked && !string.IsNullOrEmpty(locale) && !link.HasTranslationFor(locale))
            {
                EditorGUILayout.HelpBox(
                    $"No '{locale}' copy on this string yet. Players fall back to the source text.",
                    MessageType.Info);
            }
        }

        private void DrawTextSection(GrimoireTextLink link)
        {
            if (!link.HasCode && string.IsNullOrEmpty(link.CachedStringId))
            {
                return;
            }

            EditorGUILayout.Space(6);
            _textFoldout = EditorGUILayout.Foldout(
                _textFoldout, "Source text", true, EditorStyles.foldoutHeader);
            if (!_textFoldout)
            {
                return;
            }

            var label = link.IsSourceDeviated ? "Source text •" : "Source text";
            EditorGUILayout.LabelField(label, EditorStyles.miniBoldLabel);

            EditorGUI.BeginChangeCheck();
            var next = EditorGUILayout.TextArea(link.SourceText ?? "", GUILayout.MinHeight(48));
            if (EditorGUI.EndChangeCheck())
            {
                GrimoireTextLinkStore.SetSourceText(link, next);
            }

            if (link.IsSourceDeviated)
            {
                EditorGUILayout.HelpBox(
                    "Source text changes are local only. Edit source copy in Grimoire, or push translation rows below.",
                    MessageType.Info);
            }
        }

        private void DrawTranslationsSection(GrimoireTextLink link)
        {
            if (link.Translations == null || link.Translations.Count == 0)
            {
                if (link.HasCode || !string.IsNullOrEmpty(link.CachedStringId))
                {
                    EditorGUILayout.Space(4);
                    EditorGUILayout.LabelField(
                        "No translations loaded yet. Click Refresh from Grimoire.",
                        EditorStyles.miniLabel);
                }

                return;
            }

            EditorGUILayout.Space(6);
            _translationsFoldout = EditorGUILayout.Foldout(
                _translationsFoldout,
                link.HasTranslationDeviations
                    ? $"Translations ({link.TranslationDeviationCount} changed)"
                    : "Translations",
                true,
                EditorStyles.foldoutHeader);
            if (!_translationsFoldout)
            {
                return;
            }

            foreach (var row in link.Translations)
            {
                if (row == null || string.IsNullOrEmpty(row.LanguageCode))
                {
                    continue;
                }

                DrawTranslationRow(link, row);
            }
        }

        private static void DrawTranslationRow(GrimoireTextLink link, GrimoireLinkedTranslation row)
        {
            EditorGUILayout.BeginHorizontal();
            var label = row.IsDeviated ? $"{row.LanguageCode} •" : row.LanguageCode;
            EditorGUILayout.LabelField(label, GUILayout.Width(FieldLabelWidth));

            EditorGUILayout.BeginVertical();
            EditorGUI.BeginChangeCheck();
            var next = EditorGUILayout.TextArea(row.LocalText ?? "", GUILayout.MinHeight(36));
            if (EditorGUI.EndChangeCheck())
            {
                GrimoireTextLinkStore.SetTranslationText(link, row.LanguageCode, next);
            }

            if (row.IsDeviated)
            {
                var preview = string.IsNullOrEmpty(row.GrimoireText) ? "(empty)" : row.GrimoireText;
                if (preview.Length > 60)
                {
                    preview = preview.Substring(0, 57) + "…";
                }

                EditorGUILayout.LabelField($"Grimoire: {preview}", EditorStyles.miniLabel);
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(2);
        }

        private void DrawActions(GrimoireTextLink link)
        {
            if (!GrimoireTextLinkStore.HasDeviations(link))
            {
                return;
            }

            EditorGUILayout.Space(6);
            EditorGUILayout.BeginHorizontal();

            using (new EditorGUI.DisabledScope(_busy || Application.isPlaying || !GrimoireTextLinkStore.HasPushableDeviations(link)))
            {
                if (GUILayout.Button(
                        new GUIContent(
                            "Push translations to Grimoire",
                            "PATCH deviated translation rows via the Public API."),
                        GrimoireEditorStyles.PrimaryButtonStyle))
                {
                    PushAsync(link);
                }
            }

            using (new EditorGUI.DisabledScope(_busy || Application.isPlaying))
            {
                if (GUILayout.Button("Reset to Grimoire"))
                {
                    if (GrimoireTextLinkStore.ResetToGrimoire(link))
                    {
                        _validationMessage = "Restored text from the last Grimoire baseline.";
                        _validationType = MessageType.Info;
                    }
                }
            }

            EditorGUILayout.EndHorizontal();
        }

        private void OpenPicker(GrimoireTextLink link)
        {
            if (!GrimoireSettings.IsConfigured)
            {
                _validationMessage = "Sign in and choose a workspace first (Window > Grimoire > Grimoire Connect).";
                _validationType = MessageType.Warning;
                return;
            }

            GrimoireStringPickerWindow.Open(resource =>
            {
                Undo.RecordObject(link, "Pick Grimoire string");
                link.TextCode = GrimoireStringPickerWindow.DisplayCode(resource);
                link.CachedStringId = resource.id;
                GrimoireStringKeyResolver.Remember(GrimoireSettings.GameId, resource);
                link.ClearCachedText();
                EditorUtility.SetDirty(link);
                Refresh(link, preserveLocalEdits: false);
            });
        }

        private async void Refresh(GrimoireTextLink link, bool preserveLocalEdits)
        {
            if (_busy || link == null)
            {
                return;
            }

            _busy = true;
            _validationMessage = null;
            Repaint();

            var result = await GrimoireTextLinkStore.RefreshFromGrimoireAsync(link, preserveLocalEdits);

            _busy = false;
            if (!result.Success)
            {
                _validationMessage = result.Error;
                _validationType = MessageType.Error;
            }
            else
            {
                _validationMessage = preserveLocalEdits
                    ? "Refreshed from Grimoire (kept local edits)."
                    : "Refreshed from Grimoire.";
                _validationType = MessageType.Info;
            }

            Repaint();
        }

        private async void PushAsync(GrimoireTextLink link)
        {
            if (_busy || link == null)
            {
                return;
            }

            _busy = true;
            _validationMessage = null;
            Repaint();

            var result = await GrimoireTextSync.PushLinksAsync(new[] { link });

            _busy = false;
            if (!result.Success)
            {
                _validationMessage = result.Error;
                _validationType = MessageType.Error;
            }
            else
            {
                _validationMessage = result.Data == 1
                    ? "Pushed 1 translation to Grimoire."
                    : $"Pushed {result.Data} translations to Grimoire.";
                _validationType = MessageType.Info;
            }

            Repaint();
        }
    }

    /// <summary>
    /// Searchable string picker backed by GET /api/v1/strings.
    /// </summary>
    public class GrimoireStringPickerWindow : EditorWindow
    {
        private Action<StringResource> _onPicked;
        private StringResource[] _results = Array.Empty<StringResource>();
        private StringResource[] _filtered = Array.Empty<StringResource>();
        private string _search = "";
        private bool _loading;
        private string _error;
        private Vector2 _scroll;

        public static void Open(Action<StringResource> onPicked)
        {
            var window = GetWindow<GrimoireStringPickerWindow>(true, "Pick Grimoire String", true);
            window.minSize = new Vector2(420, 320);
            window._onPicked = onPicked;
            window.Fetch();
        }

        public static string DisplayCode(StringResource resource)
        {
            if (resource == null)
            {
                return "";
            }

            if (!string.IsNullOrEmpty(resource.abbrev))
            {
                return !string.IsNullOrEmpty(resource.section)
                    ? $"{resource.section}/{resource.abbrev}"
                    : resource.abbrev;
            }

            return resource.id ?? "";
        }

        private void OnGUI()
        {
            EditorGUI.BeginChangeCheck();
            _search = EditorGUILayout.TextField(_search, EditorStyles.toolbarSearchField);
            if (EditorGUI.EndChangeCheck())
            {
                ApplyFilter();
            }

            if (_loading)
            {
                EditorGUILayout.LabelField("Loading...", EditorStyles.centeredGreyMiniLabel);
            }

            if (!string.IsNullOrEmpty(_error))
            {
                EditorGUILayout.HelpBox(_error, MessageType.Error);
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var row in _filtered)
            {
                DrawRow(row);
            }

            if (!_loading && _filtered.Length == 0 && string.IsNullOrEmpty(_error))
            {
                EditorGUILayout.LabelField("No strings found.", EditorStyles.centeredGreyMiniLabel);
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawRow(StringResource row)
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
            EditorGUILayout.BeginVertical();

            var title = string.IsNullOrEmpty(row.source_text)
                ? "(empty source text)"
                : row.source_text;
            if (title.Length > 80)
            {
                title = title.Substring(0, 77) + "…";
            }

            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
            EditorGUILayout.LabelField(DisplayCode(row), EditorStyles.miniLabel);
            EditorGUILayout.EndVertical();

            if (GUILayout.Button("Select", GUILayout.Width(60), GUILayout.Height(28)))
            {
                _onPicked?.Invoke(row);
                Close();
            }

            EditorGUILayout.EndHorizontal();
        }

        private async void Fetch()
        {
            if (!GrimoireSettings.IsConfigured)
            {
                _error = "Sign in and choose a workspace first (Window > Grimoire > Grimoire Connect).";
                Repaint();
                return;
            }

            _loading = true;
            _error = null;
            Repaint();

            await GrimoireAuthSession.EnsureFreshTokenAsync();
            var result = await GrimoireApiClient.ListStringsAsync(
                GrimoireSettings.GameId, includeTranslations: false, includeContext: false);

            _loading = false;
            if (!result.Success)
            {
                _error = result.Error;
                Repaint();
                return;
            }

            _results = result.Data ?? Array.Empty<StringResource>();
            foreach (var row in _results)
            {
                GrimoireStringKeyResolver.Remember(GrimoireSettings.GameId, row);
            }

            ApplyFilter();
            Repaint();
        }

        private void ApplyFilter()
        {
            if (string.IsNullOrWhiteSpace(_search))
            {
                _filtered = _results;
                return;
            }

            var query = _search.Trim();
            var list = new List<StringResource>();
            foreach (var row in _results)
            {
                if (row == null)
                {
                    continue;
                }

                if (Contains(row.source_text, query) ||
                    Contains(row.abbrev, query) ||
                    Contains(row.section, query) ||
                    Contains(row.id, query) ||
                    Contains(DisplayCode(row), query))
                {
                    list.Add(row);
                }
            }

            _filtered = list.ToArray();
        }

        private static bool Contains(string value, string query) =>
            !string.IsNullOrEmpty(value) &&
            value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
