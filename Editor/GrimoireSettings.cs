using UnityEditor;

namespace Grimoire.PluginV2.Editor
{
    /// <summary>
    /// Plugin configuration, stored in EditorPrefs: per-user and per-machine,
    /// so the API secret can never end up in version control. Keys are scoped
    /// by project GUID because the game id (and possibly the server) differ
    /// between projects sharing the same machine.
    /// </summary>
    public static class GrimoireSettings
    {
        public const string DefaultApiBaseUrl = "https://api.usegrimoire.com";

        private static string Key(string name) =>
            $"GrimoireV2_{PlayerSettings.productGUID}_{name}";

        public static string ApiBaseUrl
        {
            get => Normalize(EditorPrefs.GetString(Key("ApiBaseUrl"), DefaultApiBaseUrl));
            set => EditorPrefs.SetString(Key("ApiBaseUrl"), Normalize(value));
        }

        public static string GameId
        {
            get => EditorPrefs.GetString(Key("GameId"), "");
            set => EditorPrefs.SetString(Key("GameId"), value?.Trim() ?? "");
        }

        public static string ApiKey
        {
            get => EditorPrefs.GetString(Key("ApiKey"), "");
            set => EditorPrefs.SetString(Key("ApiKey"), value?.Trim() ?? "");
        }

        public static string ApiSecret
        {
            get => EditorPrefs.GetString(Key("ApiSecret"), "");
            set => EditorPrefs.SetString(Key("ApiSecret"), value?.Trim() ?? "");
        }

        /// <summary>Locale for translatable fields; empty shows source text.</summary>
        public static string Locale
        {
            get => EditorPrefs.GetString(Key("Locale"), "");
            set => EditorPrefs.SetString(Key("Locale"), value?.Trim() ?? "");
        }

        public static bool HasApiCredentials =>
            !string.IsNullOrEmpty(ApiKey) && !string.IsNullOrEmpty(ApiSecret);

        public static bool HasGameId => !string.IsNullOrEmpty(GameId);

        public static bool IsConfigured => HasApiCredentials && HasGameId;

        // Auth session storage lives here too so every consumer agrees on the keys.

        public static string SessionToken
        {
            get => EditorPrefs.GetString(Key("SessionToken"), "");
            set => EditorPrefs.SetString(Key("SessionToken"), value ?? "");
        }

        /// <summary>UTC ticks; 0 means unknown.</summary>
        public static long SessionExpiresAtTicks
        {
            get => long.TryParse(EditorPrefs.GetString(Key("SessionExpiresAt"), "0"), out var ticks) ? ticks : 0;
            set => EditorPrefs.SetString(Key("SessionExpiresAt"), value.ToString());
        }

        public static string SessionUserName
        {
            get => EditorPrefs.GetString(Key("SessionUserName"), "");
            set => EditorPrefs.SetString(Key("SessionUserName"), value ?? "");
        }

        public static string SessionUserEmail
        {
            get => EditorPrefs.GetString(Key("SessionUserEmail"), "");
            set => EditorPrefs.SetString(Key("SessionUserEmail"), value ?? "");
        }

        private static string Normalize(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return DefaultApiBaseUrl;
            }

            return url.Trim().TrimEnd('/');
        }
    }
}
