using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Grimoire.PluginV2.Editor
{
    /// <summary>Browse the selected game's translated strings and copy runtime lookup keys.</summary>
    internal sealed class GrimoireStringsView
    {
        private const string StylePath = "Packages/com.grimoire.plugin/Editor/UI/GrimoireStrings.uss";
        private const string Unclassified = "Unclassified";

        private readonly List<StringResource> _rows = new List<StringResource>();
        private readonly List<StringResource> _visible = new List<StringResource>();
        private readonly Dictionary<string, List<string>> _typesById =
            new Dictionary<string, List<string>>(StringComparer.Ordinal);
        private readonly HashSet<string> _selectedTypes =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _availableTypes =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _duplicateCodes =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private VisualElement _root;
        private ListView _list;
        private VisualElement _detail;
        private Label _summary;
        private Label _resultHeading;
        private Label _noticeLabel;
        private Button _filters;
        private Button _copyShown;
        private string _search = "";
        private string _selectedId;
        private string _notice;
        private string _error;
        private bool _loading;
        private bool _active;
        private int _generation;

        internal void Activate()
        {
            if (_active) return;
            _active = true;
            GrimoireSettings.Changed += Refresh;
            GrimoireSettings.LocaleChanged += Render;
            GrimoireGameLanguages.Changed += RenderDetail;
            GrimoireContentSync.Completed += Refresh;
            if (_root != null) Refresh();
        }

        internal void Deactivate()
        {
            if (!_active) return;
            _active = false;
            _generation++;
            GrimoireSettings.Changed -= Refresh;
            GrimoireSettings.LocaleChanged -= Render;
            GrimoireGameLanguages.Changed -= RenderDetail;
            GrimoireContentSync.Completed -= Refresh;
        }

        internal void Build(VisualElement host)
        {
            _root = host;
            _root.Clear();
            _root.AddToClassList("grimoire-strings");
            _root.EnableInClassList("light", !EditorGUIUtility.isProSkin);
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(StylePath);
            if (sheet == null)
            {
                var guids = AssetDatabase.FindAssets("GrimoireStrings t:StyleSheet");
                if (guids.Length > 0)
                    sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(AssetDatabase.GUIDToAssetPath(guids[0]));
            }
            if (sheet != null) _root.styleSheets.Add(sheet);

            var header = Column("strings-header");
            header.Add(Label("Strings", "strings-title"));
            header.Add(Label("Game strings, Loca keys, and translations in one place.", "strings-muted"));
            _root.Add(header);

            var summaryRow = Row("strings-summary");
            _summary = Label("", "strings-summary-text");
            summaryRow.Add(_summary);
            var refresh = new Button(Refresh) { text = "Refresh strings" };
            summaryRow.Add(refresh);
            _root.Add(summaryRow);

            var toolbar = Row("strings-toolbar");
            var search = new TextField("Search");
            search.AddToClassList("strings-search");
            search.RegisterValueChangedCallback(evt =>
            {
                _search = evt.newValue ?? "";
                Render();
            });
            toolbar.Add(search);
            _filters = new Button(ShowFilters) { text = "Types ▾" };
            _filters.AddToClassList("strings-filters");
            toolbar.Add(_filters);
            _copyShown = new Button(CopyShownKeys) { text = "Copy shown keys" };
            toolbar.Add(_copyShown);
            _root.Add(toolbar);

            _noticeLabel = Label("", "strings-notice");
            _root.Add(_noticeLabel);

            var panes = Row("strings-panes");
            var listPane = Column("strings-list-pane");
            _resultHeading = Label("STRINGS", "strings-pane-heading");
            listPane.Add(_resultHeading);
            _list = new ListView(_visible, 64, MakeRow, BindRow)
            {
                selectionType = SelectionType.Single,
            };
            _list.AddToClassList("strings-list");
            _list.onSelectionChange += selection =>
            {
                _selectedId = selection.OfType<StringResource>().FirstOrDefault()?.id;
                RenderDetail();
            };
            listPane.Add(_list);
            panes.Add(listPane);

            var detailPane = Column("strings-detail-pane");
            detailPane.Add(Label("STRING DETAILS", "strings-pane-heading"));
            var scroll = new ScrollView();
            scroll.AddToClassList("strings-detail-scroll");
            _detail = Column("strings-detail-content");
            scroll.Add(_detail);
            detailPane.Add(scroll);
            panes.Add(detailPane);
            _root.Add(panes);

            _root.RegisterCallback<GeometryChangedEvent>(evt =>
            {
                _root.EnableInClassList("compact", evt.newRect.width < 750f);
            });
            if (_active) Refresh();
            else Render();
        }

        internal async void Refresh()
        {
            if (!_active || _root == null) return;
            var generation = ++_generation;
            var gameId = GrimoireSettings.GameId;
            _loading = true;
            _error = null;
            _notice = null;
            Render();
            if (!GrimoireSettings.IsConfigured)
            {
                _loading = false;
                _error = "Sign in and select a game to browse strings.";
                Render();
                return;
            }

            try
            {
                if (!await GrimoireAuthSession.EnsureFreshTokenAsync())
                {
                    if (IsCurrent(generation, gameId))
                    {
                        _error = "Your session expired. Sign in again.";
                        _loading = false;
                        Render();
                    }
                    return;
                }

                var result = await GrimoireApiClient.ListTaggedStringsAsync(gameId);
                var tagged = result.Success;
                if (!tagged)
                {
                    result = await GrimoireApiClient.ListStringsAsync(gameId);
                    if (result.Success)
                        _notice = "String Tags are unavailable; types are inferred from Loca keys.";
                }
                if (!IsCurrent(generation, gameId)) return;
                if (!result.Success)
                {
                    _error = result.Error ?? "Could not load game strings.";
                    _loading = false;
                    Render();
                    return;
                }

                var tags = tagged ? await GrimoireApiClient.ListStringTagsAsync(gameId) :
                    ApiResult<StringTagResource[]>.Fail("String Tags unavailable");
                if (!IsCurrent(generation, gameId)) return;
                if (!tags.Success && tagged)
                    _notice = "String Tag names are unavailable; types are inferred from Loca keys.";

                _rows.Clear();
                _rows.AddRange((result.Data ?? Array.Empty<StringResource>())
                    .Where(row => row != null && !string.IsNullOrEmpty(row.id))
                    .GroupBy(row => row.id, StringComparer.Ordinal).Select(group => group.First())
                    .OrderBy(row => GrimoireStringPickerWindow.DisplayCode(row), StringComparer.OrdinalIgnoreCase));
                BuildTypes(tags.Success ? tags.Data : null);
                BuildDuplicateCodes();
                _loading = false;
                GrimoireGameLanguages.EnsureLoaded();
                Render();
            }
            catch (Exception exception)
            {
                if (!IsCurrent(generation, gameId)) return;
                _error = exception.Message;
                _loading = false;
                Render();
            }
        }

        private bool IsCurrent(int generation, string gameId) =>
            _active && generation == _generation && gameId == GrimoireSettings.GameId;

        private void BuildTypes(StringTagResource[] tags)
        {
            _typesById.Clear();
            _availableTypes.Clear();
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var tag in tags ?? Array.Empty<StringTagResource>())
                if (tag != null && !string.IsNullOrEmpty(tag.id) &&
                    string.Equals(tag.group_name, "String Tags", StringComparison.OrdinalIgnoreCase))
                {
                    names[tag.id] = tag.name;
                    if (!string.IsNullOrWhiteSpace(tag.name)) _availableTypes.Add(tag.name);
                }

            foreach (var row in _rows)
            {
                var types = (row.tags ?? Array.Empty<string>())
                    .Where(names.ContainsKey).Select(id => names[id])
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (types.Count == 0) types.Add(InferredType(row));
                _typesById[row.id] = types;
                foreach (var type in types) _availableTypes.Add(type);
            }

            _selectedTypes.RemoveWhere(type => !_availableTypes.Contains(type));
        }

        private static string InferredType(StringResource row)
        {
            var key = row?.abbrev ?? "";
            if (key.StartsWith("UI_", StringComparison.OrdinalIgnoreCase)) return "UI";
            if (key.StartsWith("DIA_", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("DLG_", StringComparison.OrdinalIgnoreCase)) return "Dialog";
            if (key.StartsWith("OBJ_", StringComparison.OrdinalIgnoreCase)) return "Object";
            if (key.StartsWith("VAR_", StringComparison.OrdinalIgnoreCase)) return "Variable";
            return Unclassified;
        }

        private void BuildDuplicateCodes()
        {
            _duplicateCodes.Clear();
            foreach (var group in _rows.GroupBy(GrimoireStringPickerWindow.DisplayCode,
                StringComparer.OrdinalIgnoreCase))
                if (!string.IsNullOrEmpty(group.Key) && group.Count() > 1)
                    _duplicateCodes.Add(group.Key);
        }

        private void ShowFilters()
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("All types"), _selectedTypes.Count == 0, () =>
            {
                _selectedTypes.Clear();
                Render();
            });
            menu.AddSeparator("");
            foreach (var type in _availableTypes.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            {
                var selectedType = type;
                menu.AddItem(new GUIContent(selectedType), _selectedTypes.Contains(selectedType), () =>
                {
                    if (!_selectedTypes.Add(selectedType)) _selectedTypes.Remove(selectedType);
                    Render();
                });
            }
            menu.ShowAsContext();
        }

        private void Render()
        {
            if (_root == null || _list == null) return;
            _visible.Clear();
            _visible.AddRange(_rows.Where(Matches));
            _list.Rebuild();
            var index = _visible.FindIndex(row => row.id == _selectedId);
            if (index >= 0) _list.SetSelectionWithoutNotify(new[] { index });
            else _list.ClearSelection();
            _resultHeading.text = $"STRINGS · {_visible.Count} SHOWN";
            _summary.text = _loading ? "Loading game strings and translations…" :
                $"{_rows.Count} strings · {_availableTypes.Count} types";
            _filters.text = _selectedTypes.Count == 0 ? "Types ▾" : $"Types ({_selectedTypes.Count}) ▾";
            _filters.EnableInClassList("selected", _selectedTypes.Count > 0);
            _copyShown.SetEnabled(_visible.Count > 0);
            UpdateNotice();
            RenderDetail();
        }

        private void UpdateNotice()
        {
            if (_noticeLabel == null) return;
            _noticeLabel.text = _error ?? _notice ?? "";
            _noticeLabel.style.display = string.IsNullOrEmpty(_noticeLabel.text) ?
                DisplayStyle.None : DisplayStyle.Flex;
        }

        private bool Matches(StringResource row)
        {
            if (_selectedTypes.Count > 0 && !_typesById[row.id].Any(_selectedTypes.Contains)) return false;
            if (string.IsNullOrWhiteSpace(_search)) return true;
            var query = _search.Trim();
            return Contains(row.source_text, query) || Contains(row.abbrev, query) ||
                Contains(row.section, query) || Contains(row.context, query) ||
                Contains(row.id, query) || Contains(GrimoireStringPickerWindow.DisplayCode(row), query) ||
                (row.translations ?? Array.Empty<StringTranslation>()).Any(translation =>
                    translation != null && Contains(translation.translated_text, query));
        }

        private static bool Contains(string value, string query) =>
            !string.IsNullOrEmpty(value) && value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

        private static VisualElement MakeRow()
        {
            var row = Row("strings-item");
            var main = Column("strings-item-main");
            main.Add(new Label { name = "key" });
            main.Add(new Label { name = "preview" });
            row.Add(main);
            var side = Column("strings-item-side");
            side.Add(new Label { name = "type" });
            side.Add(new Label { name = "coverage" });
            row.Add(side);
            return row;
        }

        private void BindRow(VisualElement element, int index)
        {
            if (index < 0 || index >= _visible.Count) return;
            var row = _visible[index];
            element.EnableInClassList("selected", row.id == _selectedId);
            element.Q<Label>("key").text = GrimoireStringPickerWindow.DisplayCode(row);
            element.Q<Label>("preview").text = GrimoireTranslationCatalog.DisplayText(row, GrimoireSettings.Locale);
            element.Q<Label>("type").text = string.Join(", ", _typesById[row.id]);
            element.Q<Label>("coverage").text = TranslationCoverage(row);
        }

        private string TranslationCoverage(StringResource row)
        {
            var languages = GrimoireGameLanguages.Codes.Where(code => !GrimoireGameLanguages.IsDefault(code)).ToArray();
            var count = (row.translations ?? Array.Empty<StringTranslation>()).Count(translation =>
                translation != null && !string.IsNullOrEmpty(translation.translated_text) &&
                (languages.Length == 0 || languages.Any(code =>
                    string.Equals(code, translation.language_code, StringComparison.OrdinalIgnoreCase))));
            return languages.Length > 0 ? $"{count}/{languages.Length} translated" : $"{count} translations";
        }

        private void RenderDetail()
        {
            if (_detail == null) return;
            _detail.Clear();
            var row = _rows.FirstOrDefault(item => item.id == _selectedId);
            if (row == null)
            {
                _detail.Add(Label("Select a string to see its translations and copy its key.", "strings-info"));
                return;
            }

            _detail.Add(Label(string.Join(", ", _typesById[row.id]).ToUpperInvariant(), "strings-kind"));
            _detail.Add(Label(GrimoireStringPickerWindow.DisplayCode(row), "strings-detail-key"));
            var lookup = LookupKey(row);
            var actions = Row("strings-actions");
            actions.Add(new Button(() => Copy(lookup, "Key copied")) { text = "Copy key" });
            var expression = "Grimoire.PluginV2.GrimoireStrings.Get(" + Quote(lookup) + ")";
            actions.Add(new Button(() => Copy(expression, "C# call copied")) { text = "Copy C# call" });
            _detail.Add(actions);
            if (lookup == row.id && !string.IsNullOrEmpty(row.abbrev))
                _detail.Add(Label("This Loca key is duplicated, so code uses the unique string ID.", "strings-info"));
            _detail.Add(Label(expression, "strings-code"));
            _detail.Add(Label("Source · " + GrimoireGameLanguages.SourceLanguageLabel, "strings-section"));
            _detail.Add(ValueCard(row.source_text, null));

            _detail.Add(Label("Translations", "strings-section"));
            var languages = GrimoireGameLanguages.Codes.Where(code => !GrimoireGameLanguages.IsDefault(code)).ToList();
            foreach (var translation in row.translations ?? Array.Empty<StringTranslation>())
                if (translation != null && !string.IsNullOrWhiteSpace(translation.language_code) &&
                    !languages.Any(code => string.Equals(code, translation.language_code, StringComparison.OrdinalIgnoreCase)))
                    languages.Add(translation.language_code);
            foreach (var code in languages)
            {
                var translation = (row.translations ?? Array.Empty<StringTranslation>()).FirstOrDefault(value =>
                    value != null && string.Equals(value.language_code, code, StringComparison.OrdinalIgnoreCase));
                _detail.Add(ValueCard(translation?.translated_text,
                    GrimoireGameLanguages.DisplayName(code) + (translation?.approved == true ? " · approved" : "")));
            }
            if (languages.Count == 0)
                _detail.Add(Label("No target languages are configured for this game.", "strings-info"));
            if (!string.IsNullOrWhiteSpace(row.context))
            {
                _detail.Add(Label("Context", "strings-section"));
                _detail.Add(ValueCard(row.context, null));
            }
        }

        private string LookupKey(StringResource row)
        {
            var code = GrimoireStringPickerWindow.DisplayCode(row);
            return _duplicateCodes.Contains(code) ? row.id : code;
        }

        private void CopyShownKeys()
        {
            Copy(string.Join(Environment.NewLine, _visible.Select(LookupKey)),
                $"Copied {_visible.Count} keys");
        }

        private void Copy(string value, string message)
        {
            EditorGUIUtility.systemCopyBuffer = value ?? "";
            _notice = message;
            UpdateNotice();
        }

        private static string Quote(string value) => "\"" + (value ?? "")
            .Replace("\\", "\\\\").Replace("\"", "\\\"")
            .Replace("\r", "\\r").Replace("\n", "\\n") + "\"";

        private static VisualElement ValueCard(string value, string heading)
        {
            var card = Column("strings-value-card");
            if (!string.IsNullOrEmpty(heading)) card.Add(Label(heading, "strings-value-heading"));
            card.Add(Label(string.IsNullOrEmpty(value) ? "Missing" : value, "strings-value-text"));
            return card;
        }

        private static Label Label(string text, string className)
        {
            var label = new Label(text);
            label.AddToClassList(className);
            return label;
        }

        private static VisualElement Row(string className)
        {
            var element = new VisualElement();
            element.AddToClassList(className);
            element.style.flexDirection = FlexDirection.Row;
            return element;
        }

        private static VisualElement Column(string className)
        {
            var element = new VisualElement();
            element.AddToClassList(className);
            element.style.flexDirection = FlexDirection.Column;
            return element;
        }
    }
}
