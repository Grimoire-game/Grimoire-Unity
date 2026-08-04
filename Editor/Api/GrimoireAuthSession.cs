using System;
using System.Threading.Tasks;
using System.Text.RegularExpressions;

namespace Grimoire.PluginV2.Editor
{
    /// <summary>
    /// The signed-in Grimoire user session (bearer JWT). Used for all API
    /// calls in the plugin — reads, game discovery, workflow statuses, and
    /// task updates. Token and identity are stored via
    /// <see cref="GrimoireSettings"/> (EditorPrefs).
    /// </summary>
    public static class GrimoireAuthSession
    {
        /// <summary>Refresh when the token has less than this much life left.</summary>
        private static readonly TimeSpan RefreshMargin = TimeSpan.FromHours(1);

        public static event Action Changed;

        public static bool IsSignedIn => !string.IsNullOrEmpty(GrimoireSettings.SessionToken);

        public static string UserName => GrimoireSettings.SessionUserName;

        public static DateTime ExpiresAtUtc
        {
            get
            {
                var ticks = GrimoireSettings.SessionExpiresAtTicks;
                return ticks > 0 ? new DateTime(ticks, DateTimeKind.Utc) : DateTime.MinValue;
            }
        }

        public static void ApplySession(AuthSessionData session)
        {
            GrimoireSettings.SessionToken = session.token;
            GrimoireSettings.SessionExpiresAtTicks =
                (DateTime.UtcNow + ParseLifetime(session.expires_in)).Ticks;
            GrimoireSettings.SessionUserName = session.user?.DisplayName ?? "";
            GrimoireSettings.SessionUserEmail = session.user?.email ?? "";
            Changed?.Invoke();
        }

        public static void SignOut()
        {
            GrimoireSettings.SessionToken = "";
            GrimoireSettings.SessionExpiresAtTicks = 0;
            GrimoireSettings.SessionUserName = "";
            GrimoireSettings.SessionUserEmail = "";
            Changed?.Invoke();
        }

        /// <summary>
        /// Refreshes the JWT when it is missing margin or already expired (the
        /// refresh endpoint accepts expired tokens). Call before any bearer
        /// request. Returns false when there is no usable session afterwards.
        /// </summary>
        public static async Task<bool> EnsureFreshTokenAsync()
        {
            if (!IsSignedIn)
            {
                return false;
            }

            var expiresAt = ExpiresAtUtc;
            if (expiresAt != DateTime.MinValue && DateTime.UtcNow + RefreshMargin < expiresAt)
            {
                return true;
            }

            var result = await GrimoireApiClient.RefreshAsync();
            if (result.Success && !string.IsNullOrEmpty(result.Data?.token))
            {
                ApplySession(result.Data);
                return true;
            }

            // A refresh rejected with 401 means the session is dead; keeping
            // the token around would make every task update fail confusingly.
            if (result.HttpStatus == 401)
            {
                SignOut();
                return false;
            }

            // Transient failure (network, 5xx): keep the session, let the
            // actual request try with the current token.
            return true;
        }

        /// <summary>
        /// `expires_in` is a duration string such as "24h", "30m" or "3600s".
        /// Unknown formats fall back to 24h — being wrong here only shifts when
        /// the next refresh happens.
        /// </summary>
        private static TimeSpan ParseLifetime(string expiresIn)
        {
            if (!string.IsNullOrWhiteSpace(expiresIn))
            {
                var match = Regex.Match(expiresIn.Trim(), @"^(\d+)\s*([smhd]?)$", RegexOptions.IgnoreCase);
                if (match.Success && long.TryParse(match.Groups[1].Value, out var amount))
                {
                    switch (match.Groups[2].Value.ToLowerInvariant())
                    {
                        case "s": return TimeSpan.FromSeconds(amount);
                        case "m": return TimeSpan.FromMinutes(amount);
                        case "d": return TimeSpan.FromDays(amount);
                        case "h":
                        case "": return TimeSpan.FromHours(amount);
                    }
                }
            }

            return TimeSpan.FromHours(24);
        }
    }
}
