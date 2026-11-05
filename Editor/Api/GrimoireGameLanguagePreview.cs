using System;
using System.Collections.Generic;
using UnityEditor;

namespace Grimoire.PluginV2.Editor
{
    /// <summary>
    /// Editor preview of the selected game's language. The string catalog comes
    /// from the Public API; changing the language refreshes linked scene data.
    /// </summary>
    [InitializeOnLoad]
    public static class GrimoireGameLanguagePreview
    {
        private static GrimoireTranslationCatalog _catalog;
        private static string _gameId;
        private static string _attemptedGameId;
        private static int _generation;
        private static bool _loading;
        private static string _error;

        static GrimoireGameLanguagePreview()
        {
            GrimoireSettings.Changed += OnWorkspaceChanged;
            GrimoireSettings.LocaleChanged += Refresh;
        }

        public static event Action Changed;
        public static bool IsLoading => _loading;
        public static string Error => _error;
        public static bool IsLoaded => _catalog != null && _gameId == GrimoireSettings.GameId;
        public static int StringCount => IsLoaded ? _catalog.Strings.Count : 0;

        public static void EnsureLoaded()
        {
            if (!_loading && !IsLoaded && GrimoireSettings.IsConfigured &&
                _attemptedGameId != GrimoireSettings.GameId)
                Refresh();
        }

        public static void Refresh()
        {
            if (!GrimoireSettings.IsConfigured) return;
            _attemptedGameId = GrimoireSettings.GameId;
            RefreshAsync(GrimoireSettings.GameId, ++_generation);
        }

        public static string DisplayText(string stringId, string sourceText)
        {
            if (!IsLoaded || !_catalog.TryResolve(stringId, out var resource))
                return sourceText ?? "";
            return GrimoireTranslationCatalog.DisplayText(resource, GrimoireSettings.Locale, sourceText);
        }

        private static void OnWorkspaceChanged()
        {
            if (_gameId == GrimoireSettings.GameId) return;
            ++_generation;
            _catalog = null;
            _gameId = null;
            _attemptedGameId = null;
            _loading = false;
            _error = null;
            Changed?.Invoke();
            // SelectWorkspace raises several Changed events. Coalesce them.
            EditorApplication.delayCall -= EnsureLoaded;
            EditorApplication.delayCall += EnsureLoaded;
        }

        private static async void RefreshAsync(string gameId, int generation)
        {
            _loading = true;
            _error = null;
            Changed?.Invoke();
            try
            {
                if (!await GrimoireAuthSession.EnsureFreshTokenAsync())
                {
                    Finish(generation, gameId, "Your Grimoire session expired. Sign in again.");
                    return;
                }

                var result = await GrimoireTranslationCatalog.LoadAsync(gameId);
                if (!IsCurrent(generation, gameId)) return;
                if (!result.Success)
                {
                    Finish(generation, gameId, result.Error);
                    return;
                }

                _catalog = result.Data;
                _gameId = gameId;
                GrimoireStringKeyResolver.Invalidate(gameId);
                foreach (var resource in _catalog.Strings)
                    GrimoireStringKeyResolver.Remember(gameId, resource);
                Changed?.Invoke();

                if (!EditorApplication.isPlayingOrWillChangePlaymode)
                    await RefreshSceneLinks(generation, gameId);

                Finish(generation, gameId, null);
            }
            catch (Exception ex)
            {
                Finish(generation, gameId, ex.Message);
            }
        }

        private static async System.Threading.Tasks.Task RefreshSceneLinks(int generation, string gameId)
        {
            var errors = new List<string>();
            var texts = new List<GrimoireTextLink>();
            GrimoireTextLinkStore.CollectSceneLinks(texts);
            foreach (var link in texts)
            {
                if (!IsCurrent(generation, gameId)) return;
                if (link == null) continue;
                var key = !string.IsNullOrEmpty(link.TextCode) ? link.TextCode : link.CachedStringId;
                if (_catalog.TryResolve(key, out var resource) ||
                    _catalog.TryResolve(link.CachedStringId, out resource))
                    GrimoireTextLinkStore.ApplyFromResource(link, resource, preserveLocalEdits: true);
                else
                    errors.Add($"string '{key}'");
            }

            var objects = new List<GrimoireObjectLink>();
            GrimoireGameEngineDirtyTracker.CollectSceneLinks(objects);
            foreach (var link in objects)
            {
                if (!IsCurrent(generation, gameId)) return;
                if (link == null || !link.HasKey) continue;
                var result = await GrimoireLinkedFieldStore.RefreshFromGrimoireAsync(
                    link, preserveLocalEdits: true, catalog: _catalog);
                if (!result.Success) errors.Add($"object '{link.ObjectKey}' ({result.Error})");
            }

            if (errors.Count > 0 && IsCurrent(generation, gameId))
                _error = "Some scene links could not be refreshed: " + string.Join(", ", errors);
        }

        private static bool IsCurrent(int generation, string gameId) =>
            generation == _generation && gameId == GrimoireSettings.GameId;

        private static void Finish(int generation, string gameId, string error)
        {
            if (!IsCurrent(generation, gameId)) return;
            _loading = false;
            if (!string.IsNullOrEmpty(error)) _error = error;
            Changed?.Invoke();
        }
    }
}
