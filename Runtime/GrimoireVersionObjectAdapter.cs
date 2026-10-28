using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Grimoire.PluginV2
{
    /// <summary>
    /// Reads the generated Database.Library and ObjectRuntime without a compile
    /// dependency on an imported version. Version keys are the exported Library
    /// keys, which can differ from an API object's code_id.
    /// </summary>
    internal static class GrimoireVersionObjectAdapter
    {
        private const BindingFlags PublicStatic = BindingFlags.Public | BindingFlags.Static;
        private const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;
        private static bool _searched;
        private static Dictionary<string, object> _library;
        private static Type _runtimeType;
        private static Type _logicType;

        public static bool IsAvailable => Library.Count > 0;
        public static IReadOnlyList<string> Keys => Library.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase).ToArray();

        private static Dictionary<string, object> Library
        {
            get
            {
                if (!_searched) Search();
                return _library;
            }
        }

        private static void Search()
        {
            _searched = true;
            _library = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            var database = FindGeneratedType("Database", null, type =>
                type.GetField("Library", PublicStatic) != null ||
                type.GetProperty("Library", PublicStatic) != null);
            var member = (object)database?.GetField("Library", PublicStatic) ??
                         database?.GetProperty("Library", PublicStatic);
            object source = null;
            try
            {
                if (member is FieldInfo field) source = field.GetValue(null);
                else if (member is PropertyInfo property) source = property.GetValue(null);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Grimoire] Could not read Database.Library: " + ex.Message);
            }

            if (source is IEnumerable entries)
            {
                foreach (var entry in entries)
                {
                    if (entry == null) continue;
                    var type = entry.GetType();
                    var key = type.GetProperty("Key")?.GetValue(entry) as string;
                    var value = type.GetProperty("Value")?.GetValue(entry);
                    if (!string.IsNullOrEmpty(key) && value != null) _library[key] = value;
                }
            }
            _runtimeType = FindGeneratedType("ObjectRuntime", database?.Namespace, type =>
                type.GetMethods(PublicStatic).Any(method => method.Name == "TryGetString"));
            _logicType = FindGeneratedType("LogicRuntime", database?.Namespace, type =>
                type.GetMethods(PublicStatic).Any(method => method.Name == "TriggerFunction"));
        }

        private static Type FindGeneratedType(string name, string preferredNamespace, Func<Type, bool> hasMembers)
        {
            var candidates = new List<Type>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types; }
                catch { continue; }
                foreach (var type in types)
                {
                    if (type == null || type.Name != name) continue;
                    try { if (hasMembers(type)) candidates.Add(type); }
                    catch { }
                }
            }
            return candidates.OrderBy(type => !string.IsNullOrEmpty(preferredNamespace) &&
                    type.Namespace == preferredNamespace ? 0 :
                    type.FullName == "Grimoire.Data." + name ? 1 :
                    (type.Namespace ?? "").StartsWith("Grimoire", StringComparison.Ordinal) ? 2 : 3)
                .ThenBy(type => type.FullName, StringComparer.Ordinal).FirstOrDefault();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset()
        {
            _searched = false;
            _library = null;
            _runtimeType = null;
            _logicType = null;
        }

        internal static void SetTestSources(IDictionary<string, object> library, Type runtime, Type logic)
        {
            _searched = true;
            _library = new Dictionary<string, object>(library, StringComparer.OrdinalIgnoreCase);
            _runtimeType = runtime;
            _logicType = logic;
        }

        internal static void ResetForTests() => Reset();

        public static bool TryResolveKey(string explicitKey, string objectKey, string name, out string resolved)
        {
            resolved = null;
            if (!string.IsNullOrWhiteSpace(explicitKey))
                return TryExact(explicitKey.Trim(), out resolved);
            if (TryExact(objectKey, out resolved)) return true;
            if (TryUniqueNormalized(objectKey, out resolved)) return true;
            return TryUniqueNormalized(name, out resolved);
        }

        private static bool TryExact(string candidate, out string key)
        {
            key = null;
            if (string.IsNullOrWhiteSpace(candidate) || !Library.ContainsKey(candidate)) return false;
            key = Library.Keys.First(existing => string.Equals(existing, candidate, StringComparison.OrdinalIgnoreCase));
            return true;
        }

        private static bool TryUniqueNormalized(string candidate, out string key)
        {
            key = null;
            if (string.IsNullOrWhiteSpace(candidate)) return false;
            var normalized = Normalize(candidate);
            var matches = Library.Keys.Where(existing =>
                Normalize(existing) == normalized || Normalize(LastSegment(existing)) == normalized).Take(2).ToArray();
            if (matches.Length != 1) return false;
            key = matches[0];
            return true;
        }

        private static string LastSegment(string value)
        {
            var slash = value.LastIndexOf('/');
            return slash < 0 ? value : value.Substring(slash + 1);
        }

        private static string Normalize(string value) => new string(
            (value ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

        public static bool TryBuildSnapshot(string key, string displayName, out GrimoireObjectSnapshot snapshot)
        {
            snapshot = null;
            if (string.IsNullOrEmpty(key) || !Library.TryGetValue(key, out var item)) return false;
            snapshot = new GrimoireObjectSnapshot
            {
                ObjectKey = key,
                Name = string.IsNullOrEmpty(displayName) ? LastSegment(key) : displayName,
            };
            foreach (var member in item.GetType().GetFields(PublicInstance))
            {
                if (member.IsStatic) continue;
                object authored;
                try { authored = member.GetValue(item); }
                catch { continue; }
                var value = ReadRuntimeValue(key, member.Name, authored);
                var field = CreateField(member.Name, value);
                if (field != null) snapshot.Fields.Add(field);
            }
            return true;
        }

        public static bool TryGetLocalizedText(string key, string fieldName, string languageCode, out string text)
        {
            text = null;
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(languageCode) ||
                !Library.TryGetValue(key, out var item)) return false;
            var member = item.GetType().GetField(fieldName, PublicInstance);
            if (member == null) return false;
            object value;
            try { value = member.GetValue(item); }
            catch { return false; }
            if (value == null) return false;
            if (value is Array array)
            {
                var parts = new List<string>();
                foreach (var element in array)
                {
                    if (!TryTranslate(element, languageCode, out var part)) return false;
                    parts.Add(part);
                }
                text = string.Join(",", parts);
                return true;
            }
            return TryTranslate(value, languageCode, out text);
        }

        private static bool TryTranslate(object value, string languageCode, out string text)
        {
            text = null;
            if (value == null || value.GetType().Name != "TranslationKey") return false;
            try
            {
                var indexer = value.GetType().GetProperty("Item", PublicInstance);
                text = indexer?.GetValue(value, new object[] { languageCode }) as string;
                return text != null;
            }
            catch { return false; }
        }

        private static GrimoireCachedField CreateField(string name, object value)
        {
            if (value == null) return null;
            var referenceArray = name.EndsWith("_ids", StringComparison.Ordinal) && value is string[];
            var referenceSingle = name.EndsWith("_id", StringComparison.Ordinal) && value is string;
            var variableArray = value is Array && IsVariableElement(value.GetType().GetElementType());
            var variableSingle = IsVariableElement(value.GetType());
            var label = referenceArray ? name.Substring(0, name.Length - 4) :
                referenceSingle ? name.Substring(0, name.Length - 3) : name;
            var field = new GrimoireCachedField
            {
                FieldId = name,
                Label = label,
                SectionTitle = "Version database",
                Kind = referenceArray || referenceSingle || variableArray || variableSingle ? "reference" :
                    value is bool ? "boolean" : IsNumber(value) ? "number" : "text",
                FieldType = referenceArray || referenceSingle ? "glossaryItem" :
                    variableArray ? "typesList" : variableSingle ? "types" : "",
                Multiple = value is Array,
            };
            if (variableArray || variableSingle)
            {
                var elements = variableArray ? ((Array)value).Cast<object>() : new[] { value };
                var ids = new List<string>();
                foreach (var element in elements)
                {
                    if (element == null) continue;
                    var id = element.GetType().GetProperty("Id")?.GetValue(element) as string;
                    var elementName = element.GetType().GetProperty("Name")?.GetValue(element) as string;
                    if (string.IsNullOrEmpty(id)) continue;
                    ids.Add(id);
                    field.References.Add(new GrimoireObjectRef { Id = id, Name = elementName ?? id });
                }
                field.Value = string.Join(",", ids);
                return field;
            }
            if (referenceArray || referenceSingle)
            {
                var keys = referenceArray ? (string[])value : new[] { (string)value };
                foreach (var key in keys)
                {
                    if (!string.IsNullOrEmpty(key)) field.References.Add(new GrimoireObjectRef
                    {
                        CodeId = key,
                        Name = LastSegment(key),
                    });
                }
                field.Value = string.Join(",", keys);
                return field;
            }
            if (value is Array array)
            {
                foreach (var element in array) field.Values.Add(Format(element));
                field.Value = string.Join(",", field.Values);
            }
            else field.Value = Format(value);
            return field;
        }

        private static object ReadRuntimeValue(string key, string field, object authored)
        {
            if (_runtimeType == null || authored == null) return authored;
            if (authored is string[] && TryGet<string[]>("TryGetStringArray", key, field, out var array)) return array;
            if (authored is bool && TryGet<bool>("TryGetBoolean", key, field, out var flag)) return flag;
            if (IsNumber(authored) && TryGet<double>("TryGetNumber", key, field, out var number)) return number;
            if (authored is string && TryGet<string>("TryGetString", key, field, out var text)) return text;
            return authored;
        }

        private static bool TryGet<T>(string method, string key, string field, out T value)
        {
            value = default;
            try
            {
                var member = _runtimeType?.GetMethod(method, PublicStatic, null,
                    new[] { typeof(string), typeof(string), typeof(T).MakeByRefType() }, null);
                if (member == null) return false;
                var args = new object[] { key, field, null };
                if (!(bool)member.Invoke(null, args)) return false;
                value = (T)args[2];
                return true;
            }
            catch { return false; }
        }

        public static bool TryWrite(string key, GrimoireCachedField field, string value)
        {
            if (string.IsNullOrEmpty(key) || field == null || _runtimeType == null ||
                !Library.TryGetValue(key, out var item)) return false;
            var name = field.FieldId;
            var authored = item.GetType().GetField(name, PublicInstance)?.GetValue(item);
            if (!(authored is string) && !(authored is string[]) &&
                !(authored is bool) && !IsNumber(authored)) return false;
            if (field.Multiple)
                return Invoke("SetStringArray", key, name,
                    value.Split(new[] { ',', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(part => part.Trim()).ToArray());
            if (field.Kind == "boolean")
            {
                if (!bool.TryParse(value, out var flag)) return false;
                return Invoke("SetBoolean", key, name, flag);
            }
            if (field.Kind == "number")
            {
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return false;
                return Invoke("SetNumber", key, name, number);
            }
            return Invoke("SetString", key, name, value);
        }

        private static bool Invoke<T>(string method, string key, string field, T value)
        {
            try
            {
                var member = _runtimeType?.GetMethod(method, PublicStatic, null,
                    new[] { typeof(string), typeof(string), typeof(T) }, null);
                if (member == null) return false;
                member.Invoke(null, new object[] { key, field, value });
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Grimoire] Version write failed: " + ex.Message);
                return false;
            }
        }

        public static bool TryTriggerFunction(string key, string functionName)
        {
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(functionName) || _logicType == null) return false;
            try
            {
                var member = _logicType.GetMethod("TriggerFunction", PublicStatic, null,
                    new[] { typeof(string), typeof(string) }, null);
                if (member == null) return false;
                member.Invoke(null, new object[] { key, functionName });
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Grimoire] Version function failed: " + ex.Message);
                return false;
            }
        }

        private static bool IsNumber(object value) => value is double || value is float ||
            value is int || value is long || value is decimal;

        private static bool IsVariableElement(Type type)
        {
            for (var current = type; current != null; current = current.BaseType)
                if (current.Name == "VariableElement") return true;
            return false;
        }

        private static string Format(object value)
        {
            if (value == null) return "";
            if (value.GetType().Name == "DocumentBlock")
            {
                var content = value.GetType().GetField("content", PublicInstance)?.GetValue(value) as string;
                if (!string.IsNullOrEmpty(content)) return content;
            }
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        }
    }
}
