using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace Grimoire.PluginV2.Internal
{
    /// <summary>
    /// Reads and writes object fields through the imported export's
    /// <c>ObjectRuntime</c> (keyed by object code id + field name), found by
    /// reflection so the plugin compiles without an export in the project.
    /// </summary>
    internal static class GrimoireDialogExportBridge
    {
        private static bool _searched;
        private static Type _objectRuntime;

        public static bool HasObjectRuntime => ObjectRuntimeType != null;

        private static Type ObjectRuntimeType
        {
            get
            {
                if (!_searched)
                {
                    _searched = true;
                    _objectRuntime = GrimoireReflect.FindType("ObjectRuntime");
                }

                return _objectRuntime;
            }
        }

        public static bool TryReadObjectField(GrimoireDialogValueRef reference, out object value)
        {
            value = null;
            var type = ObjectRuntimeType;
            if (type == null || reference == null)
            {
                return false;
            }

            foreach (var key in ObjectKeys(reference))
            {
                foreach (var field in FieldNames(reference))
                {
                    if (TryGet<string[]>(type, "TryGetStringArray", key, field, out var array)) { value = array; return true; }
                    if (TryGet<string>(type, "TryGetString", key, field, out var text)) { value = text; return true; }
                    if (TryGet<bool>(type, "TryGetBoolean", key, field, out var flag)) { value = flag; return true; }
                    if (TryGet<double>(type, "TryGetNumber", key, field, out var number)) { value = number; return true; }
                }
            }

            return false;
        }

        public static bool TryWriteObjectField(GrimoireDialogValueRef reference, object value)
        {
            var type = ObjectRuntimeType;
            if (type == null || reference == null)
            {
                return false;
            }

            if (!TryFindExisting(type, reference, out var key, out var field))
            {
                return false;
            }

            switch (value)
            {
                case bool flag:
                    return Invoke(type, "SetBoolean", key, field, flag);
                case string[] array:
                    return Invoke(type, "SetStringArray", key, field, array);
                case string text:
                    return Invoke(type, "SetString", key, field, text);
                default:
                    if (GrimoireDialogConditions.IsNumberLike(value))
                    {
                        return Invoke(type, "SetNumber", key, field, GrimoireDialogConditions.ToNumber(value));
                    }

                    return Invoke(type, "SetString", key, field, GrimoireDialogConditions.AsString(value));
            }
        }

        /// <summary>"house/interactables_bed:interactionCount" for log messages.</summary>
        public static string DescribeKey(GrimoireDialogValueRef reference)
        {
            var key = First(ObjectKeys(reference)) ?? "?";
            var field = First(FieldNames(reference)) ?? "?";
            return key + ":" + field;
        }

        private static bool TryFindExisting(Type type, GrimoireDialogValueRef reference, out string key, out string field)
        {
            foreach (var k in ObjectKeys(reference))
            {
                foreach (var f in FieldNames(reference))
                {
                    if (TryGet<string[]>(type, "TryGetStringArray", k, f, out _) ||
                        TryGet<string>(type, "TryGetString", k, f, out _) ||
                        TryGet<bool>(type, "TryGetBoolean", k, f, out _) ||
                        TryGet<double>(type, "TryGetNumber", k, f, out _))
                    {
                        key = k;
                        field = f;
                        return true;
                    }
                }
            }

            key = null;
            field = null;
            return false;
        }

        private static IEnumerable<string> ObjectKeys(GrimoireDialogValueRef reference)
        {
            if (!string.IsNullOrEmpty(reference.objectKey)) yield return reference.objectKey;
            if (!string.IsNullOrEmpty(reference.objectId) && reference.objectId != reference.objectKey) yield return reference.objectId;
        }

        private static IEnumerable<string> FieldNames(GrimoireDialogValueRef reference)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var labelField = LabelFieldPart(reference.label);
            foreach (var raw in new[] { reference.fieldName, labelField, reference.fieldId })
            {
                if (string.IsNullOrEmpty(raw))
                {
                    continue;
                }

                if (seen.Add(raw)) yield return raw;

                var camel = ToCamelIdentifier(raw);
                if (!string.IsNullOrEmpty(camel) && seen.Add(camel)) yield return camel;
            }
        }

        // Dialog editor labels look like "bed: interactionCount".
        private static string LabelFieldPart(string label)
        {
            if (string.IsNullOrEmpty(label))
            {
                return null;
            }

            var colon = label.LastIndexOf(':');
            return colon >= 0 ? label.Substring(colon + 1).Trim() : null;
        }

        private static string ToCamelIdentifier(string text)
        {
            var builder = new StringBuilder(text.Length);
            var upperNext = false;
            foreach (var c in text)
            {
                if (!char.IsLetterOrDigit(c))
                {
                    upperNext = builder.Length > 0;
                    continue;
                }

                builder.Append(builder.Length == 0 ? char.ToLowerInvariant(c) : upperNext ? char.ToUpperInvariant(c) : c);
                upperNext = false;
            }

            return builder.ToString();
        }

        private static bool TryGet<T>(Type type, string method, string key, string field, out T value)
        {
            value = default;
            try
            {
                var m = type.GetMethod(method, BindingFlags.Public | BindingFlags.Static, null,
                    new[] { typeof(string), typeof(string), typeof(T).MakeByRefType() }, null);
                if (m == null)
                {
                    return false;
                }

                var args = new object[] { key, field, null };
                if (!(bool)m.Invoke(null, args))
                {
                    return false;
                }

                value = (T)args[2];
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool Invoke<T>(Type type, string method, string key, string field, T value)
        {
            try
            {
                var m = type.GetMethod(method, BindingFlags.Public | BindingFlags.Static, null,
                    new[] { typeof(string), typeof(string), typeof(T) }, null);
                if (m == null)
                {
                    return false;
                }

                m.Invoke(null, new object[] { key, field, value });
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string First(IEnumerable<string> values)
        {
            foreach (var value in values)
            {
                return value;
            }

            return null;
        }
    }
}
