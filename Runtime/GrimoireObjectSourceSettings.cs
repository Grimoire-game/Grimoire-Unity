using UnityEngine;

namespace Grimoire.PluginV2
{
    /// <summary>The source used by Object Links unless a link overrides it.</summary>
    public enum GrimoireObjectDataSource
    {
        ConnectedSnapshot = 0,
        VersionDatabase = 1,
    }

    /// <summary>Per-link choice. Existing links inherit the project default.</summary>
    public enum GrimoireObjectLinkSource
    {
        ProjectDefault = 0,
        ConnectedSnapshot = 1,
        VersionDatabase = 2,
    }

    /// <summary>
    /// Build-safe project setting. Place the asset at
    /// Assets/Resources/GrimoireObjectSourceSettings.asset so it is available
    /// both in the editor and in player builds.
    /// </summary>
    public sealed class GrimoireObjectSourceSettings : ScriptableObject
    {
        public const string ResourcesName = "GrimoireObjectSourceSettings";

        [SerializeField]
        private GrimoireObjectDataSource _defaultSource = GrimoireObjectDataSource.ConnectedSnapshot;

        public GrimoireObjectDataSource DefaultSource
        {
            get => _defaultSource;
            set => _defaultSource = value;
        }
    }

    public static class GrimoireObjectSources
    {
        private static GrimoireObjectSourceSettings _settings;

        public static GrimoireObjectSourceSettings Settings
        {
            get
            {
                if (_settings == null)
                    _settings = Resources.Load<GrimoireObjectSourceSettings>(
                        GrimoireObjectSourceSettings.ResourcesName);
                return _settings;
            }
            set => _settings = value;
        }

        public static GrimoireObjectDataSource ProjectDefault =>
            Settings != null ? Settings.DefaultSource : GrimoireObjectDataSource.ConnectedSnapshot;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset() => _settings = null;
    }
}
