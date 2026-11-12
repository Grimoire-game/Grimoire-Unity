using System;
using UnityEditor;
using UnityEngine;

namespace Grimoire.PluginV2.Editor
{
    /// <summary>Runs one full pull when a signed-in game becomes active and shares its progress with editor views.</summary>
    [InitializeOnLoad]
    internal static class GrimoireContentSync
    {
        private static string SessionKey =>
            $"GrimoireV2_{PlayerSettings.productGUID}_InitialContentSync";

        private static string _runningContext;

        public static event Action Changed;
        public static event Action Completed;

        public static bool IsRunning { get; private set; }
        public static float Progress { get; private set; }
        public static string Message { get; private set; }
        public static string Error { get; private set; }
        public static string Summary { get; private set; }

        static GrimoireContentSync()
        {
            GrimoireAuthSession.Changed += OnContextChanged;
            GrimoireSettings.Changed += OnContextChanged;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            EditorApplication.delayCall += CheckInitialSync;
        }

        public static void PullNow()
        {
            if (IsRunning || !GrimoireSettings.IsConfigured ||
                EditorApplication.isPlayingOrWillChangePlaymode) return;
            var context = CurrentContext();
            SessionState.SetString(SessionKey, context);
            StartSync(context);
        }

        private static void OnContextChanged()
        {
            if (!GrimoireSettings.IsConfigured)
            {
                SessionState.EraseString(SessionKey);
                Changed?.Invoke();
                return;
            }

            EditorApplication.delayCall -= CheckInitialSync;
            EditorApplication.delayCall += CheckInitialSync;
        }

        private static void CheckInitialSync()
        {
            if (!GrimoireSettings.IsConfigured || EditorApplication.isPlayingOrWillChangePlaymode) return;
            var context = CurrentContext();
            if (IsRunning || SessionState.GetString(SessionKey, "") == context &&
                GrimoireStringSync.HasLibraryFor(GrimoireSettings.GameId)) return;
            SessionState.SetString(SessionKey, context);
            StartSync(context);
        }

        private static string CurrentContext() =>
            GrimoireSettings.ApiBaseUrl + "|" + GrimoireAuthSession.UserId + "|" +
            GrimoireSettings.CompanyId + "|" + GrimoireSettings.GameId;

        private static void OnPlayModeChanged(PlayModeStateChange change)
        {
            Changed?.Invoke();
            if (change == PlayModeStateChange.EnteredEditMode)
                EditorApplication.delayCall += CheckInitialSync;
        }

        private static async void StartSync(string context)
        {
            _runningContext = context;
            IsRunning = true;
            Progress = 0f;
            Message = "Preparing Grimoire content…";
            Error = null;
            Summary = null;
            Changed?.Invoke();

            try
            {
                var result = await GrimoireSyncEverything.RunAsync((message, amount) =>
                {
                    Message = message;
                    Progress = Mathf.Clamp01(amount);
                    Changed?.Invoke();
                    return !GrimoireSettings.IsConfigured || CurrentContext() != context;
                });

                if (!result.Success)
                    Error = result.Error ?? "The content pull failed.";
                else if (result.Data.Cancelled)
                    Message = "Content pull stopped because the selected game changed.";
                else
                {
                    Summary = result.Data.ToString();
                    if (result.Data.Errors.Count > 0)
                        Error = string.Join("\n", result.Data.Errors);
                    Progress = 1f;
                    Message = "Content is up to date";
                }
            }
            catch (Exception exception)
            {
                Error = exception.Message;
            }
            finally
            {
                IsRunning = false;
                _runningContext = null;
                Changed?.Invoke();
                Completed?.Invoke();
                if (GrimoireSettings.IsConfigured &&
                    (CurrentContext() != context || SessionState.GetString(SessionKey, "") != context))
                {
                    EditorApplication.delayCall -= CheckInitialSync;
                    EditorApplication.delayCall += CheckInitialSync;
                }
            }
        }

        private static void OnBeforeAssemblyReload()
        {
            if (IsRunning && SessionState.GetString(SessionKey, "") == _runningContext)
                SessionState.EraseString(SessionKey);
        }
    }
}
