using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEditor;

namespace Grimoire.PluginV2.Editor
{
    /// <summary>
    /// Pushes deviated translations on scene text links through
    /// PATCH /api/v1/strings/{id}/translations/{language_code}.
    /// </summary>
    public static class GrimoireTextSync
    {
        public static event Action TextPushed;

        public sealed class PushFailure
        {
            public GrimoireTextLink Link;
            public string LanguageCode;
            public string Error;
        }

        public static async Task<ApiResult<int>> PushLinksAsync(IReadOnlyList<GrimoireTextLink> links)
        {
            if (!GrimoireSettings.IsConfigured)
            {
                return ApiResult<int>.Fail(
                    "Sign in and choose a workspace first (Window > Grimoire > Grimoire Connect).",
                    "not_configured");
            }

            if (links == null || links.Count == 0)
            {
                return ApiResult<int>.Fail("No text links to push.", "missing_parameter");
            }

            await GrimoireAuthSession.EnsureFreshTokenAsync();

            var gameId = GrimoireSettings.GameId;
            var pushed = 0;
            var failures = new List<PushFailure>();

            foreach (var link in links)
            {
                if (link == null || !GrimoireTextLinkStore.HasPushableDeviations(link))
                {
                    continue;
                }

                if (string.IsNullOrEmpty(link.CachedStringId))
                {
                    var resolved = await GrimoireTextLinkStore.RefreshFromGrimoireAsync(link, preserveLocalEdits: true);
                    if (!resolved.Success)
                    {
                        failures.Add(new PushFailure
                        {
                            Link = link,
                            LanguageCode = "",
                            Error = resolved.Error,
                        });
                        continue;
                    }
                }

                foreach (var row in link.Translations)
                {
                    if (row == null || !row.IsDeviated || string.IsNullOrEmpty(row.LanguageCode))
                    {
                        continue;
                    }

                    var patch = await GrimoireApiClient.PatchStringTranslationAsync(
                        gameId,
                        link.CachedStringId,
                        row.LanguageCode,
                        row.LocalText ?? "",
                        approved: true);
                    if (!patch.Success)
                    {
                        failures.Add(new PushFailure
                        {
                            Link = link,
                            LanguageCode = row.LanguageCode,
                            Error = patch.Error,
                        });
                        continue;
                    }

                    row.GrimoireText = row.LocalText ?? "";
                    pushed++;
                }

                EditorUtility.SetDirty(link);
                if (!GrimoireTextLinkStore.HasPushableDeviations(link))
                {
                    GrimoireTextLinkStore.AcceptSubmitted(link);
                }
            }

            if (pushed > 0)
            {
                TextPushed?.Invoke();
            }

            if (failures.Count > 0)
            {
                var first = failures[0];
                var label = string.IsNullOrEmpty(first.LanguageCode)
                    ? first.Link?.gameObject.name
                    : $"{first.Link?.gameObject.name} ({first.LanguageCode})";
                return ApiResult<int>.Fail(
                    $"{label}: {first.Error}",
                    "push_failed");
            }

            if (pushed == 0)
            {
                return ApiResult<int>.Fail("Nothing to push.", "missing_parameter");
            }

            return ApiResult<int>.Ok(pushed);
        }
    }
}
