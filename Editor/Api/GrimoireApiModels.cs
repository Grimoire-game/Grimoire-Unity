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

    /// <summary>
    /// Body of <c>POST /api/v1/objects</c>. The plugin sends a draft only:
    /// name, optional description, code id, and tags, and an optional template.
    /// Custom sections and field overlays are not part of this payload.
    /// </summary>
    public class CreateObjectRequest
    {
        public string name;
        public string description;
        public string code_id;
        public string[] tags;
        public string template_id;

        /// <summary>Always <c>draft</c> when sent by the plugin.</summary>
        public string status;
    }

    /// <summary>Object returned by <c>POST /api/v1/objects</c> (201).</summary>
    public class CreatedObject : ObjectSummary
    {
        public CreatedObjectSection[] sections;
    }

    public class CreatedObjectSection
    {
        public string id;
        public string title;
        public CreatedObjectField[] fields;
    }

    public class CreatedObjectField
    {
        public string id;
        public string type;
        public string label;
        public string string_id;
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
    /// One field value update for <c>PATCH /api/v1/objects/{id}</c> or an
    /// engine-commit field change. <see cref="value"/> is the stored glossary
    /// shape (string, number, bool, vector object, or array for <c>multiple</c>).
    /// </summary>
    public class FieldValueUpdate
    {
        public string id;
        public object value;
        public object base_value;
    }

    // ---------------------------------------------------------------------
    // Engine commits (queued engine sync for review)
    // ---------------------------------------------------------------------

    /// <summary>
    /// One change in <c>POST /api/v1/engine-commits</c>. Send either
    /// <c>field_id</c> + <c>value</c> or <c>game_engine_data</c>, not both.
    /// </summary>
    public class EngineCommitChangeRequest
    {
        public string object_id;
        public string field_id;
        public object value;
        public object base_value;
        public GameEngineInstance[] game_engine_data;
    }

    public class EngineCommitSummary
    {
        public string id;
        public string title;
        public string description;
        public string source;
        public string status;
        public string created_at;
    }

    public class EngineCommitCreatedData
    {
        public EngineCommitSummary commit;
        public EngineCommitChangeResult[] changes;
    }

    /// <summary>Body of <c>PATCH /api/v1/objects/{id}</c> when a change is queued (202).</summary>
    public class EngineCommitQueuedData
    {
        public string commit_id;
        public string status;
        public EngineCommitChangeResult[] changes;
    }

    public class EngineCommitChangeResult
    {
        public string id;
        public string object_id;
        public string object_name;
        public string change_type;
        public string field_id;
        public string field_label;
        public bool has_base;
        public object base_value;
        public object new_value;
        public object current_value;
        public object snapshot_value;
        public string status;
        public string apply_error;
        public string state;
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

        /// <summary>Language codes this game is localized into, source language included.</summary>
        public string[] languages;

        /// <summary>The game's source language; empty when the game never set one.</summary>
        public string default_language;
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
    // Dialogs (GET /api/v1/dialogs, GET /api/v1/dialogs/{id})
    // ---------------------------------------------------------------------

    public class DialogSummary
    {
        public string id;
        public string game_id;
        public string name;
        public string dialog_key;
        public string[] tags;
        public string folder;
        public string status;
        public string created_at;
        public string updated_at;
    }

    public class DialogResource : DialogSummary
    {
        public string description;
        public DialogDataDto data;
    }

    /// <summary>The dialog `data` blob. Legacy dialogs may only carry `sections`.</summary>
    public class DialogDataDto
    {
        public DialogNodeDto[] nodes;
        public string startingNode;
        public DialogSectionDto[] sections;
        public string startingSection;
        public bool? translatable;
        public string speakerMode;
        public string speakerTypeId;
        public string[] speakerTemplateIds;
        public DialogVariableDto[] variables;
    }

    public class DialogSectionDto
    {
        public string id;
        public string name;
        public string section_identifier;
        public int? order_index;
        public DialogNodeDto[] fields;
        public string[] fieldOrder;
    }

    /// <summary>
    /// One node (or one legacy section field). Values typed as object hold the
    /// raw JSON primitive: bool, long, double, or string.
    /// </summary>
    public class DialogNodeDto
    {
        public string id;
        public string name;
        public string node_identifier;
        public int? order_index;
        public string type;
        public string text;
        public bool? translatable;
        public string string_id;
        public string speaker;
        public string next_node;
        public string next_dialog;
        public string next_dialog_node;
        public DialogNodeConditionDto condition;
        public ConditionOptionDto[] conditionOptions;
        public DialogOptionDto[] options;
        public bool? pickOnce;
        public VariableActionDto setter;

        public string next_section;
        public string next_field;
        public string next_dialog_section;
        public string next_section_true;
        public string next_section_false;
        public string next_dialog_true;
        public string next_dialog_false;
        public string next_dialog_section_true;
        public string next_dialog_section_false;
    }

    public class DialogNodeConditionDto
    {
        public string variable;
        public string checkVariable;
        public ValueRefDto subjectRef;
        public ValueRefDto checkRef;
    }

    public class ConditionOptionDto
    {
        public string id;
        public object condition_value;
        public ValueRefDto valueRef;
        public string @operator;
        public string next_node;
        public string next_dialog;
        public string next_dialog_node;
        public int? order_index;
        public string next_section;
        public string next_dialog_section;
    }

    public class DialogOptionDto
    {
        public string id;
        public string option_text;
        public bool? translatable;
        public string string_id;
        public string next_node;
        public string next_dialog;
        public string next_dialog_node;
        public int? order_index;
        public VariableActionDto variableAction;
        public VariableConditionDto visibilityCondition;
        public bool? alwaysAvailable;
        public string next_section;
        public string next_dialog_section;
    }

    public class VariableActionDto
    {
        public string variable;
        public string @operator;
        public object value;
        public ValueRefDto targetRef;
        public ValueRefDto valueRef;
        public bool? enforceRange;
    }

    public class VariableConditionDto
    {
        public string variable;
        public string @operator;
        public object value;
        public ValueRefDto subjectRef;
        public ValueRefDto valueRef;
    }

    public class ValueRefDto
    {
        public string source;
        public object value;
        public string valueType;
        public string variableName;
        public string typeId;
        public string elementId;
        public string objectId;
        public string sectionId;
        public string fieldId;
        public string label;
    }

    public class DialogVariableDto
    {
        public string name;
        public string type;
        public object initialValue;
        public string description;
        public double? rangeMin;
        public double? rangeMax;
    }

    // ---------------------------------------------------------------------
    // Strings (GET /api/v1/strings, PATCH translations)
    // ---------------------------------------------------------------------

    public class StringTranslation
    {
        public string id;
        public string dialogue_line_id;
        public string language_code;
        public string translated_text;
        public bool approved;
        public string voice_url;
        public string created_at;
        public string updated_at;
    }

    public class StringResource
    {
        public string id;
        public string version_id;
        public string game_id;
        public string source_text;
        public string section;
        public string context;
        public string abbrev;
        public string status;
        public int? character_limit;
        public string voice_url;
        public StringTranslation[] translations;
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
