using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace Grimoire.PluginV2.Editor
{
    /// <summary>
    /// Draws an Object View Document with IMGUI: one switch over the eight
    /// value kinds. It knows nothing about Grimoire field types, templates,
    /// translations or reference tables — the server resolved all of that.
    ///
    /// The default branch is load-bearing: a kind this build has never heard of
    /// draws its `plain` text, which is what lets the platform add field types
    /// without shipping a new plugin. See Documentation/object-view-schema.md.
    /// </summary>
    public static class GrimoireObjectViewRenderer
    {
        private const float LabelWidth = 150f;

        private static readonly HashSet<string> ReportedUnknownKinds = new HashSet<string>();
        private static readonly Dictionary<string, Texture2D> ThumbnailCache = new Dictionary<string, Texture2D>();

        /// <summary>Raised when an async thumbnail arrives, so the window can repaint.</summary>
        public static event Action RepaintNeeded;

        /// <summary>
        /// Draws the document header and Info fields
        /// (<c>hints.game_engine_editable == false</c>). Game-engine-editable
        /// fields live on the Editable tab. Tasks are drawn by
        /// <see cref="GrimoireTasksPanel"/>.
        /// </summary>
        public static void Draw(ObjectViewDocument document)
        {
            if (document == null)
            {
                GrimoireEditorStyles.DrawInfoBox("No object loaded.");
                return;
            }

            DrawHeader(document);

            if (document.sections == null || document.sections.Length == 0)
            {
                GrimoireEditorStyles.DrawInfoBox(
                    "This object has no sections you can view. It may be empty, or your role may hide all of them.");
                return;
            }

            var drewAnyInfoField = false;
            foreach (var section in document.sections)
            {
                if (DrawSection(section, infoFieldsOnly: true))
                {
                    drewAnyInfoField = true;
                }
            }

            if (!drewAnyInfoField)
            {
                GrimoireEditorStyles.DrawInfoBox(
                    "No informational fields on this object. Game-engine-editable fields are on the Editable tab.");
            }
        }

        private static void DrawHeader(ObjectViewDocument document)
        {
            var summary = document.@object;
            var title = "Overview";

            if (!GrimoireEditorStyles.BeginCollapsibleSection("object-header", title, defaultExpanded: true))
            {
                return;
            }

            EditorGUILayout.BeginHorizontal();

            if (!string.IsNullOrEmpty(summary.thumbnail_url))
            {
                var thumbnail = GetThumbnail(summary.thumbnail_url);
                if (thumbnail != null)
                {
                    GUILayout.Label(thumbnail, GUILayout.Width(48), GUILayout.Height(48));
                }
            }

            EditorGUILayout.BeginVertical();
            if (!string.IsNullOrEmpty(summary.name))
            {
                EditorGUILayout.LabelField(summary.name, GrimoireEditorStyles.TitleStyle);
            }

            if (!string.IsNullOrEmpty(summary.description))
            {
                EditorGUILayout.LabelField(summary.description, EditorStyles.wordWrappedMiniLabel);
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();

            DrawMetaRow("Template", summary.template != null ? summary.template.name ?? summary.template.id : null);
            DrawMetaRow("Code ID", summary.code_id);
            DrawMetaRow("Folder", summary.folder);
            DrawMetaRow("Status", summary.status);
            DrawMetaRow("Version", summary.version);

            if (summary.tags != null && summary.tags.Length > 0)
            {
                DrawMetaRow("Tags", string.Join(", ", summary.tags));
            }

            // A requested locale that resolved to nothing means the object has
            // no translations yet, which is worth saying rather than silently
            // showing source text.
            if (document.locale != null && !string.IsNullOrEmpty(document.locale.requested))
            {
                if (string.IsNullOrEmpty(document.locale.resolved))
                {
                    GrimoireEditorStyles.DrawInfoBox(
                        $"No '{document.locale.requested}' translations found. Showing source text.");
                }
                else
                {
                    DrawMetaRow("Locale", document.locale.resolved);
                }
            }

            GrimoireEditorStyles.EndCollapsibleSection();
        }

        private static void DrawMetaRow(string label, string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(label, GUILayout.Width(70));
            EditorGUILayout.SelectableLabel(value, EditorStyles.miniLabel, GUILayout.Height(16));
            EditorGUILayout.EndHorizontal();
        }

        /// <returns>True when at least one field was drawn.</returns>
        private static bool DrawSection(ViewSection section, bool infoFieldsOnly)
        {
            if (section?.fields == null || section.fields.Length == 0)
            {
                return false;
            }

            var fieldsToDraw = new List<ViewField>();
            foreach (var field in section.fields)
            {
                if (field == null)
                {
                    continue;
                }

                var isEngineEditable = field.hints != null && field.hints.game_engine_editable;
                if (infoFieldsOnly && isEngineEditable)
                {
                    continue;
                }

                if (!infoFieldsOnly && !isEngineEditable)
                {
                    continue;
                }

                fieldsToDraw.Add(field);
            }

            if (fieldsToDraw.Count == 0)
            {
                return false;
            }

            var sectionId = infoFieldsOnly ? $"section:{section.title}" : $"editable-section:{section.title}";
            if (!GrimoireEditorStyles.BeginCollapsibleSection(sectionId, section.title, defaultExpanded: true))
            {
                return true;
            }

            if (!string.IsNullOrEmpty(section.documentation))
            {
                EditorGUILayout.LabelField(section.documentation, EditorStyles.wordWrappedMiniLabel);
                EditorGUILayout.Space(2);
            }

            foreach (var field in fieldsToDraw)
            {
                DrawField(field);
            }

            GrimoireEditorStyles.EndCollapsibleSection();
            return true;
        }

        private static void DrawField(ViewField field)
        {
            EditorGUILayout.BeginHorizontal();

            var label = field.hints != null && field.hints.required ? $"{field.label} *" : field.label;
            EditorGUILayout.LabelField(new GUIContent(label, Tooltip(field)), GUILayout.Width(LabelWidth));

            EditorGUILayout.BeginVertical();

            if (!field.HasValues)
            {
                DrawNotSet();
            }
            else
            {
                foreach (var value in field.values)
                {
                    DrawValue(field, value);
                }
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();
        }

        private static string Tooltip(ViewField field)
        {
            if (field.hints == null)
            {
                return field.kind;
            }

            var tooltip = $"{field.hints.field_type} ({field.kind})";

            if (field.hints.read_only)
            {
                tooltip += " — read-only for your role";
            }

            if (field.hints.game_engine_editable)
            {
                tooltip += " — game engine editable";
            }

            if (!string.IsNullOrEmpty(field.hints.documentation))
            {
                tooltip += $"\n{field.hints.documentation}";
            }

            return tooltip;
        }

        private static void DrawNotSet()
        {
            var style = new GUIStyle(EditorStyles.miniLabel) { fontStyle = FontStyle.Italic };
            var previous = GUI.color;
            GUI.color = new Color(previous.r, previous.g, previous.b, 0.55f);
            EditorGUILayout.LabelField("Not set", style);
            GUI.color = previous;
        }

        private static void DrawValue(ViewField field, ViewValue value)
        {
            switch (field.kind)
            {
                case ObjectViewKinds.Text:
                    DrawText(field, value);
                    break;

                case ObjectViewKinds.Number:
                    EditorGUILayout.SelectableLabel(value.plain, EditorStyles.label, GUILayout.Height(16));
                    break;

                case ObjectViewKinds.Boolean:
                    EditorGUI.BeginDisabledGroup(true);
                    EditorGUILayout.Toggle(value.boolean != null && value.boolean.value);
                    EditorGUI.EndDisabledGroup();
                    break;

                case ObjectViewKinds.Vector:
                    DrawVector(value);
                    break;

                case ObjectViewKinds.Reference:
                    DrawReference(value);
                    break;

                case ObjectViewKinds.Media:
                    DrawMedia(value);
                    break;

                case ObjectViewKinds.Link:
                    DrawLink(value);
                    break;

                case ObjectViewKinds.Empty:
                    DrawNotSet();
                    break;

                default:
                    DrawUnknownKind(field, value);
                    break;
            }
        }

        private static void DrawText(ViewField field, ViewValue value)
        {
            // `plain` is the stripped form of `content`; IMGUI cannot render
            // HTML, so it is the right thing to show for rich text.
            var text = value.plain ?? "";
            var multiline = field.hints != null && field.hints.multiline;

            if (multiline || text.Contains("\n"))
            {
                EditorGUILayout.LabelField(text, EditorStyles.wordWrappedLabel);
            }
            else
            {
                EditorGUILayout.SelectableLabel(text, EditorStyles.label, GUILayout.Height(16));
            }

            if (value.text != null && value.text.translated)
            {
                EditorGUILayout.LabelField("translated", EditorStyles.miniLabel);
            }
        }

        private static void DrawVector(ViewValue value)
        {
            if (value.vector == null || value.vector.components == null)
            {
                DrawNotSet();
                return;
            }

            EditorGUILayout.BeginHorizontal();

            // Iterating rather than assuming two or three axes keeps this
            // working if a future vector4 appears.
            foreach (var component in value.vector.components)
            {
                EditorGUILayout.LabelField($"{component.axis.ToUpperInvariant()} {component.value}", GUILayout.Width(80));
            }

            EditorGUILayout.EndHorizontal();
        }

        private static void DrawReference(ViewValue value)
        {
            var reference = value.reference;

            if (reference == null)
            {
                DrawNotSet();
                return;
            }

            EditorGUILayout.BeginHorizontal();

            if (reference.IsResolved)
            {
                EditorGUILayout.LabelField(new GUIContent(reference.name, $"{reference.ref_kind}: {reference.id}"));
            }
            else
            {
                // Surfacing the dangling id is how a designer finds out the
                // target was deleted, so it must not be hidden.
                EditorGUILayout.LabelField(
                    new GUIContent($"⚠ {reference.id}", $"This {reference.ref_kind} no longer exists"),
                    EditorStyles.miniLabel);
            }

            if (!string.IsNullOrEmpty(reference.code_id) &&
                GUILayout.Button(new GUIContent("Copy key", $"Copy {reference.code_id}"), EditorStyles.miniButton, GUILayout.Width(64)))
            {
                EditorGUIUtility.systemCopyBuffer = reference.code_id;
            }

            EditorGUILayout.EndHorizontal();
        }

        private static void DrawMedia(ViewValue value)
        {
            var media = value.media;

            if (media == null || string.IsNullOrEmpty(media.url))
            {
                DrawNotSet();
                return;
            }

            if (media.IsImage)
            {
                var thumbnail = GetThumbnail(media.url);
                if (thumbnail != null)
                {
                    GUILayout.Label(thumbnail, GUILayout.Height(64), GUILayout.Width(64));
                }
            }

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.SelectableLabel(media.url, EditorStyles.miniLabel, GUILayout.Height(16));

            if (GUILayout.Button("Open", EditorStyles.miniButton, GUILayout.Width(50)))
            {
                Application.OpenURL(media.url);
            }

            EditorGUILayout.EndHorizontal();
        }

        private static void DrawLink(ViewValue value)
        {
            var link = value.link;

            if (link == null || string.IsNullOrEmpty(link.href))
            {
                DrawNotSet();
                return;
            }

            if (GUILayout.Button(new GUIContent(link.label ?? link.href, link.href), EditorStyles.linkLabel))
            {
                Application.OpenURL(link.href);
            }

            var rect = GUILayoutUtility.GetLastRect();
            EditorGUIUtility.AddCursorRect(rect, MouseCursor.Link);
        }

        /// <summary>
        /// The forward-compatibility path. A document produced by a newer
        /// platform can contain kinds this build predates; every value carries
        /// `plain` precisely so they still display.
        /// </summary>
        private static void DrawUnknownKind(ViewField field, ViewValue value)
        {
            EditorGUILayout.LabelField(value.plain ?? "", EditorStyles.wordWrappedLabel);

            if (ReportedUnknownKinds.Add(field.kind))
            {
                Debug.LogWarning(
                    $"[Grimoire] Unknown Object View kind '{field.kind}' on field '{field.label}'. " +
                    "Showing plain text. Update Grimoire Plugin 2 to render it properly.");
            }
        }

        /// <summary>
        /// Thumbnails are fetched once per URL and kept for the editor session.
        /// A miss simply means no preview; the URL is always shown regardless.
        /// </summary>
        private static Texture2D GetThumbnail(string url)
        {
            if (ThumbnailCache.TryGetValue(url, out var cached))
            {
                return cached;
            }

            ThumbnailCache[url] = null;
            LoadThumbnail(url);
            return null;
        }

        private static void LoadThumbnail(string url)
        {
            var request = UnityWebRequestTexture.GetTexture(url);
            request.timeout = 15;
            request.SendWebRequest().completed += _ =>
            {
                if (request.result == UnityWebRequest.Result.Success)
                {
                    ThumbnailCache[url] = DownloadHandlerTexture.GetContent(request);
                    RepaintNeeded?.Invoke();
                }

                request.Dispose();
            };
        }

        /// <summary>Drops cached previews so a reopened window refetches them.</summary>
        public static void ClearCaches()
        {
            ThumbnailCache.Clear();
        }
    }
}
