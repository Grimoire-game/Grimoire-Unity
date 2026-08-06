using System;
using UnityEditor;
using UnityEngine;

namespace Grimoire.PluginV2.Editor
{
    /// <summary>
    /// Inspector for <see cref="GrimoireObjectLink"/>: pick an object from
    /// Grimoire and open it in Connect.
    /// </summary>
    [CustomEditor(typeof(GrimoireObjectLink))]
    public class GrimoireObjectLinkEditor : UnityEditor.Editor
    {
        private string _validationMessage;
        private MessageType _validationType = MessageType.None;

        public override void OnInspectorGUI()
        {
            var link = (GrimoireObjectLink)target;

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.TextField(
                    new GUIContent("Object key", "The Grimoire object's code_id, e.g. 'characters/aragorn'. Set via Pick from Grimoire."),
                    link.ObjectKey);
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();

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

            EditorGUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(_validationMessage))
            {
                EditorGUILayout.HelpBox(_validationMessage, _validationType);
            }
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
