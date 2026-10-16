using System;
using System.Collections.Generic;
using UnityEngine;

namespace Grimoire.PluginV2
{
    /// <summary>
    /// The game's Grimoire variables from any script. Sync them once in
    /// Grimoire Connect &gt; Dialogs &gt; Sync variables (dialog imports sync them too).
    ///
    /// <code>
    /// // Object fields of type "types" store an element id:
    /// if (link.TryGetVariableElement("Mood", out var mood))
    ///     Debug.Log(mood.Name);                          // "Angry"
    ///
    /// GrimoireVariables.ElementName("d8797c69-...");     // "Angry"
    /// GrimoireVariables.TryFindElement("Mood", "Angry", out var angry);
    /// GrimoireVariables.GetValue&lt;float&gt;("Difficulty", "Easy");
    /// </code>
    ///
    /// Values change at runtime when a dialog setter writes to a variable element
    /// (or you call <see cref="SetValue"/>); the synced asset itself is never modified.
    /// </summary>
    public static class GrimoireVariables
    {
        private static GrimoireVariableLibrary _library;
        private static bool _searched;
        private static readonly Dictionary<string, object> RuntimeValues =
            new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        /// <summary>An element's runtime value changed (element, new value).</summary>
        public static event Action<GrimoireVariableElement, object> ValueChanged;

        /// <summary>
        /// The synced variables. Loaded from Resources on first use; assign your own
        /// library to override it (e.g. in tests).
        /// </summary>
        public static GrimoireVariableLibrary Library
        {
            get
            {
                if (_library == null && !_searched)
                {
                    _searched = true;
                    _library = Resources.Load<GrimoireVariableLibrary>(GrimoireVariableLibrary.ResourcesName);
                }

                return _library;
            }
            set
            {
                _library = value;
                _searched = true;
            }
        }

        /// <summary>True when variables have been synced into the project.</summary>
        public static bool IsAvailable => Library != null;

        public static IReadOnlyList<GrimoireVariable> All =>
            Library != null ? Library.Variables : Array.Empty<GrimoireVariable>();

        /// <summary>A variable by id or name, e.g. Find("Mood").</summary>
        public static GrimoireVariable Find(string idOrName)
        {
            return Library != null ? Library.FindVariable(idOrName) : null;
        }

        public static bool TryGetElement(string elementId, out GrimoireVariableElement element)
        {
            element = null;
            return Library != null && Library.TryGetElement(elementId, out element);
        }

        /// <summary>An element by variable and element name (or ids), e.g. ("Mood", "Angry").</summary>
        public static bool TryFindElement(string variable, string element, out GrimoireVariableElement found)
        {
            found = Find(variable)?.FindElement(element);
            return found != null;
        }

        /// <summary>The element's name, e.g. "Angry". Returns <paramref name="elementId"/> when it is unknown.</summary>
        public static string ElementName(string elementId)
        {
            return TryGetElement(elementId, out var element) && !string.IsNullOrEmpty(element.DisplayName)
                ? element.DisplayName
                : elementId ?? "";
        }

        /// <summary>
        /// The element's value now: what a dialog setter or <see cref="SetValue"/> wrote,
        /// else the value authored in Grimoire. Elements without a value (like "Angry"
        /// in a "type" variable) return their id.
        /// </summary>
        public static object GetValue(string elementId)
        {
            if (string.IsNullOrEmpty(elementId))
            {
                return null;
            }

            if (RuntimeValues.TryGetValue(elementId, out var value))
            {
                return value;
            }

            if (!TryGetElement(elementId, out var element))
            {
                return null;
            }

            return element.AuthoredValue ?? element.Id;
        }

        /// <summary>The element's value converted to <typeparamref name="T"/>, e.g. GetValue&lt;float&gt;("Difficulty", "Easy").</summary>
        public static T GetValue<T>(string variable, string element)
        {
            return TryFindElement(variable, element, out var found)
                ? Internal.GrimoireDialogConditions.ConvertTo<T>(GetValue(found.Id))
                : default;
        }

        /// <summary>Change an element's value for this play session.</summary>
        public static void SetValue(string elementId, object value)
        {
            if (string.IsNullOrEmpty(elementId))
            {
                return;
            }

            RuntimeValues[elementId] = value;
            TryGetElement(elementId, out var element);
            ValueChanged?.Invoke(element, value);
        }

        /// <summary>Drop runtime changes so every element reads its Grimoire value again.</summary>
        public static void ResetValues()
        {
            RuntimeValues.Clear();
        }

        /// <summary>Forget the loaded library so the next call reloads it (after a sync).</summary>
        internal static void Reload()
        {
            _library = null;
            _searched = false;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            Reload();
            RuntimeValues.Clear();
            ValueChanged = null;
        }
    }
}
