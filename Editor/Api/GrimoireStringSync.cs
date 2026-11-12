using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Grimoire.PluginV2.Editor
{
    internal static class GrimoireStringSync
    {
        private const string Folder = "Assets/GrimoireStrings";
        private const string ResourcesFolder = Folder + "/Resources";
        private const string AssetPath = ResourcesFolder + "/GrimoireStrings.asset";

        internal static bool HasLibraryFor(string gameId)
        {
            var library = AssetDatabase.LoadAssetAtPath<GrimoireStringLibrary>(AssetPath);
            return library != null && library.GameId == gameId;
        }

        internal static int Apply(GrimoireTranslationCatalog catalog)
        {
            if (catalog == null) return 0;
            if (!AssetDatabase.IsValidFolder(Folder)) AssetDatabase.CreateFolder("Assets", "GrimoireStrings");
            if (!AssetDatabase.IsValidFolder(ResourcesFolder))
                AssetDatabase.CreateFolder(Folder, "Resources");

            var library = AssetDatabase.LoadAssetAtPath<GrimoireStringLibrary>(AssetPath);
            if (library == null)
            {
                library = ScriptableObject.CreateInstance<GrimoireStringLibrary>();
                AssetDatabase.CreateAsset(library, AssetPath);
            }

            var entries = new List<GrimoireStringEntry>();
            foreach (var row in catalog.Strings)
            {
                if (row == null || string.IsNullOrEmpty(row.id)) continue;
                var code = GrimoireStringPickerWindow.DisplayCode(row);
                entries.Add(new GrimoireStringEntry(row.id, code,
                    row.abbrev, row.source_text, catalog.GetTranslations(row.id)));
            }

            library.SetSyncedData(catalog.GameId, entries);
            EditorUtility.SetDirty(library);
            GrimoireStrings.Library = library;
            return entries.Count;
        }
    }
}
