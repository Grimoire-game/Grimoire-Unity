using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Grimoire.PluginV2.Editor
{
    /// <summary>
    /// Data models for the Grimoire Public API v1, matching the OpenAPI spec
    /// and Documentation/object-view-schema.md. Field names mirror the wire
    /// format (snake_case) so Newtonsoft maps them without attributes.
    /// </summary>
    public static class ObjectViewKinds
    {
        public const string Text = "text";
        public const string Number = "number";
        public const string Boolean = "boolean";
        public const string Vector = "vector";
        public const string Reference = "reference";
        public const string Media = "media";
        public const string Link = "link";
        public const string Empty = "empty";
    }

    // ---------------------------------------------------------------------
    // Response envelopes
    // ---------------------------------------------------------------------

    public class ApiEnvelope
    {
        public bool success;
        public string error;
        public string code;
    }

    public class ListEnvelope<T> : ApiEnvelope
    {
        public T[] data;
        public int count;
    }

    public class SingleEnvelope<T> : ApiEnvelope
    {
        public T data;
    }

    // ---------------------------------------------------------------------
    // Objects
    // ---------------------------------------------------------------------

    public class ObjectTemplateRef
    {
        public string id;
        public string name;
    }

    public class ObjectSummary
    {
        public string id;
        public string game_id;
        public string name;
        public string type;
        public string description;
        public string code_id;
        public string[] tags;
        public string folder;
        public string status;
        public string thumbnail_url;
        public ObjectTemplateRef template;
        public string version;
        public string created_at;
        public string updated_at;
    }

    public class TextPayload
    {
        public string content;
        public string format;
        public string string_id;
        public bool translated;

        public bool IsHtml => format == "html";
    }

    public class NumberPayload
    {
        public double value;
        public bool integer;
    }

    public class BooleanPayload
    {
        public bool value;
    }

    public class VectorComponent
    {
        public string axis;
        public double value;
    }

    public class VectorPayload
    {
        public VectorComponent[] components;
    }

    public class ReferencePayload
    {
        public string ref_kind;
        public string id;
        public string name;
        public string code_id;

        /// <summary>A reference whose target was deleted resolves to a null name.</summary>
        public bool IsResolved => !string.IsNullOrEmpty(name);
    }

    public class MediaPayload
    {
        public string url;
        public string media_kind;

        public bool IsImage => media_kind == "image";
    }

    public class LinkPayload
    {
        public string href;
        public string label;
    }

    /// <summary>
    /// A value carries `plain` plus at most one payload; the field's `kind`
    /// names which one to read. See object-view-schema.md, "Two invariants".
    /// </summary>
    public class ViewValue
    {
        public string plain;
        public TextPayload text;
        public NumberPayload number;
        public BooleanPayload boolean;
        public VectorPayload vector;
        public ReferencePayload reference;
        public MediaPayload media;
        public LinkPayload link;
    }

    public class FieldHints
    {
        public string field_type;
        public bool required;
        public bool translatable;
        public bool multiline;
        public bool read_only;
        /// <summary>
        /// Template Game engine / Info toggle. True = runtime-adjustable in the
        /// engine plugin; false = informational display-only context.
        /// </summary>
        public bool game_engine_editable;
        public string documentation;
        public int? character_limit;
    }

    public class ViewField
    {
        public string id;
        public string label;
        public string kind;
        public bool multiple;
        public string plain;
        public FieldHints hints;
        public ViewValue[] values;

        public bool HasValues => values != null && values.Length > 0;

        /// <summary>
        /// True when the template marks this field Game engine and the caller's
        /// role may edit it (<see cref="FieldHints.read_only"/> is false).
        /// </summary>
        public bool IsGameEngineEditable =>
            hints != null && hints.game_engine_editable && !hints.read_only;
    }

    /// <summary>
    /// One field value update for <c>PATCH /api/v1/objects/{id}</c>.
    /// <see cref="value"/> is the stored glossary shape (string, number, bool,
    /// vector object, or array for <c>multiple</c> fields).
    /// </summary>
    public class FieldValueUpdate
    {
        public string id;
        public object value;
    }

    public class ViewSection
    {
        public string id;
        public string title;
        public string documentation;
        public ViewField[] fields;
    }

    public class ViewLocale
    {
        public string requested;
        public string resolved;
    }

    public class GameEngineVector3
    {
        public double x;
        public double y;
        public double z;

        public override string ToString() => $"({FormatComponent(x)}, {FormatComponent(y)}, {FormatComponent(z)})";

        private static string FormatComponent(double value) =>
            Math.Abs(value % 1) < 0.0001 ? value.ToString("0") : value.ToString("0.###");
    }

    public class GameEngineInstance
    {
        public string id;
        public string engine_instance_id;
        public string scene;
        public GameEngineVector3 location;
        public GameEngineVector3 rotation;
        public GameEngineVector3 scale;
    }

    public class ObjectViewDocument
    {
        public string schema_version;
        public ObjectSummary @object;
        public ViewLocale locale;
        public ViewSection[] sections;
        public GameEngineInstance[] game_engine_data;
        public GrimoireTask[] tasks;
    }

    // ---------------------------------------------------------------------
    // Tasks
    // ---------------------------------------------------------------------

    public class ContentBlock
    {
        public string body;
        public string[] mentions;
        public JArray data_links;
    }

    /// <summary>Wire name: ObjectAttachedTask. Both tasks (`is_task`) and notes.</summary>
    public class GrimoireTask
    {
        public string id;
        public string game_id;
        public string entity_type;
        public string entity_id;

        /// <summary>Sub-element anchor with a shape that varies by kind; null means the whole object.</summary>
        public JToken anchor;

        public ContentBlock[] content;
        public bool is_task;
        public string status;
        public string[] assignees;
        public string due_by;
        public string[] followed_by;
        public bool archived;
        public string created_by;
        public string status_updated_by;
        public string created_at;
        public string updated_at;
        public string last_activity_at;
        public int reply_count;
        public string latest_reply_at;

        public string Body =>
            content != null && content.Length > 0 && !string.IsNullOrEmpty(content[0].body)
                ? content[0].body
                : "";
    }

    // ---------------------------------------------------------------------
    // Workflow statuses
    // ---------------------------------------------------------------------

    public class WorkflowStatusEntry
    {
        public string key;
        public string label;
        public string color;
        public int order;
        public bool locked;
    }

    public class WorkflowStatusData
    {
        public string domain;
        public string source;
        public WorkflowStatusEntry[] statuses;
    }

    // ---------------------------------------------------------------------
    // Games
    // ---------------------------------------------------------------------

    public class GameDirectoryEntry
    {
        public string id;
        public string name;
        public string company_id;
        public string company_name;
    }

    public class UserDirectoryEntry
    {
        public string id;
        public string name;
        public string username;

        public string DisplayName =>
            !string.IsNullOrEmpty(name) ? name :
            !string.IsNullOrEmpty(username) ? username : id;
    }

    // ---------------------------------------------------------------------
    // Auth
    // ---------------------------------------------------------------------

    public class AuthUser
    {
        public string id;
        public string email;
        public string username;
        public string full_name;
        public string avatar_url;

        public string DisplayName =>
            !string.IsNullOrEmpty(full_name) ? full_name :
            !string.IsNullOrEmpty(username) ? username : email;
    }

    /// <summary>
    /// Login responds with one of two shapes (session or 2FA challenge); this
    /// merges them so one deserialization handles both. `requires_2fa` tells
    /// which shape arrived.
    /// </summary>
    public class AuthSessionData
    {
        public string token;
        public string expires_in;
        public AuthUser user;
        public bool requires_2fa_setup;
        public bool requires_2fa;
        public string temp_token;
    }

    // ---------------------------------------------------------------------
    // Export versions (legacy /api/exports route; used by Export tab)
    // ---------------------------------------------------------------------

    public class ExportVersion
    {
        public string id;
        public string game_id;
        public string game_name;
        public string company_id;
        public string version_name;
        public string platform;
        public string template_name;
        public string storage_path;
        public long file_size;
        public string file_type;
        public string created_at;
        public string created_by;
        public int download_count;
        public string last_downloaded_at;
        public bool is_public;
        public string download_url;
    }

    // ---------------------------------------------------------------------
    // Schema versioning
    // ---------------------------------------------------------------------

    public static class ObjectViewSchema
    {
        /// <summary>The schema major version this plugin was written against.</summary>
        public const int SupportedMajor = 1;

        private static bool _warnedAboutVersion;

        /// <summary>
        /// A major version skew is reported but never fatal: the `plain`
        /// fallback means most of the document still displays, and refusing to
        /// render would turn a cosmetic drift into an outage in the editor.
        /// </summary>
        public static void WarnOnVersionMismatch(string schemaVersion)
        {
            if (_warnedAboutVersion || string.IsNullOrEmpty(schemaVersion))
            {
                return;
            }

            var parts = schemaVersion.Split('.');
            if (parts.Length == 0 || !int.TryParse(parts[0], out var major) || major == SupportedMajor)
            {
                return;
            }

            _warnedAboutVersion = true;
            Debug.LogWarning(
                $"[Grimoire] Object View schema is v{schemaVersion} but this plugin targets v{SupportedMajor}.x. " +
                "Fields may render as plain text. Update Grimoire Plugin 2.");
        }
    }
}
