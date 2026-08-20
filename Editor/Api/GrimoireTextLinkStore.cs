using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Grimoire.PluginV2.Editor
{
    /// <summary>
    /// Keeps <see cref="GrimoireTextLink"/> copy in sync with GET /api/v1/strings
    /// and builds PATCH payloads for deviated translations.
    /// </summary>
    public static class GrimoireTextLinkStore
    {
        public static event Action Changed;

        public sealed class DirtyTranslationChange
        {
            public string LanguageCode;
            public string Label;
            public string Previous;
            public string Current;
        }

        public static void ApplyFromResource(
            GrimoireTextLink link,
            StringResource resource,
            bool preserveLocalEdits = true)
        {
            if (link == null || resource == null)
            {
                return;
            }

            var previousByLang = new Dictionary<string, GrimoireLinkedTranslation>(StringComparer.OrdinalIgnoreCase);
            foreach (var existing in link.Translations)
            {
                if (existing == null || string.IsNullOrEmpty(existing.LanguageCode))
                {
                    continue;
                }

                previousByLang[existing.LanguageCode] = existing;
            }

            Undo.RecordObject(link, "Update Grimoire text link");
            link.CachedStringId = resource.id;
            link.GrimoireSourceText = resource.source_text ?? "";
            link.SourceText = preserveLocalEdits && link.IsSourceDeviated
                ? link.SourceText
                : link.GrimoireSourceText;
            link.CharacterLimit = resource.character_limit ?? 0;

            var next = new List<GrimoireLinkedTranslation>();
            if (resource.translations != null)
            {
                foreach (var row in resource.translations)
                {
                    if (row == null || string.IsNullOrEmpty(row.language_code))
                    {
                        continue;
                    }

                    previousByLang.TryGetValue(row.language_code, out var prior);
                    var baseline = row.translated_text ?? "";
                    next.Add(new GrimoireLinkedTranslation
                    {
                        LanguageCode = row.language_code,
                        GrimoireText = baseline,
                        LocalText = preserveLocalEdits && prior != null && prior.IsDeviated
                            ? prior.LocalText
                            : baseline,
                    });
                }
            }

            link.ReplaceTranslations(next);
            link.ApplyResolvedText(ResolvePreviewLanguage(link));
            EditorUtility.SetDirty(link);
            NotifyChanged();
        }

        public static bool HasDeviations(GrimoireTextLink link) =>
            link != null && (link.HasTranslationDeviations || link.IsSourceDeviated);

        public static bool HasPushableDeviations(GrimoireTextLink link) =>
            link != null && link.HasTranslationDeviations;

        public static List<DirtyTranslationChange> GetDeviations(GrimoireTextLink link)
        {
            var list = new List<DirtyTranslationChange>();
            if (link == null)
            {
                return list;
            }

            if (link.IsSourceDeviated)
            {
                list.Add(new DirtyTranslationChange
                {
                    LanguageCode = "(source)",
                    Label = "Source text",
                    Previous = link.GrimoireSourceText ?? "",
                    Current = link.SourceText ?? "",
                });
            }

            foreach (var row in link.Translations)
            {
                if (row == null || !row.IsDeviated)
                {
                    continue;
                }

                list.Add(new DirtyTranslationChange
                {
                    LanguageCode = row.LanguageCode,
                    Label = row.LanguageCode,
                    Previous = row.GrimoireText ?? "",
                    Current = row.LocalText ?? "",
                });
            }

            return list;
        }

        public static bool SetSourceText(GrimoireTextLink link, string value)
        {
            if (link == null)
            {
                return false;
            }

            var next = Clamp(link, value ?? "");
            if (string.Equals(link.SourceText, next, StringComparison.Ordinal))
            {
                return false;
            }

            Undo.RecordObject(link, "Edit Grimoire source text");
            link.SourceText = next;
            link.ApplyResolvedText(ResolvePreviewLanguage(link));
            EditorUtility.SetDirty(link);
            NotifyChanged();
            return true;
        }

        public static bool SetTranslationText(GrimoireTextLink link, string languageCode, string value)
        {
            if (link == null || string.IsNullOrEmpty(languageCode) ||
                !link.TryGetTranslation(languageCode, out var row))
            {
                return false;
            }

            var next = Clamp(link, value ?? "");
            if (string.Equals(row.LocalText, next, StringComparison.Ordinal))
            {
                return false;
            }

            Undo.RecordObject(link, "Edit Grimoire translation");
            row.LocalText = next;
            link.ApplyResolvedText(ResolvePreviewLanguage(link));
            EditorUtility.SetDirty(link);
            NotifyChanged();
            return true;
        }

        public static bool ResetToGrimoire(GrimoireTextLink link)
        {
            if (link == null || !HasDeviations(link))
            {
                return false;
            }

            Undo.RecordObject(link, "Reset Grimoire text link");
            link.SourceText = link.GrimoireSourceText ?? "";
            foreach (var row in link.Translations)
            {
                if (row == null || !row.IsDeviated)
                {
                    continue;
                }

                row.LocalText = row.GrimoireText ?? "";
            }

            link.ApplyResolvedText(ResolvePreviewLanguage(link));
            EditorUtility.SetDirty(link);
            NotifyChanged();
            return true;
        }

        public static void AcceptSubmitted(GrimoireTextLink link)
        {
            if (link?.Translations == null)
            {
                return;
            }

            Undo.RecordObject(link, "Accept Grimoire text submit");
            foreach (var row in link.Translations)
            {
                if (row == null || !row.IsDeviated)
                {
                    continue;
                }

                row.GrimoireText = row.LocalText ?? "";
            }

            EditorUtility.SetDirty(link);
            NotifyChanged();
        }

        public static async Task<ApiResult<StringResource>> RefreshFromGrimoireAsync(
            GrimoireTextLink link,
            bool preserveLocalEdits = true)
        {
            if (link == null)
            {
                return ApiResult<StringResource>.Fail("Missing text link.", "missing_parameter");
            }

            if (!GrimoireSettings.IsConfigured)
            {
                return ApiResult<StringResource>.Fail(
                    "Sign in and choose a workspace first (Window > Grimoire > Grimoire Connect).",
                    "not_configured");
            }

            if (!link.HasCode)
            {
                return ApiResult<StringResource>.Fail(
                    "This Grimoire Text Link has no string code.",
                    "missing_parameter");
            }

            await GrimoireAuthSession.EnsureFreshTokenAsync();

            var resolved = await GrimoireStringKeyResolver.ResolveAsync(GrimoireSettings.GameId, link.TextCode);
            if (!resolved.Success)
            {
                return resolved;
            }

            GrimoireStringKeyResolver.Remember(GrimoireSettings.GameId, resolved.Data);
            ApplyFromResource(link, resolved.Data, preserveLocalEdits);
            return resolved;
        }

        public static void CollectSceneLinks(List<GrimoireTextLink> into)
        {
            if (into == null)
            {
                return;
            }

            into.Clear();
            var found = Resources.FindObjectsOfTypeAll<GrimoireTextLink>();
            foreach (var link in found)
            {
                if (link == null || link.gameObject == null || EditorUtility.IsPersistent(link))
                {
                    continue;
                }

                var scene = link.gameObject.scene;
                if (!scene.IsValid() || !scene.isLoaded)
                {
                    continue;
                }

                if (!link.HasCode && string.IsNullOrEmpty(link.CachedStringId))
                {
                    continue;
                }

                into.Add(link);
            }
        }

        public static string ResolvePreviewLanguage(GrimoireTextLink link)
        {
            if (link != null && !string.IsNullOrWhiteSpace(link.PreviewLanguage))
            {
                return link.PreviewLanguage.Trim();
            }

            return GrimoireSettings.Locale;
        }

        private static string Clamp(GrimoireTextLink link, string value)
        {
            if (link == null || link.CharacterLimit <= 0 || value.Length <= link.CharacterLimit)
            {
                return value;
            }

            return value.Substring(0, link.CharacterLimit);
        }

        private static void NotifyChanged() => Changed?.Invoke();
    }
}
