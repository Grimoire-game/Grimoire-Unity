using System;
using System.Reflection;

namespace Grimoire.PluginV2
{
    /// <summary>
    /// Shared reflection utilities used by GrimoireBootstrap and GrimoireSessionTracker.
    /// Keeps the type-lookup and delegate-wiring logic in one place so generated code
    /// under Assets/Grimoire/ can be referenced without compile-time dependencies.
    /// </summary>
    internal static class GrimoireReflect
    {
        /// <summary>
        /// Finds a type by name across all loaded assemblies, preferring types
        /// that live in the Grimoire namespace.
        /// </summary>
        public static Type FindType(string typeName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (var t in asm.GetTypes())
                    {
                        if (t.Name != typeName) continue;
                        string ns = t.Namespace ?? "";
                        if (ns.StartsWith("Grimoire", StringComparison.Ordinal) || ns == string.Empty)
                            return t;
                    }
                }
                catch { }
            }
            return null;
        }

        public static void SetStaticDelegate(Type type, string memberName, object value)
        {
            var field = type.GetField(memberName,
                BindingFlags.Public | BindingFlags.Static);
            if (field != null) { field.SetValue(null, value); return; }

            var prop = type.GetProperty(memberName,
                BindingFlags.Public | BindingFlags.Static);
            if (prop != null && prop.CanWrite) prop.SetValue(null, value);
        }

        public static bool IsRuntimeInitialized(Type runtimeType)
        {
            try
            {
                var m = runtimeType.GetMethod("IsInitialized",
                    BindingFlags.Public | BindingFlags.Static);
                return m != null && (bool)m.Invoke(null, null);
            }
            catch { return false; }
        }

        public static bool InvokeStaticBool(Type type, string methodName, string arg)
        {
            try
            {
                var m = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static,
                    null, new[] { typeof(string) }, null);
                return m != null && (bool)m.Invoke(null, new object[] { arg });
            }
            catch { return false; }
        }

        public static void InvokeStaticVoid(Type type, string methodName, string arg)
        {
            try
            {
                var m = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static,
                    null, new[] { typeof(string) }, null);
                m?.Invoke(null, new object[] { arg });
            }
            catch { }
        }
    }
}
