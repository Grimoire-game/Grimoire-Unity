using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Grimoire.PluginV2.Editor
{
    /// <summary>
    /// Creates a draft library object with <c>POST /api/v1/objects</c>.
    /// The user confirms the selected game before anything is sent. Linking a
    /// GameObject is optional and still queues engine data for review.
    /// </summary>
    public class GrimoireCreateObjectWindow : EditorWindow
    {
        private const string BlankLabel = "Blank (default Title field)";

        private GrimoireObjectLink _link;
        private Action<string, MessageType> _onFinished;

        private string _gameId = "";
        private string _gameName = "";
        private string _name = "";
        private string _description = "";
        private string _codeId = "";
        private string _tags = "";
        private bool _fromTemplate;
        private bool _manualTemplateId;
        private int _templateIndex;
        private string _templateIdText = "";
        private bool _linkAfterCreate;

        private ObjectTemplateRef[] _templates = Array.Empty<ObjectTemplateRef>();
        private string[] _templateLabels = Array.Empty<string>();
        private int _templateGeneration;
        private bool _loadingTemplates;
        private string _templateNote;

        private bool _busy;
        private bool _created;
        private string _error;
        private string _info;
        private Vector2 _scroll;

        [MenuItem("Window/Grimoire/Create Object")]
        public static void OpenFromMenu()
        {
            Open(null);
        }

        public static void Open(GrimoireObjectLink link, Action<string, MessageType> onFinished = null)
        {
            var window = GetWindow<GrimoireCreateObjectWindow>(true, "Create Grimoire Object", true);
            window.minSize = new Vector2(440, 480);
            window.Begin(link, onFinished);
            window.Show();
        }

        private void Begin(GrimoireObjectLink link, Action<string, MessageType> onFinished)
        {
            _link = link;
            _onFinished = onFinished;
            _gameId = GrimoireSettings.GameId ?? "";
            _gameName = string.IsNullOrEmpty(GrimoireSettings.GameName)
                ? _gameId
                : GrimoireSettings.GameName;
            _name = link != null ? link.gameObject.name : "";
            _description = "";
            _codeId = "";
            _tags = "";
            _fromTemplate = false;
            _manualTemplateId = false;
            _templateIndex = 0;
            _templateIdText = "";
            _linkAfterCreate = false;
            _templates = Array.Empty<ObjectTemplateRef>();
            _templateLabels = Array.Empty<string>();
            _busy = false;
            _created = false;
            _error = null;
            _info = null;
            _templateNote = null;
            LoadTemplates();
        }

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            if (!GrimoireSettings.IsConfigured || string.IsNullOrEmpty(_gameId))
            {
                EditorGUILayout.HelpBox(
                    "Sign in and choose a company and game in Grimoire Connect, then open this window again.",
                    MessageType.Warning);
                EditorGUILayout.EndScrollView();
                return;
            }

            if (!string.Equals(GrimoireSettings.GameId, _gameId, StringComparison.Ordinal))
            {
                EditorGUILayout.HelpBox(
                    "The selected game changed. Close this window and start again so the object is created in the game you expect.",
                    MessageType.Warning);
                EditorGUILayout.EndScrollView();
                return;
            }

            EditorGUILayout.LabelField("Game", _gameName, EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "This saves a draft object in Grimoire as soon as you confirm. " +
                "Publish it in Grimoire when it is ready. " +
                "Linking a GameObject queues transform data for review afterwards.",
                MessageType.Info);

            if (GrimoireGameEngineSync.IsPlayModeBlocked)
            {
                EditorGUILayout.HelpBox(
                    "Exit Play Mode before creating an object.",
                    MessageType.Warning);
            }

            using (new EditorGUI.DisabledScope(_busy || _created))
            {
                _name = EditorGUILayout.TextField("Name", _name);
                _description = EditorGUILayout.TextField("Description", _description);
                _codeId = EditorGUILayout.TextField(
                    new GUIContent("Code ID", "Leave empty and Grimoire generates one from the name."),
                    _codeId);
                _tags = EditorGUILayout.TextField(
                    new GUIContent("Tags", "Optional. Separate tags with commas."),
                    _tags);

                EditorGUILayout.Space(6);
                _fromTemplate = EditorGUILayout.Toggle(
                    new GUIContent("Build from a template", "Uses that template's fields and default values."),
                    _fromTemplate);

                if (_fromTemplate)
                {
                    DrawTemplatePicker();
                }
                else
                {
                    EditorGUILayout.LabelField(BlankLabel, EditorStyles.miniLabel);
                }

                if (_link != null)
                {
                    EditorGUILayout.Space(6);
                    using (new EditorGUI.DisabledScope(GrimoireGameEngineSync.IsPlayModeBlocked))
                    {
                        var targetName = _link.gameObject != null ? _link.gameObject.name : "GameObject";
                        _linkAfterCreate = EditorGUILayout.Toggle(
                            new GUIContent(
                                "Link " + targetName,
                                "After the draft exists, link this GameObject. Off leaves the scene unchanged."),
                            _linkAfterCreate);
                    }
                }
            }

            if (_loadingTemplates)
            {
                EditorGUILayout.LabelField("Loading templates…", EditorStyles.centeredGreyMiniLabel);
            }
            else if (!string.IsNullOrEmpty(_templateNote))
            {
                EditorGUILayout.HelpBox(_templateNote, MessageType.None);
            }

            if (!string.IsNullOrEmpty(_error))
            {
                EditorGUILayout.HelpBox(_error, MessageType.Error);
            }

            if (!string.IsNullOrEmpty(_info))
            {
                EditorGUILayout.HelpBox(_info, MessageType.Info);
            }

            EditorGUILayout.Space(8);
            using (new EditorGUI.DisabledScope(
                       _busy || _created || GrimoireGameEngineSync.IsPlayModeBlocked ||
                       (_fromTemplate && _loadingTemplates)))
            {
                if (GUILayout.Button(_busy ? "Creating…" : "Create draft"))
                {
                    Create();
                }
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawTemplatePicker()
        {
            if (_loadingTemplates)
            {
                return;
            }

            if (_templates.Length > 0 && !_manualTemplateId)
            {
                _templateIndex = EditorGUILayout.Popup("Template", _templateIndex, _templateLabels);
            }

            if (_templates.Length > 0)
            {
                _manualTemplateId = EditorGUILayout.Toggle(
                    new GUIContent("Enter template id", "For a template that has no objects in this game yet."),
                    _manualTemplateId);
            }

            if (_templates.Length == 0 || _manualTemplateId)
            {
                _templateIdText = EditorGUILayout.TextField("Template id", _templateIdText);
            }
        }

        private async void LoadTemplates()
        {
            var generation = ++_templateGeneration;
            var gameId = _gameId;
            _loadingTemplates = true;
            _templateNote = null;
            Repaint();

            if (!GrimoireObjectCreate.IsUuid(gameId))
            {
                _loadingTemplates = false;
                return;
            }

            await GrimoireAuthSession.EnsureFreshTokenAsync();
            if (generation != _templateGeneration)
            {
                return;
            }

            await GrimoireObjectKeyResolver.EnsureLibraryCachedAsync(gameId);
            if (generation != _templateGeneration)
            {
                return;
            }

            _templates = GrimoireObjectKeyResolver.ListCachedTemplates(gameId);
            _templateLabels = new string[_templates.Length];
            for (var i = 0; i < _templates.Length; i++)
            {
                var template = _templates[i];
                _templateLabels[i] = string.IsNullOrEmpty(template.name) ? template.id : template.name;
            }

            if (_templateIndex >= _templates.Length)
            {
                _templateIndex = 0;
            }

            _loadingTemplates = false;
            if (!GrimoireObjectKeyResolver.IsLibraryCacheReady(gameId))
            {
                _templateNote =
                    "Template names could not be loaded. A blank draft still works, or enter a template id.";
            }
            else if (_templates.Length == 0)
            {
                _templateNote =
                    "No templates are in use in this game yet. Create a blank draft, or enter a template id.";
            }

            Repaint();
        }

        private async void Create()
        {
            if (_busy || _created)
            {
                return;
            }

            _error = null;
            _info = null;

            if (GrimoireGameEngineSync.IsPlayModeBlocked)
            {
                _error = "Exit Play Mode before creating an object.";
                Repaint();
                return;
            }

            if (!GrimoireSettings.IsConfigured ||
                !string.Equals(GrimoireSettings.GameId, _gameId, StringComparison.Ordinal))
            {
                _error = "The selected game changed. Close this window and start again.";
                Repaint();
                return;
            }

            var templateId = SelectedTemplateId();
            if (_fromTemplate && _loadingTemplates)
            {
                _error = "Wait until templates finish loading, or turn off Build from a template.";
                Repaint();
                return;
            }

            if (_fromTemplate && string.IsNullOrEmpty(templateId))
            {
                _error = "Choose a template, or turn off Build from a template.";
                Repaint();
                return;
            }

            var request = new CreateObjectRequest
            {
                name = _name,
                description = _description,
                code_id = _codeId,
                tags = ParseTags(_tags),
                template_id = _fromTemplate ? templateId : null,
            };

            var prepared = GrimoireObjectCreate.Prepare(request);
            if (!prepared.Success)
            {
                _error = prepared.Error;
                Repaint();
                return;
            }

            _busy = true;
            Repaint();

            await GrimoireAuthSession.EnsureFreshTokenAsync();
            if (this == null)
            {
                return;
            }

            if (!CanStillCreate())
            {
                _busy = false;
                Repaint();
                return;
            }

            if (!string.IsNullOrEmpty(prepared.Data.code_id))
            {
                var existing = await GrimoireObjectKeyResolver.ResolveAsync(_gameId, prepared.Data.code_id);
                if (this == null)
                {
                    return;
                }

                if (existing.Success)
                {
                    _busy = false;
                    _error =
                        $"Code ID '{prepared.Data.code_id}' is already used in this game. " +
                        "Leave Code ID empty to let Grimoire generate a new one.";
                    Repaint();
                    return;
                }

                if (existing.Code != "key_not_found")
                {
                    _busy = false;
                    _error = string.IsNullOrEmpty(existing.Error)
                        ? "Could not check whether this Code ID is already used."
                        : existing.Error;
                    Repaint();
                    return;
                }
            }

            var names = await GrimoireObjectCreate.FindExactNamesAsync(_gameId, prepared.Data.name);
            if (this == null)
            {
                return;
            }

            if (!names.Success)
            {
                _busy = false;
                _error = string.IsNullOrEmpty(names.Error)
                    ? "Could not check whether this name is already used."
                    : names.Error;
                Repaint();
                return;
            }

            if (!CanStillCreate())
            {
                _busy = false;
                Repaint();
                return;
            }

            var confirmed = EditorUtility.DisplayDialog(
                "Create draft object",
                BuildConfirmation(prepared.Data, names.Data),
                "Create draft",
                "Cancel");
            if (!confirmed || !CanStillCreate())
            {
                _busy = false;
                Repaint();
                return;
            }

            var created = await GrimoireApiClient.CreateObjectAsync(_gameId, prepared.Data);
            if (this == null)
            {
                return;
            }
            if (!created.Success)
            {
                _busy = false;
                _error = created.Error;
                Repaint();
                return;
            }

            GrimoireObjectKeyResolver.RememberSummary(_gameId, created.Data);
            if (!string.IsNullOrEmpty(created.Data.code_id))
            {
                GrimoireObjectKeyResolver.Remember(_gameId, created.Data.code_id, created.Data.id);
            }

            _created = true;
            _busy = false;

            var linkRequested = _linkAfterCreate && _link != null;
            var linked = false;
            string linkDetail = null;
            if (linkRequested)
            {
                var outcome = await TryLinkAsync(_link, created.Data);
                if (this == null)
                {
                    return;
                }

                linked = outcome.Linked;
                linkDetail = outcome.Detail;
            }

            var message = BuildResultMessage(created.Data, linked, linkDetail);
            var linkFailed = linkRequested && !linked;
            var messageType = linkDetail == null ? MessageType.Info : MessageType.Warning;
            if (linkFailed)
            {
                _error = message;
            }
            else
            {
                _info = message;
            }

            try
            {
                _onFinished?.Invoke(message, messageType);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }

            Repaint();
            if (!linkFailed)
            {
                Close();
            }
        }

        private bool CanStillCreate()
        {
            if (GrimoireGameEngineSync.IsPlayModeBlocked)
            {
                _error = "Exit Play Mode before creating an object.";
                return false;
            }

            if (!GrimoireSettings.IsConfigured ||
                !string.Equals(GrimoireSettings.GameId, _gameId, StringComparison.Ordinal))
            {
                _error = "The selected game changed. Close this window and start again.";
                return false;
            }

            return true;
        }

        private string SelectedTemplateId()
        {
            if (!_fromTemplate)
            {
                return null;
            }

            if (_manualTemplateId || _templates.Length == 0)
            {
                return string.IsNullOrWhiteSpace(_templateIdText) ? null : _templateIdText.Trim();
            }

            if (_templateIndex < 0 || _templateIndex >= _templates.Length)
            {
                return null;
            }

            return _templates[_templateIndex].id;
        }

        private string SelectedTemplateLabel(CreateObjectRequest prepared)
        {
            if (string.IsNullOrEmpty(prepared.template_id))
            {
                return BlankLabel;
            }

            for (var i = 0; i < _templates.Length; i++)
            {
                if (string.Equals(_templates[i].id, prepared.template_id, StringComparison.OrdinalIgnoreCase))
                {
                    return string.IsNullOrEmpty(_templates[i].name)
                        ? _templates[i].id
                        : _templates[i].name;
                }
            }

            return prepared.template_id;
        }

        private string BuildConfirmation(CreateObjectRequest prepared, NameCollision names)
        {
            var builder = new StringBuilder();
            builder.Append("Create a draft object in ").Append(_gameName).AppendLine("?");
            builder.AppendLine();
            builder.Append("Name: ").AppendLine(prepared.name);
            builder.Append("Structure: ").AppendLine(SelectedTemplateLabel(prepared));
            builder.Append("Code ID: ").AppendLine(
                string.IsNullOrEmpty(prepared.code_id) ? "Grimoire will generate one" : prepared.code_id);
            if (prepared.tags != null && prepared.tags.Length > 0)
            {
                builder.Append("Tags: ").AppendLine(string.Join(", ", prepared.tags));
            }

            builder.AppendLine();
            builder.Append("The object is saved immediately with status draft.");

            if (names.Matches != null && names.Matches.Length > 0)
            {
                builder.AppendLine();
                builder.AppendLine();
                builder.Append("An object named \"").Append(prepared.name).Append("\" is already in this game");
                var sample = names.Matches[0];
                if (!string.IsNullOrEmpty(sample.code_id))
                {
                    builder.Append(" (").Append(sample.code_id).Append(')');
                }

                builder.Append(". This adds another draft beside it.");
                if (names.Matches.Length > 1)
                {
                    builder.Append(" ").Append(names.Matches.Length).Append(" objects share this name.");
                }
            }
            else if (names.Incomplete)
            {
                builder.AppendLine();
                builder.AppendLine();
                builder.Append("Name search filled a full page. Confirm only if a new draft is still what you want.");
            }

            if (_linkAfterCreate && _link != null)
            {
                builder.AppendLine();
                builder.AppendLine();
                var targetName = _link.gameObject != null ? _link.gameObject.name : "this GameObject";
                builder.Append(targetName).Append(" will be linked after the draft exists. Transform data is queued for review.");
                if (!string.IsNullOrEmpty(_link.CachedObjectId) || _link.HasKey)
                {
                    builder.Append(" Its current Grimoire link is replaced.");
                }
            }

            return builder.ToString();
        }

        private static string BuildResultMessage(CreatedObject created, bool linked, string detail)
        {
            var name = string.IsNullOrEmpty(created.name) ? created.id : created.name;
            if (!linked && !string.IsNullOrEmpty(detail))
            {
                return $"Created draft '{name}'. The draft is already in Grimoire. {detail}";
            }

            if (linked && !string.IsNullOrEmpty(detail))
            {
                return $"Created draft '{name}' and linked this GameObject. {detail}";
            }

            if (linked)
            {
                return $"Created draft '{name}' and linked this GameObject. Transform sync was queued for review.";
            }

            return $"Created draft '{name}'.";
        }

        private struct LinkOutcome
        {
            public bool Linked;
            public string Detail;
        }

        private static async Task<LinkOutcome> TryLinkAsync(
            GrimoireObjectLink link, CreatedObject created)
        {
            if (link == null)
            {
                return new LinkOutcome
                {
                    Detail = "The Grimoire Object Link is gone, so nothing in the scene was linked.",
                };
            }

            if (GrimoireGameEngineSync.IsPlayModeBlocked)
            {
                return new LinkOutcome
                {
                    Detail = "Exit Play Mode, then use Pick from Grimoire if this GameObject should use the new draft. Nothing in the scene was linked.",
                };
            }

            var previous = GrimoireGameEngineSync.CaptureIdentity(link);
            Undo.RecordObject(link, "Link created Grimoire object");
            link.ObjectKey = created.code_id ?? "";
            link.CachedObjectId = created.id;
            link.ClearLinkedFields();
            EditorUtility.SetDirty(link);

            if (!string.IsNullOrEmpty(previous.ObjectId) && previous.ObjectId != created.id)
            {
                var removed = await GrimoireGameEngineSync.RemoveAsync(null, previous);
                if (!removed.Success)
                {
                    Debug.LogWarning(
                        $"[Grimoire] Could not clear the previous game_engine_data entry: {removed.Error}");
                }
            }

            var result = await GrimoireGameEngineSync.UpsertAsync(link);
            if (!result.Success)
            {
                return new LinkOutcome
                {
                    Detail = "This GameObject now points at the new object, and queuing its transform failed: " +
                             result.Error + " Use Sync now to try again.",
                };
            }

            GrimoireGameEngineSyncHooks.Remember(link);
            var fields = await GrimoireLinkedFieldStore.RefreshFromGrimoireAsync(link, preserveLocalEdits: false);
            if (!fields.Success && !string.IsNullOrEmpty(fields.Error))
            {
                return new LinkOutcome
                {
                    Linked = true,
                    Detail = "Transform sync was queued for review. Field refresh failed: " + fields.Error,
                };
            }

            return new LinkOutcome { Linked = true };
        }

        private static string[] ParseTags(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            var parts = raw.Split(',');
            var tags = new List<string>(parts.Length);
            foreach (var part in parts)
            {
                tags.Add(part);
            }

            return tags.ToArray();
        }
    }
}
