using System;
using UnityEditor;
using UnityEngine;

namespace Grimoire.PluginV2.Editor
{
    /// <summary>
    /// Inspector for <see cref="GrimoireObjectLink"/>: pick an object from
    /// Grimoire, choose which transform fields sync into
    /// <c>game_engine_data</c>, edit linked fields, and open the object in Connect.
    /// </summary>
    [CustomEditor(typeof(GrimoireObjectLink))]
    public class GrimoireObjectLinkEditor : UnityEditor.Editor
    {
        private const float FieldLabelWidth = 140f;

        private string _validationMessage;
        private MessageType _validationType = MessageType.None;
        private bool _syncing;
        private bool _syncFoldout = true;
        private bool _fieldsFoldout = true;

        private void OnEnable()
        {
            GrimoireLinkedFieldStore.Changed += OnLinkedFieldsChanged;
        }

        private void OnDisable()
        {
            GrimoireLinkedFieldStore.Changed -= OnLinkedFieldsChanged;
        }

        private void OnLinkedFieldsChanged() => Repaint();

        public override void OnInspectorGUI()
        {
            var link = (GrimoireObjectLink)target;

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.TextField(
                    new GUIContent("Object key", "The Grimoire object's code_id, e.g. 'characters/aragorn'. Set via Pick from Grimoire."),
                    link.ObjectKey);
            }

            DrawSyncSection(link);
            DrawEditableFieldsSection(link);

            if (_syncing)
            {
                EditorGUILayout.HelpBox("Syncing with Grimoire…", MessageType.Info);
            }
            else if (!string.IsNullOrEmpty(_validationMessage))
            {
                EditorGUILayout.HelpBox(_validationMessage, _validationType);
            }
        }

        private void DrawSyncSection(GrimoireObjectLink link)
        {
            EditorGUILayout.Space(6);
            _syncFoldout = EditorGUILayout.Foldout(
                _syncFoldout, "Sync to game engine data", true, EditorStyles.foldoutHeader);
            if (!_syncFoldout)
            {
                return;
            }

            EditorGUILayout.HelpBox(
                "When linked, selected fields are queued into this object's game_engine_data for review in Grimoire. " +
                "Removing the link or this component queues removal of the entry.",
                MessageType.None);

            EditorGUI.BeginChangeCheck();
            using (new EditorGUI.DisabledScope(Application.isPlaying))
            {
                var syncPosition = EditorGUILayout.Toggle(
                    new GUIContent("Position", "World position → location"),
                    link.SyncPosition);
                var syncRotation = EditorGUILayout.Toggle(
                    new GUIContent("Rotation", "World euler angles → rotation"),
                    link.SyncRotation);
                var syncScale = EditorGUILayout.Toggle(
                    new GUIContent("Scale", "Local scale → scale"),
                    link.SyncScale);
                var syncIdName = EditorGUILayout.Toggle(
                    new GUIContent("Id / Name", "Unity GlobalObjectId and GameObject name → engine_instance_id"),
                    link.SyncIdName);

                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(link, "Change Grimoire sync fields");
                    link.SyncPosition = syncPosition;
                    link.SyncRotation = syncRotation;
                    link.SyncScale = syncScale;
                    link.SyncIdName = syncIdName;
                    EditorUtility.SetDirty(link);

                    if (link.HasKey || !string.IsNullOrEmpty(link.CachedObjectId))
                    {
                        SyncUpsert(link, "Updated game_engine_data sync fields (queued for review).");
                    }
                }
            }

            if (Application.isPlaying)
            {
                EditorGUILayout.HelpBox(
                    "Game engine data sync is editor-only. Exit Play Mode to update engine data.",
                    MessageType.Info);
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();

            using (new EditorGUI.DisabledScope(_syncing || Application.isPlaying))
            {
                if (GUILayout.Button("Pick from Grimoire..."))
                {
                    if (!GrimoireSettings.IsConfigured)
                    {
                        _validationMessage = "Sign in and choose a workspace first (Window > Grimoire > Grimoire Connect).";
                        _validationType = MessageType.Warning;
                    }
                    else
                    {
                        GrimoireObjectPickerWindow.Open(summary =>
                        {
                            var previous = GrimoireGameEngineSync.CaptureIdentity(link);

                            Undo.RecordObject(link, "Pick Grimoire object");
                            link.ObjectKey = summary.code_id ?? "";
                            link.CachedObjectId = summary.id;
                            link.ClearLinkedFields();
                            GrimoireObjectKeyResolver.Remember(GrimoireSettings.GameId, summary.code_id, summary.id);
                            GrimoireObjectKeyResolver.RememberSummary(GrimoireSettings.GameId, summary);
                            EditorUtility.SetDirty(link);

                            if (string.IsNullOrEmpty(summary.code_id))
                            {
                                _validationMessage =
                                    $"'{summary.name}' has no key (code_id) in Grimoire. The link uses its UUID, " +
                                    "but giving the object a Code ID in Grimoire makes the link robust.";
                                _validationType = MessageType.Warning;
                            }
                            else
                            {
                                _validationMessage = $"Linked to '{summary.name}' ({summary.code_id}).";
                                _validationType = MessageType.Info;
                            }

                            // If this GameObject was previously linked to another
                            // Grimoire object, drop the old engine instance first.
                            if (!string.IsNullOrEmpty(previous.ObjectId) &&
                                previous.ObjectId != summary.id)
                            {
                                RemovePreviousThenUpsert(link, previous, refreshFields: true);
                            }
                            else
                            {
                                SyncUpsert(link, _validationMessage, refreshFields: true);
                            }

                            Repaint();
                        });
                    }
                }

                using (new EditorGUI.DisabledScope(!link.HasKey && string.IsNullOrEmpty(link.CachedObjectId)))
                {
                    if (GUILayout.Button("Open in Connect"))
                    {
                        GrimoireConnectWindow.ShowAndLoad(link);
                    }
                }
            }

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(
                       _syncing || Application.isPlaying ||
                       (!link.HasKey && string.IsNullOrEmpty(link.CachedObjectId))))
            {
                if (GUILayout.Button(
                        new GUIContent(
                            "Sync now",
                            Application.isPlaying
                                ? "Exit Play Mode to sync game engine data."
                                : "Queue this object's transform into game_engine_data for review.")))
                {
                    SyncUpsert(link, "Queued game_engine_data for review in Grimoire.");
                }

                if (GUILayout.Button("Unlink"))
                {
                    Unlink(link);
                }
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawEditableFieldsSection(GrimoireObjectLink link)
        {
            EditorGUILayout.Space(10);

            var deviationCount = link.FieldDeviationCount;
            var title = deviationCount > 0
                ? $"Editable fields ({deviationCount} to sync)"
                : "Editable fields";

            _fieldsFoldout = EditorGUILayout.Foldout(_fieldsFoldout, title, true, EditorStyles.foldoutHeader);
            if (!_fieldsFoldout)
            {
                return;
            }

            EditorGUILayout.HelpBox(
                "These values live on this Object Link and persist with the scene. " +
                "A • marks fields that differ from Grimoire — commit them from the Sync tab.",
                MessageType.None);

            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(
                       _syncing || (!link.HasKey && string.IsNullOrEmpty(link.CachedObjectId))))
            {
                if (GUILayout.Button(
                        new GUIContent(
                            "Refresh from Grimoire",
                            "Pull field definitions and Grimoire baselines. Local edits are kept."),
                        GUILayout.Height(22)))
                {
                    RefreshLinkedFields(link, preserveLocalEdits: true);
                }
            }

            using (new EditorGUI.DisabledScope(deviationCount == 0))
            {
                if (GUILayout.Button(
                        new GUIContent("Reset to Grimoire", "Discard local field edits."),
                        GUILayout.Width(120),
                        GUILayout.Height(22)))
                {
                    if (GrimoireLinkedFieldStore.ResetToGrimoire(link))
                    {
                        _validationMessage = "Local field edits discarded.";
                        _validationType = MessageType.Info;
                    }
                }
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(4);

            var fields = link.LinkedFields;
            if (fields == null || fields.Count == 0)
            {
                EditorGUILayout.LabelField(
                    "No editable fields cached yet. Link an object and refresh from Grimoire.",
                    EditorStyles.miniLabel);
                return;
            }

            string currentSection = null;
            for (var i = 0; i < fields.Count; i++)
            {
                var field = fields[i];
                if (field == null)
                {
                    continue;
                }

                var section = string.IsNullOrEmpty(field.SectionTitle) ? "Fields" : field.SectionTitle;
                if (section != currentSection)
                {
                    currentSection = section;
                    EditorGUILayout.Space(4);
                    EditorGUILayout.LabelField(section, EditorStyles.boldLabel);
                }

                DrawLinkedField(link, field);
            }
        }

        private static void DrawLinkedField(GrimoireObjectLink link, GrimoireLinkedField field)
        {
            EditorGUILayout.BeginHorizontal();

            var label = field.DisplayLabel;
            if (!field.ReadOnly && field.IsDeviated)
            {
                label += " •";
            }

            EditorGUILayout.LabelField(
                new GUIContent(
                    label,
                    field.IsDeviated
                        ? $"Differs from Grimoire ({field.GrimoireValue})"
                        : $"{field.FieldType} ({field.Kind})"),
                GUILayout.Width(FieldLabelWidth));

            EditorGUILayout.BeginVertical();
            EditorGUI.BeginDisabledGroup(field.ReadOnly);

            var kind = string.IsNullOrEmpty(field.Kind) ? ObjectViewKinds.Text : field.Kind;
            if (!GrimoireFieldSync.IsSupportedEditKind(kind))
            {
                EditorGUILayout.LabelField(
                    string.IsNullOrEmpty(field.LocalValue) ? "Not set" : field.LocalValue,
                    EditorStyles.miniLabel);
            }
            else if (kind == ObjectViewKinds.Boolean)
            {
                var current = string.Equals(field.LocalValue, "true", StringComparison.OrdinalIgnoreCase);
                var toggled = EditorGUILayout.Toggle(current);
                if (!field.ReadOnly && toggled != current)
                {
                    GrimoireLinkedFieldStore.SetLocalValue(link, field.FieldId, toggled ? "true" : "false");
                }
            }
            else if (field.Multiline)
            {
                var buffer = field.LocalValue ?? "";
                var next = EditorGUILayout.TextArea(buffer, GUILayout.MinHeight(40));
                if (!field.ReadOnly && next != buffer)
                {
                    GrimoireLinkedFieldStore.SetLocalValue(link, field.FieldId, next);
                }
            }
            else
            {
                var buffer = field.LocalValue ?? "";
                var next = EditorGUILayout.TextField(buffer);
                if (!field.ReadOnly && next != buffer)
                {
                    GrimoireLinkedFieldStore.SetLocalValue(link, field.FieldId, next);
                }
            }

            EditorGUI.EndDisabledGroup();

            if (field.ReadOnly)
            {
                EditorGUILayout.LabelField("Read-only", EditorStyles.miniLabel);
            }
            else if (field.IsDeviated)
            {
                var preview = string.IsNullOrEmpty(field.GrimoireValue) ? "(empty)" : field.GrimoireValue;
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

        private async void RefreshLinkedFields(GrimoireObjectLink link, bool preserveLocalEdits)
        {
            if (_syncing || link == null)
            {
                return;
            }

            if (!GrimoireSettings.IsConfigured)
            {
                _validationMessage = "Sign in and choose a workspace first (Window > Grimoire > Grimoire Connect).";
                _validationType = MessageType.Warning;
                Repaint();
                return;
            }

            _syncing = true;
            Repaint();

            var result = await GrimoireLinkedFieldStore.RefreshFromGrimoireAsync(link, preserveLocalEdits);

            _syncing = false;
            if (link == null)
            {
                return;
            }

            if (result.Success)
            {
                var count = link.LinkedFields?.Count ?? 0;
                var dirty = link.FieldDeviationCount;
                _validationMessage = dirty > 0
                    ? $"Loaded {count} editable field{(count == 1 ? "" : "s")} · {dirty} differ from Grimoire."
                    : $"Loaded {count} editable field{(count == 1 ? "" : "s")}.";
                _validationType = MessageType.Info;
            }
            else
            {
                _validationMessage = result.Error;
                _validationType = MessageType.Error;
            }

            Repaint();
        }

        private async void SyncUpsert(
            GrimoireObjectLink link, string successMessage, bool refreshFields = false)
        {
            if (_syncing || link == null)
            {
                return;
            }

            if (GrimoireGameEngineSync.IsPlayModeBlocked)
            {
                _validationMessage = "Game engine data sync is editor-only. Exit Play Mode first.";
                _validationType = MessageType.Warning;
                Repaint();
                return;
            }

            _syncing = true;
            Repaint();

            var result = await GrimoireGameEngineSync.UpsertAsync(link);

            if (link == null)
            {
                _syncing = false;
                return;
            }

            if (result.Success)
            {
                GrimoireGameEngineSyncHooks.Remember(link);
                _validationMessage = successMessage;
                _validationType = MessageType.Info;

                if (refreshFields)
                {
                    var fields = await GrimoireLinkedFieldStore.RefreshFromGrimoireAsync(
                        link, preserveLocalEdits: false);
                    if (fields.Success)
                    {
                        var count = link.LinkedFields?.Count ?? 0;
                        _validationMessage =
                            $"{successMessage} Loaded {count} editable field{(count == 1 ? "" : "s")}.";
                    }
                    else if (!string.IsNullOrEmpty(fields.Error))
                    {
                        _validationMessage =
                            $"{successMessage} Field refresh failed: {fields.Error}";
                        _validationType = MessageType.Warning;
                    }
                }
            }
            else
            {
                _validationMessage = result.Error;
                _validationType = MessageType.Error;
            }

            _syncing = false;
            Repaint();
        }

        private async void RemovePreviousThenUpsert(
            GrimoireObjectLink link,
            GrimoireGameEngineSync.LinkIdentity previous,
            bool refreshFields = false)
        {
            if (_syncing || link == null)
            {
                return;
            }

            _syncing = true;
            Repaint();

            var removed = await GrimoireGameEngineSync.RemoveAsync(null, previous);
            if (!removed.Success)
            {
                Debug.LogWarning($"[Grimoire] Could not clear previous game_engine_data entry: {removed.Error}");
            }

            var result = await GrimoireGameEngineSync.UpsertAsync(link);

            if (link == null)
            {
                _syncing = false;
                return;
            }

            if (result.Success)
            {
                GrimoireGameEngineSyncHooks.Remember(link);
                if (_validationType != MessageType.Warning)
                {
                    _validationMessage = "Linked and queued game_engine_data for review.";
                    _validationType = MessageType.Info;
                }

                if (refreshFields)
                {
                    var fields = await GrimoireLinkedFieldStore.RefreshFromGrimoireAsync(
                        link, preserveLocalEdits: false);
                    if (fields.Success)
                    {
                        var count = link.LinkedFields?.Count ?? 0;
                        _validationMessage =
                            $"Linked and loaded {count} editable field{(count == 1 ? "" : "s")}.";
                        _validationType = MessageType.Info;
                    }
                }
            }
            else
            {
                _validationMessage = result.Error;
                _validationType = MessageType.Error;
            }

            _syncing = false;
            Repaint();
        }

        private async void Unlink(GrimoireObjectLink link)
        {
            if (_syncing || link == null)
            {
                return;
            }

            var identity = GrimoireGameEngineSync.CaptureIdentity(link);

            _syncing = true;
            Repaint();

            var result = await GrimoireGameEngineSync.RemoveAsync(link, identity);

            Undo.RecordObject(link, "Unlink Grimoire object");
            link.ObjectKey = "";
            link.CachedObjectId = "";
            link.ClearLinkedFields();
            EditorUtility.SetDirty(link);
            GrimoireLinkedFieldStore.NotifyExternalChange();
            GrimoireGameEngineSyncHooks.Forget(link);
            GrimoireGameEngineDirtyTracker.Forget(link);

            _syncing = false;

            if (result.Success)
            {
                _validationMessage = "Unlinked and removed from game_engine_data.";
                _validationType = MessageType.Info;
            }
            else
            {
                _validationMessage =
                    $"Unlinked locally, but Grimoire remove failed: {result.Error}";
                _validationType = MessageType.Warning;
            }

            Repaint();
        }
    }

    /// <summary>
    /// Searchable object picker backed by GET /api/v1/objects. Search happens
    /// server-side (name substring match); the debounce keeps a fast typist
    /// from firing a request per keystroke.
    /// </summary>
    public class GrimoireObjectPickerWindow : EditorWindow
    {
        private const int PageSize = 50;
        private const double SearchDebounceSeconds = 0.35;

        private Action<ObjectSummary> _onPicked;
        private ObjectSummary[] _results = Array.Empty<ObjectSummary>();
        private string _search = "";
        private string _pendingSearch;
        private double _searchQueuedAt;
        private bool _loading;
        private string _error;
        private Vector2 _scroll;

        public static void Open(Action<ObjectSummary> onPicked)
        {
            var window = GetWindow<GrimoireObjectPickerWindow>(true, "Pick Grimoire Object", true);
            window.minSize = new Vector2(380, 320);
            window._onPicked = onPicked;
            window.Fetch("");
        }

        private void OnGUI()
        {
            EditorGUI.BeginChangeCheck();
            _search = EditorGUILayout.TextField(_search, EditorStyles.toolbarSearchField);
            if (EditorGUI.EndChangeCheck())
            {
                _pendingSearch = _search;
                _searchQueuedAt = EditorApplication.timeSinceStartup;
            }

            if (_pendingSearch != null &&
                EditorApplication.timeSinceStartup - _searchQueuedAt >= SearchDebounceSeconds)
            {
                var query = _pendingSearch;
                _pendingSearch = null;
                Fetch(query);
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

            foreach (var summary in _results)
            {
                DrawRow(summary);
            }

            if (!_loading && _results.Length == 0 && string.IsNullOrEmpty(_error))
            {
                EditorGUILayout.LabelField("No objects found.", EditorStyles.centeredGreyMiniLabel);
            }

            EditorGUILayout.EndScrollView();

            // The debounce needs OnGUI to run again after the delay elapses.
            if (_pendingSearch != null)
            {
                Repaint();
            }
        }

        private void DrawRow(ObjectSummary summary)
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);

            EditorGUILayout.BeginVertical();
            EditorGUILayout.LabelField(summary.name, EditorStyles.boldLabel);

            var detail = string.IsNullOrEmpty(summary.code_id) ? "(no key)" : summary.code_id;
            if (!string.IsNullOrEmpty(summary.folder))
            {
                detail += $"  ·  {summary.folder}";
            }

            EditorGUILayout.LabelField(detail, EditorStyles.miniLabel);
            EditorGUILayout.EndVertical();

            if (GUILayout.Button("Select", GUILayout.Width(60), GUILayout.Height(28)))
            {
                _onPicked?.Invoke(summary);
                Close();
            }

            EditorGUILayout.EndHorizontal();
        }

        private async void Fetch(string search)
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

            var result = await GrimoireApiClient.ListObjectsAsync(GrimoireSettings.GameId, search, PageSize);

            // Results for a superseded query would flash outdated rows.
            if (_pendingSearch == null)
            {
                _loading = false;

                if (result.Success)
                {
                    _results = result.Data;
                }
                else
                {
                    _error = result.Error;
                }

                Repaint();
            }
        }
    }
}
