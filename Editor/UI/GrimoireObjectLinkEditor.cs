using System;
using UnityEditor;
using UnityEngine;

namespace Grimoire.PluginV2.Editor
{
    /// <summary>
    /// Inspector for <see cref="GrimoireObjectLink"/>: pick an object from
    /// Grimoire, choose which transform fields sync into
    /// <c>game_engine_data</c>, and open the object in Connect.
    /// </summary>
    [CustomEditor(typeof(GrimoireObjectLink))]
    public class GrimoireObjectLinkEditor : UnityEditor.Editor
    {
        private string _validationMessage;
        private MessageType _validationType = MessageType.None;
        private bool _syncing;

        public override void OnInspectorGUI()
        {
            var link = (GrimoireObjectLink)target;

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.TextField(
                    new GUIContent("Object key", "The Grimoire object's code_id, e.g. 'characters/aragorn'. Set via Pick from Grimoire."),
                    link.ObjectKey);
            }

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Sync to game engine data", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "When linked, selected fields are written to this object's game_engine_data in Grimoire. " +
                "Removing the link or this component clears the entry.",
                MessageType.None);

            EditorGUI.BeginChangeCheck();
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
                    SyncUpsert(link, "Updated game_engine_data sync fields.");
                }
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();

            using (new EditorGUI.DisabledScope(_syncing))
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
                            GrimoireObjectKeyResolver.Remember(GrimoireSettings.GameId, summary.code_id, summary.id);
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
                                RemovePreviousThenUpsert(link, previous);
                            }
                            else
                            {
                                SyncUpsert(link, _validationMessage);
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
            using (new EditorGUI.DisabledScope(_syncing || (!link.HasKey && string.IsNullOrEmpty(link.CachedObjectId))))
            {
                if (GUILayout.Button("Sync now"))
                {
                    SyncUpsert(link, "Synced game_engine_data.");
                }

                if (GUILayout.Button("Unlink"))
                {
                    Unlink(link);
                }
            }

            EditorGUILayout.EndHorizontal();

            if (_syncing)
            {
                EditorGUILayout.HelpBox("Syncing with Grimoire…", MessageType.Info);
            }
            else if (!string.IsNullOrEmpty(_validationMessage))
            {
                EditorGUILayout.HelpBox(_validationMessage, _validationType);
            }
        }

        private async void SyncUpsert(GrimoireObjectLink link, string successMessage)
        {
            if (_syncing || link == null)
            {
                return;
            }

            _syncing = true;
            Repaint();

            var result = await GrimoireGameEngineSync.UpsertAsync(link);

            _syncing = false;
            if (link == null)
            {
                return;
            }

            if (result.Success)
            {
                GrimoireGameEngineSyncHooks.Remember(link);
                _validationMessage = successMessage;
                _validationType = MessageType.Info;
            }
            else
            {
                _validationMessage = result.Error;
                _validationType = MessageType.Error;
            }

            Repaint();
        }

        private async void RemovePreviousThenUpsert(
            GrimoireObjectLink link, GrimoireGameEngineSync.LinkIdentity previous)
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

            _syncing = false;
            if (link == null)
            {
                return;
            }

            if (result.Success)
            {
                GrimoireGameEngineSyncHooks.Remember(link);
                if (_validationType != MessageType.Warning)
                {
                    _validationMessage = "Linked and synced game_engine_data.";
                    _validationType = MessageType.Info;
                }
            }
            else
            {
                _validationMessage = result.Error;
                _validationType = MessageType.Error;
            }

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
            EditorUtility.SetDirty(link);
            GrimoireGameEngineSyncHooks.Forget(link);

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
