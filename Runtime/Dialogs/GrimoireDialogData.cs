using System;
using System.Collections.Generic;
using System.Globalization;

namespace Grimoire.PluginV2
{
    // Serialized dialog graph stored inside a GrimoireDialogAsset. Written by the
    // Connect importer; read by the dialog runner. You normally never touch these
    // directly — use GrimoireDialogPlayer and DialogLine instead.

    public enum GrimoireDialogNodeType
    {
        /// <summary>A spoken line. Waits for Next().</summary>
        Line,

        /// <summary>A line with answer options. Waits for Choose().</summary>
        Question,

        /// <summary>Writer-facing context. Skipped during playback.</summary>
        Context,

        /// <summary>Branches on a variable. Resolved automatically.</summary>
        Condition,

        /// <summary>Changes a variable. Resolved automatically.</summary>
        Setter,

        /// <summary>Jumps to another node or dialog. Resolved automatically.</summary>
        Jump,

        /// <summary>Hands over to another dialog. Resolved automatically.</summary>
        ExternalDialog,

        /// <summary>Ends the dialog (shown first when it has text).</summary>
        End,
    }

    public enum GrimoireDialogValueType
    {
        None,
        Boolean,
        Number,
        Text,
    }

    /// <summary>A boolean, number, or text value that Unity can serialize.</summary>
    [Serializable]
    public class GrimoireDialogValue
    {
        public GrimoireDialogValueType type;
        public bool boolValue;
        public double numberValue;
        public string textValue = "";

        public bool HasValue => type != GrimoireDialogValueType.None;

        public object ToObject()
        {
            switch (type)
            {
                case GrimoireDialogValueType.Boolean: return boolValue;
                case GrimoireDialogValueType.Number: return numberValue;
                case GrimoireDialogValueType.Text: return textValue ?? "";
                default: return null;
            }
        }

        public static GrimoireDialogValue From(object value)
        {
            var result = new GrimoireDialogValue();
            switch (value)
            {
                case null:
                    break;
                case bool b:
                    result.type = GrimoireDialogValueType.Boolean;
                    result.boolValue = b;
                    break;
                case double d:
                    result.type = GrimoireDialogValueType.Number;
                    result.numberValue = d;
                    break;
                case float f:
                    result.type = GrimoireDialogValueType.Number;
                    result.numberValue = f;
                    break;
                case long l:
                    result.type = GrimoireDialogValueType.Number;
                    result.numberValue = l;
                    break;
                case int i:
                    result.type = GrimoireDialogValueType.Number;
                    result.numberValue = i;
                    break;
                case string s:
                    result.type = GrimoireDialogValueType.Text;
                    result.textValue = s;
                    break;
                default:
                    result.type = GrimoireDialogValueType.Text;
                    result.textValue = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
                    break;
            }

            return result;
        }

        public override string ToString()
        {
            switch (type)
            {
                case GrimoireDialogValueType.Boolean: return boolValue ? "true" : "false";
                case GrimoireDialogValueType.Number: return numberValue.ToString(CultureInfo.InvariantCulture);
                case GrimoireDialogValueType.Text: return textValue ?? "";
                default: return "(none)";
            }
        }
    }

    /// <summary>
    /// Points at a value: a literal, a dialog variable, a Grimoire type element,
    /// or an object field. Object fields are read from a Grimoire Object Link in
    /// the scene first, then from the imported export's ObjectRuntime, then from
    /// the library value baked in at import; type elements need a resolver on
    /// GrimoireDialogPlayer.
    /// </summary>
    [Serializable]
    public class GrimoireDialogValueRef
    {
        public const string SourceLiteral = "literal";
        public const string SourceDialogVariable = "dialog_variable";
        public const string SourceTypeElement = "type_element";
        public const string SourceObjectField = "object_field";

        public string source = "";
        public GrimoireDialogValue literal = new GrimoireDialogValue();
        public string variableName = "";
        public string typeId = "";
        public string elementId = "";
        public string objectId = "";
        public string sectionId = "";
        public string fieldId = "";
        public string label = "";

        /// <summary>Object code id (e.g. "house/interactables_bed"), filled in on import.</summary>
        public string objectKey = "";

        /// <summary>Field name as used by the export (e.g. "interactionCount"), filled in on import.</summary>
        public string fieldName = "";

        /// <summary>Object display name (e.g. "Hendrik"), filled in on import.</summary>
        public string objectName = "";

        /// <summary>Field kind in Grimoire ("boolean", "number", "text", "reference", ...), filled in on import.</summary>
        public string fieldType = "";

        /// <summary>
        /// Object-field value copied at import. Used when play has no
        /// ObjectRuntime entry yet, matching the Grimoire play view which
        /// starts from the library value.
        /// </summary>
        public GrimoireDialogValue authoredValue = new GrimoireDialogValue();

        public bool IsSet => !string.IsNullOrEmpty(source);
    }

    /// <summary>Where to go next: a node in this dialog, or a node in another dialog.</summary>
    [Serializable]
    public class GrimoireDialogLink
    {
        public string nextNode = "";
        public string nextDialogId = "";
        public string nextDialogNode = "";

        public bool IsEmpty => string.IsNullOrEmpty(nextNode) && string.IsNullOrEmpty(nextDialogId);
        public bool IsExternal => !string.IsNullOrEmpty(nextDialogId);
    }

    [Serializable]
    public class GrimoireDialogTranslation
    {
        public string languageCode = "";
        public string text = "";
        public string voiceUrl = "";
    }

    /// <summary>Changes a variable: set, increment, or decrement.</summary>
    [Serializable]
    public class GrimoireDialogVariableAction
    {
        public string variable = "";
        public string op = "set";
        public GrimoireDialogValue value = new GrimoireDialogValue();
        public GrimoireDialogValueRef targetRef = new GrimoireDialogValueRef();
        public GrimoireDialogValueRef valueRef = new GrimoireDialogValueRef();
        public bool enforceRange = true;

        public bool IsSet => !string.IsNullOrEmpty(variable) || targetRef.IsSet;
    }

    /// <summary>Compares a variable (or reference) against a value.</summary>
    [Serializable]
    public class GrimoireDialogCondition
    {
        public string variable = "";
        public string op = "==";
        public GrimoireDialogValue value = new GrimoireDialogValue();
        public GrimoireDialogValueRef subjectRef = new GrimoireDialogValueRef();
        public GrimoireDialogValueRef valueRef = new GrimoireDialogValueRef();

        public bool IsSet => !string.IsNullOrEmpty(variable) || subjectRef.IsSet;
    }

    /// <summary>One branch of a condition node. The first matching branch wins.</summary>
    [Serializable]
    public class GrimoireDialogConditionBranch
    {
        public string op = "==";
        public GrimoireDialogValue value = new GrimoireDialogValue();
        public GrimoireDialogValueRef valueRef = new GrimoireDialogValueRef();
        public GrimoireDialogLink link = new GrimoireDialogLink();
    }

    /// <summary>An answer on a question node.</summary>
    [Serializable]
    public class GrimoireDialogOption
    {
        public string id = "";
        public string text = "";
        public string stringId = "";
        public List<GrimoireDialogTranslation> translations = new List<GrimoireDialogTranslation>();
        public GrimoireDialogLink link = new GrimoireDialogLink();
        public GrimoireDialogVariableAction action = new GrimoireDialogVariableAction();
        public GrimoireDialogCondition visibleWhen = new GrimoireDialogCondition();
        public bool alwaysAvailable;
    }

    [Serializable]
    public class GrimoireDialogNode
    {
        public string id = "";
        public string identifier = "";
        public string name = "";
        public GrimoireDialogNodeType type;

        public string text = "";
        public string stringId = "";
        public string voiceUrl = "";
        public List<GrimoireDialogTranslation> translations = new List<GrimoireDialogTranslation>();

        public string speakerId = "";
        public string speakerName = "";

        public GrimoireDialogLink next = new GrimoireDialogLink();

        public List<GrimoireDialogOption> options = new List<GrimoireDialogOption>();
        public bool pickOnce;

        public string conditionVariable = "";
        public string conditionCheckVariable = "";
        public GrimoireDialogValueRef conditionSubjectRef = new GrimoireDialogValueRef();
        public GrimoireDialogValueRef conditionCheckRef = new GrimoireDialogValueRef();
        public List<GrimoireDialogConditionBranch> conditionBranches = new List<GrimoireDialogConditionBranch>();
        public GrimoireDialogLink whenTrue = new GrimoireDialogLink();
        public GrimoireDialogLink whenFalse = new GrimoireDialogLink();

        public GrimoireDialogVariableAction setter = new GrimoireDialogVariableAction();
    }

    [Serializable]
    public class GrimoireDialogVariable
    {
        public string name = "";
        public string type = "boolean";
        public GrimoireDialogValue initialValue = new GrimoireDialogValue();
        public string description = "";
        public bool hasRange;
        public double rangeMin;
        public double rangeMax;
    }
}
