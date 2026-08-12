using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;

namespace Grimoire.PluginV2.Editor
{
    /// <summary>
    /// Pushes edited <c>game_engine_editable</c> field values to Grimoire via
    /// PATCH /api/v1/objects/{id}, and notifies the Connect window to refresh.
    /// </summary>
    public static class GrimoireFieldSync
    {
        public static event Action<ObjectViewDocument> DocumentUpdated;

        public static async Task<ApiResult<ObjectViewDocument>> PushFieldUpdatesAsync(
            string gameId,
            string objectId,
            IList<FieldValueUpdate> updates)
        {
            if (string.IsNullOrWhiteSpace(gameId) || string.IsNullOrWhiteSpace(objectId))
            {
                return ApiResult<ObjectViewDocument>.Fail(
                    "Missing game or object id.",
                    "missing_parameter");
            }

            if (updates == null || updates.Count == 0)
            {
                return ApiResult<ObjectViewDocument>.Fail(
                    "No field updates to send.",
                    "missing_parameter");
            }

            if (!GrimoireSettings.IsConfigured)
            {
                return ApiResult<ObjectViewDocument>.Fail(
                    "Sign in and choose a workspace first (Window > Grimoire > Grimoire Connect).",
                    "not_configured");
            }

            await GrimoireAuthSession.EnsureFreshTokenAsync();

            var payload = new FieldValueUpdate[updates.Count];
            for (var i = 0; i < updates.Count; i++)
            {
                payload[i] = updates[i];
            }

            var patched = await GrimoireApiClient.PatchObjectFieldsAsync(gameId, objectId, payload);
            if (patched.Success && patched.Data != null)
            {
                DocumentUpdated?.Invoke(patched.Data);
            }

            return patched;
        }

        /// <summary>
        /// Builds the stored glossary value for a view field from an edit buffer.
        /// Returns false when the kind cannot be written from the plugin.
        /// </summary>
        public static bool TryBuildStoredValue(ViewField field, string editBuffer, out object value, out string error)
        {
            value = null;
            error = null;

            if (field == null)
            {
                error = "Missing field.";
                return false;
            }

            switch (ResolveEditKind(field))
            {
                case ObjectViewKinds.Text:
                    value = editBuffer ?? "";
                    if (field.hints?.character_limit is int limit && limit > 0 &&
                        ((string)value).Length > limit)
                    {
                        error = $"Exceeds character limit ({limit}).";
                        return false;
                    }

                    return true;

                case ObjectViewKinds.Number:
                    if (!double.TryParse(
                            editBuffer,
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out var number))
                    {
                        error = "Enter a valid number.";
                        return false;
                    }

                    var integer = field.hints != null &&
                                  (field.hints.field_type == "number" ||
                                   (field.HasValues &&
                                    field.values[0].number != null &&
                                    field.values[0].number.integer));
                    value = integer ? (object)(long)Math.Round(number) : number;
                    return true;

                case ObjectViewKinds.Boolean:
                    value = string.Equals(editBuffer, "true", StringComparison.OrdinalIgnoreCase) ||
                            editBuffer == "1";
                    return true;

                case ObjectViewKinds.Vector:
                    if (!TryParseVector(editBuffer, field, out var vector, out error))
                    {
                        return false;
                    }

                    value = vector;
                    return true;

                default:
                    error = $"Kind '{field.kind}' cannot be edited from the plugin.";
                    return false;
            }
        }

        public static string BufferFromField(ViewField field)
        {
            if (field == null || !field.HasValues)
            {
                return ResolveEditKind(field) == ObjectViewKinds.Boolean ? "false" : "";
            }

            var value = field.values[0];
            switch (ResolveEditKind(field))
            {
                case ObjectViewKinds.Text:
                    return value.plain ?? value.text?.content ?? "";

                case ObjectViewKinds.Number:
                    if (value.number != null)
                    {
                        return value.number.integer
                            ? ((long)Math.Round(value.number.value)).ToString(CultureInfo.InvariantCulture)
                            : value.number.value.ToString(CultureInfo.InvariantCulture);
                    }

                    return value.plain ?? "";

                case ObjectViewKinds.Boolean:
                    return value.boolean != null && value.boolean.value ? "true" : "false";

                case ObjectViewKinds.Vector:
                    return FormatVectorBuffer(value);

                default:
                    return value.plain ?? "";
            }
        }

        public static bool IsSupportedEditKind(string kind) =>
            kind == ObjectViewKinds.Text ||
            kind == ObjectViewKinds.Number ||
            kind == ObjectViewKinds.Boolean ||
            kind == ObjectViewKinds.Vector ||
            kind == ObjectViewKinds.Empty;

        /// <summary>
        /// Empty fields still carry <c>hints.field_type</c>; use that so a blank
        /// number/boolean can be authored from the Editable tab.
        /// </summary>
        public static string ResolveEditKind(ViewField field)
        {
            if (field == null)
            {
                return ObjectViewKinds.Text;
            }

            if (field.kind != ObjectViewKinds.Empty && !string.IsNullOrEmpty(field.kind))
            {
                return field.kind;
            }

            switch (field.hints?.field_type)
            {
                case "boolean":
                    return ObjectViewKinds.Boolean;
                case "number":
                case "float":
                    return ObjectViewKinds.Number;
                case "vector2":
                case "vector3":
                    return ObjectViewKinds.Vector;
                default:
                    return ObjectViewKinds.Text;
            }
        }

        private static bool TryParseVector(string buffer, ViewField field, out Dictionary<string, double> vector, out string error)
        {
            vector = null;
            error = null;

            var axes = ResolveAxes(field);
            var parts = (buffer ?? "")
                .Replace("(", "")
                .Replace(")", "")
                .Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length != axes.Length)
            {
                error = $"Enter {axes.Length} components ({string.Join(", ", axes)}).";
                return false;
            }

            vector = new Dictionary<string, double>(axes.Length);
            for (var i = 0; i < axes.Length; i++)
            {
                if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var component))
                {
                    error = $"Invalid {axes[i]} component.";
                    vector = null;
                    return false;
                }

                vector[axes[i]] = component;
            }

            return true;
        }

        private static string[] ResolveAxes(ViewField field)
        {
            if (field?.hints?.field_type == "vector2")
            {
                return new[] { "x", "y" };
            }

            if (field != null && field.HasValues &&
                field.values[0].vector?.components != null &&
                field.values[0].vector.components.Length > 0)
            {
                var components = field.values[0].vector.components;
                var axes = new string[components.Length];
                for (var i = 0; i < components.Length; i++)
                {
                    axes[i] = string.IsNullOrEmpty(components[i].axis) ? "x" : components[i].axis;
                }

                return axes;
            }

            return new[] { "x", "y", "z" };
        }

        private static string FormatVectorBuffer(ViewValue value)
        {
            if (value?.vector?.components == null || value.vector.components.Length == 0)
            {
                return "";
            }

            var parts = new string[value.vector.components.Length];
            for (var i = 0; i < value.vector.components.Length; i++)
            {
                parts[i] = value.vector.components[i].value.ToString(CultureInfo.InvariantCulture);
            }

            return string.Join(", ", parts);
        }
    }
}
