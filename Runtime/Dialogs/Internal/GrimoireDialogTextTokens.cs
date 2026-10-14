using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Grimoire.PluginV2.Internal
{
    /// <summary>
    /// Inline object-field tokens in dialog text, written by Grimoire as
    /// <c>#object:objectId;fieldId#</c>. The text shows the field's value there.
    /// </summary>
    public static class GrimoireDialogTextTokens
    {
        private const string ObjectPrefix = "#object:";

        private static readonly Regex ObjectToken = new Regex(
            @"#object:([^;#\s]+);([^#\s]+)#",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static bool HasTokens(string text)
        {
            return !string.IsNullOrEmpty(text) && text.IndexOf(ObjectPrefix, StringComparison.Ordinal) >= 0;
        }

        /// <summary>Adds an object-field reference to <paramref name="into"/> for every token in <paramref name="text"/>.</summary>
        public static void Collect(string text, List<GrimoireDialogValueRef> into)
        {
            if (into == null || !HasTokens(text))
            {
                return;
            }

            foreach (Match match in ObjectToken.Matches(text))
            {
                var objectId = match.Groups[1].Value;
                var fieldId = match.Groups[2].Value;
                if (Find(into, objectId, fieldId) == null)
                {
                    into.Add(NewRef(objectId, fieldId));
                }
            }
        }

        /// <summary>Replaces every token with <paramref name="read"/>'s text for its reference.</summary>
        public static string Replace(string text, List<GrimoireDialogValueRef> refs, Func<GrimoireDialogValueRef, string> read)
        {
            if (!HasTokens(text) || read == null)
            {
                return text ?? "";
            }

            return ObjectToken.Replace(text, match =>
            {
                var objectId = match.Groups[1].Value;
                var fieldId = match.Groups[2].Value;
                return read(Find(refs, objectId, fieldId) ?? NewRef(objectId, fieldId)) ?? "";
            });
        }

        private static GrimoireDialogValueRef Find(List<GrimoireDialogValueRef> refs, string objectId, string fieldId)
        {
            if (refs == null)
            {
                return null;
            }

            foreach (var reference in refs)
            {
                if (reference != null &&
                    string.Equals(reference.objectId, objectId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(reference.fieldId, fieldId, StringComparison.OrdinalIgnoreCase))
                {
                    return reference;
                }
            }

            return null;
        }

        private static GrimoireDialogValueRef NewRef(string objectId, string fieldId)
        {
            return new GrimoireDialogValueRef
            {
                source = GrimoireDialogValueRef.SourceObjectField,
                objectId = objectId,
                fieldId = fieldId,
            };
        }
    }
}
