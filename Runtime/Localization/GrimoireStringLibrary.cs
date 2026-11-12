using System;
using System.Collections.Generic;
using UnityEngine;

namespace Grimoire.PluginV2
{
    [Serializable]
    public sealed class GrimoireStringEntry
    {
        [SerializeField] private string _id = "";
        [SerializeField] private string _code = "";
        [SerializeField] private string _shortCode = "";
        [SerializeField] private string _sourceText = "";
        [SerializeField] private List<GrimoireLocalizedText> _translations = new List<GrimoireLocalizedText>();

        public string Id => _id;
        public string Code => _code;
        public string ShortCode => _shortCode;
        public string SourceText => _sourceText;
        public IReadOnlyList<GrimoireLocalizedText> Translations => _translations;

        internal GrimoireStringEntry(string id, string code, string shortCode, string sourceText,
            List<GrimoireLocalizedText> translations)
        {
            _id = id ?? "";
            _code = code ?? "";
            _shortCode = shortCode ?? "";
            _sourceText = sourceText ?? "";
            _translations = translations ?? new List<GrimoireLocalizedText>();
        }

        public string GetText(string languageCode = null) =>
            GrimoireLocalization.Resolve(_sourceText, _translations, languageCode);
    }

    /// <summary>Strings downloaded from the selected game for use in Unity builds.</summary>
    public sealed class GrimoireStringLibrary : ScriptableObject
    {
        public const string ResourcesName = "GrimoireStrings";

        [SerializeField] private string _gameId = "";
        [SerializeField] private string _syncedAt = "";
        [SerializeField] private List<GrimoireStringEntry> _strings = new List<GrimoireStringEntry>();

        [NonSerialized] private Dictionary<string, GrimoireStringEntry> _index;
        [NonSerialized] private HashSet<string> _ambiguous;

        public string GameId => _gameId;
        public string SyncedAt => _syncedAt;
        public IReadOnlyList<GrimoireStringEntry> Strings => _strings;

        internal void SetSyncedData(string gameId, List<GrimoireStringEntry> strings)
        {
            _gameId = gameId ?? "";
            _syncedAt = DateTime.UtcNow.ToString("o");
            _strings = strings ?? new List<GrimoireStringEntry>();
            _index = null;
            _ambiguous = null;
        }

        public bool TryGet(string idOrCode, out GrimoireStringEntry entry)
        {
            entry = null;
            if (string.IsNullOrWhiteSpace(idOrCode)) return false;
            EnsureIndex();
            return _index.TryGetValue(idOrCode.Trim(), out entry);
        }

        private void EnsureIndex()
        {
            if (_index != null) return;
            _index = new Dictionary<string, GrimoireStringEntry>(StringComparer.OrdinalIgnoreCase);
            _ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in _strings)
            {
                if (entry == null) continue;
                Add(entry.Id, entry);
                Add(entry.Code, entry);
                Add(entry.ShortCode, entry);
            }
        }

        private void Add(string key, GrimoireStringEntry entry)
        {
            if (string.IsNullOrWhiteSpace(key) || _ambiguous.Contains(key)) return;
            if (_index.TryGetValue(key, out var existing) && existing != entry)
            {
                _index.Remove(key);
                _ambiguous.Add(key);
            }
            else _index[key] = entry;
        }
    }

    /// <summary>Read any synced Grimoire string by its Loca key or ID.</summary>
    public static class GrimoireStrings
    {
        private static GrimoireStringLibrary _library;
        private static bool _searched;

        public static GrimoireStringLibrary Library
        {
            get
            {
                if (_library == null && !_searched)
                {
                    _searched = true;
                    _library = Resources.Load<GrimoireStringLibrary>(GrimoireStringLibrary.ResourcesName);
                }
                return _library;
            }
            set
            {
                _library = value;
                _searched = true;
            }
        }

        public static bool TryGet(string idOrCode, out string text, string languageCode = null)
        {
            text = null;
            if (Library == null || !Library.TryGet(idOrCode, out var entry)) return false;
            text = entry.GetText(languageCode);
            return true;
        }

        public static string Get(string idOrCode, string languageCode = null) =>
            TryGet(idOrCode, out var text, languageCode) ? text : idOrCode ?? "";

        public static void Reload()
        {
            _library = null;
            _searched = false;
        }
    }
}
