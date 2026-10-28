using UnityEditor;
using UnityEngine;

namespace Grimoire.PluginV2.Editor
{
    internal static class GrimoireObjectSourceSettingsStore
    {
        public const string AssetPath = "Assets/Resources/GrimoireObjectSourceSettings.asset";

        public static GrimoireObjectSourceSettings Find() =>
            AssetDatabase.LoadAssetAtPath<GrimoireObjectSourceSettings>(AssetPath);

        public static GrimoireObjectSourceSettings GetOrCreate()
        {
            var settings = Find();
            if (settings != null) return settings;
            if (!AssetDatabase.IsValidFolder("Assets/Resources"))
                AssetDatabase.CreateFolder("Assets", "Resources");
            settings = ScriptableObject.CreateInstance<GrimoireObjectSourceSettings>();
            AssetDatabase.CreateAsset(settings, AssetPath);
            AssetDatabase.SaveAssets();
            return settings;
        }

        public static void SetDefault(GrimoireObjectDataSource source)
        {
            var settings = GetOrCreate();
            Undo.RecordObject(settings, "Change Grimoire object source");
            settings.DefaultSource = source;
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
            GrimoireObjectSources.Settings = settings;
        }

        private static void SelectAsset()
        {
            var asset = GetOrCreate();
            Selection.activeObject = asset;
            EditorGUIUtility.PingObject(asset);
        }
    }
}
