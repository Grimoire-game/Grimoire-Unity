using System;
using System.Collections;
using System.Reflection;
using UnityEngine;
using UnityEngine.Events;

namespace Grimoire.PluginV2
{
    /// <summary>
    /// Scene-level MonoBehaviour bootstrap for the Grimoire engine.
    ///
    /// Add this component to a GameObject in your first scene.
    /// It persists across scene loads (DontDestroyOnLoad) and:
    ///   • Wires GrimoireLog  → Unity Debug
    ///   • Wires GrimoireStorage → PlayerPrefs
    ///   • Detects or overrides the active language (TranslationKey.CurrentLanguage)
    ///   • Initializes VariableRuntime with design-time defaults
    ///   • Initializes ObjectRuntime with Database.Library defaults
    ///   • Always resets runtimes to a clean state on initialization
    ///   • Optionally enables save/load persistence via PlayerPrefs
    ///
    /// By default, every Play starts fresh from your Grimoire export defaults.
    /// Enable "Persistence" to restore saved state between sessions.
    ///
    /// For playthrough session tracking, add a GrimoireSessionTracker component.
    ///
    /// Works entirely through reflection so the Grimoire export under Assets/Grimoire/
    /// can be regenerated freely without touching this file.
    /// </summary>
    [AddComponentMenu("Grimoire/Grimoire Bootstrap")]
    [DisallowMultipleComponent]
    public class GrimoireBootstrap : MonoBehaviour
    {
        // ── Singleton ─────────────────────────────────────────────────────────
        public static GrimoireBootstrap Instance { get; private set; }

        /// <summary>Fires once after the bootstrap has finished initializing.</summary>
        public static event Action OnInitialized;

        public bool IsInitialized { get; private set; }

        // ── Inspector ─────────────────────────────────────────────────────────

        [Header("Language")]
        [Tooltip("Detect language from the OS/device at runtime.")]
        [SerializeField] private bool _useSystemLanguage = true;

        [Tooltip("Language code to use when 'Use System Language' is off, or as fallback.")]
        [SerializeField] private string _defaultLanguage = "en";

        [Tooltip("Fallback code when the detected system language has no translation in the export.")]
        [SerializeField] private string _fallbackLanguage = "en";

        [Header("Initialization")]
        [Tooltip("Call VariableRuntime.Initialize() during Awake.")]
        [SerializeField] private bool _initializeVariables = true;

        [Tooltip("Call ObjectRuntime.Initialize() during Awake, seeding values from Database.Library.")]
        [SerializeField] private bool _initializeObjects = true;

        [Header("Persistence")]
        [Tooltip("Enable save/load of runtime state between sessions via PlayerPrefs. " +
                 "When OFF (default), every Play starts fresh from your Grimoire export defaults.")]
        [SerializeField] private bool _enablePersistence = false;

        [Tooltip("Automatically save current variable and object values when the application quits. " +
                 "Only applies when Persistence is enabled.")]
        [SerializeField] private bool _saveOnQuit = true;

        [Tooltip("Auto-save interval in seconds (0 = disabled). Useful for mobile crash recovery. " +
                 "Only applies when Persistence is enabled.")]
        [SerializeField] private float _autoSaveInterval = 0f;

        [Tooltip("PlayerPrefs key used for variable persistence.")]
        [SerializeField] private string _variablesSaveKey = "GrimoireVariables";

        [Tooltip("PlayerPrefs key used for object field persistence.")]
        [SerializeField] private string _objectsSaveKey = "GrimoireObjects";

        [Header("Logging")]
        [Tooltip("Route GrimoireLog to Unity's Debug class. " +
                 "Disable if you configure GrimoireLog yourself.")]
        [SerializeField] private bool _configureLogging = true;

        [Tooltip("Route GrimoireStorage to PlayerPrefs. " +
                 "Disable if you configure GrimoireStorage yourself.")]
        [SerializeField] private bool _configureStorage = true;

        [Header("Events")]
        [Tooltip("Invoked after the bootstrap has finished initializing.")]
        [SerializeField] private UnityEvent _onInitialized = new UnityEvent();

        // ── Runtime status (read-only in Inspector) ───────────────────────────
        [Header("Status (read-only)")]
        [SerializeField, HideInInspector] private string _activeLanguage;
        [SerializeField, HideInInspector] private string _variableRuntimeStatus;
        [SerializeField, HideInInspector] private string _objectRuntimeStatus;

        // ─────────────────────────────────────────────────────────────────────
        #region Unity lifecycle

        /// <summary>
        /// Clears static state when entering Play Mode so the singleton guard
        /// doesn't block initialization on subsequent plays.
        /// Required for "Enter Play Mode Without Reloading Domain".
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            Instance = null;
            OnInitialized = null;
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning("[GrimoireBootstrap] Duplicate found – destroying this instance.", gameObject);
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);

            RunInitialization();
        }

        private void Start()
        {
            if (_enablePersistence && _autoSaveInterval > 0f)
                StartCoroutine(AutoSaveRoutine());
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Application.quitting -= OnApplicationQuitting;
                Instance = null;
            }
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Initialization

        private void RunInitialization()
        {
            Debug.Log("[GrimoireBootstrap] Starting initialization…");

            if (_configureLogging) ConfigureLogging();
            if (_configureStorage) ConfigureStorage();

            SetLanguage(DetermineLanguage());

            if (_initializeVariables) InitializeVariableRuntime();
            if (_initializeObjects)   InitializeObjectRuntime();
            InitializeLogicRuntime();

            Application.quitting += OnApplicationQuitting;

            IsInitialized = true;

            _onInitialized?.Invoke();
            OnInitialized?.Invoke();

            Debug.Log($"[GrimoireBootstrap] Initialized — language: {_activeLanguage}," +
                      $" variables: {_variableRuntimeStatus}," +
                      $" objects: {_objectRuntimeStatus}");
        }

        // ── Logging ──────────────────────────────────────────────────────────

        private void ConfigureLogging()
        {
            Type logType = GrimoireReflect.FindType("GrimoireLog");
            if (logType == null)
            {
                Debug.Log("[GrimoireBootstrap] GrimoireLog not found — skipping logging setup.");
                return;
            }

            GrimoireReflect.SetStaticDelegate(logType, "InfoSink",
                (Action<string>)(msg => Debug.Log($"[Grimoire] {msg}")));
            GrimoireReflect.SetStaticDelegate(logType, "WarnSink",
                (Action<string>)(msg => Debug.LogWarning($"[Grimoire] {msg}")));
            GrimoireReflect.SetStaticDelegate(logType, "ErrorSink",
                (Action<string>)(msg => Debug.LogError($"[Grimoire] {msg}")));

            Debug.Log("[GrimoireBootstrap] GrimoireLog → Unity Debug configured.");
        }

        // ── Storage ──────────────────────────────────────────────────────────

        private void ConfigureStorage()
        {
            Type storageType = GrimoireReflect.FindType("GrimoireStorage");
            if (storageType == null)
            {
                Debug.Log("[GrimoireBootstrap] GrimoireStorage not found — skipping storage setup.");
                return;
            }

            GrimoireReflect.SetStaticDelegate(storageType, "HasKeyImpl",
                (Func<string, bool>)(key => PlayerPrefs.HasKey(key)));
            GrimoireReflect.SetStaticDelegate(storageType, "GetStringImpl",
                (Func<string, string>)(key => PlayerPrefs.GetString(key, "")));
            GrimoireReflect.SetStaticDelegate(storageType, "SetStringImpl",
                (Action<string, string>)((k, v) => PlayerPrefs.SetString(k, v)));
            GrimoireReflect.SetStaticDelegate(storageType, "SaveImpl",
                (Action)(() => PlayerPrefs.Save()));
            GrimoireReflect.SetStaticDelegate(storageType, "DeleteKeyImpl",
                (Action<string>)(key => PlayerPrefs.DeleteKey(key)));
            GrimoireReflect.SetStaticDelegate(storageType, "DeleteAllImpl",
                (Action)(() => PlayerPrefs.DeleteAll()));

            Debug.Log("[GrimoireBootstrap] GrimoireStorage → PlayerPrefs configured.");
        }

        // ── Language ─────────────────────────────────────────────────────────

        private string DetermineLanguage()
        {
            string code = _useSystemLanguage ? SystemLanguageToCode(Application.systemLanguage) : _defaultLanguage;

            if (!ExportHasLanguage(code))
            {
                Debug.LogWarning(
                    $"[GrimoireBootstrap] Language '{code}' not found in export — " +
                    $"falling back to '{_fallbackLanguage}'.");
                code = _fallbackLanguage;
            }

            return code;
        }

        private void SetLanguage(string languageCode)
        {
            Type tkType = GrimoireReflect.FindType("TranslationKey");
            if (tkType != null)
            {
                var prop = tkType.GetProperty("CurrentLanguage",
                    BindingFlags.Public | BindingFlags.Static);
                if (prop != null && prop.CanWrite)
                    prop.SetValue(null, languageCode);
            }

            _activeLanguage = languageCode;
            Debug.Log($"[GrimoireBootstrap] Language → '{languageCode}'.");
        }

        /// <summary>
        /// Change the active language at runtime (e.g. from a settings screen).
        /// </summary>
        public void ChangeLanguage(string languageCode)
        {
            SetLanguage(languageCode);
        }

        // ── VariableRuntime ───────────────────────────────────────────────────

        private void InitializeVariableRuntime()
        {
            Type runtimeType = GrimoireReflect.FindType("VariableRuntime");
            if (runtimeType == null)
            {
                _variableRuntimeStatus = "VariableRuntime not found (import an export first)";
                Debug.LogWarning($"[GrimoireBootstrap] {_variableRuntimeStatus}");
                return;
            }

            // Always reset to design-time defaults for a clean slate
            var reset = runtimeType.GetMethod("Reset", BindingFlags.Public | BindingFlags.Static);
            if (reset != null)
            {
                reset.Invoke(null, null);
                Debug.Log("[GrimoireBootstrap] VariableRuntime reset to design defaults.");
            }

            // When persistence is enabled, attempt to restore a saved session (overrides defaults)
            if (_enablePersistence)
            {
                bool loaded = GrimoireReflect.InvokeStaticBool(runtimeType, "LoadFromPlayerPrefs", _variablesSaveKey);
                if (loaded)
                {
                    _variableRuntimeStatus = $"restored from PlayerPrefs (key: {_variablesSaveKey})";
                    Debug.Log($"[GrimoireBootstrap] Variables restored — {_variableRuntimeStatus}");
                    return;
                }
            }

            // Ensure initialized with design-time defaults if Reset didn't already do it
            if (!GrimoireReflect.IsRuntimeInitialized(runtimeType))
            {
                var initMethod = runtimeType.GetMethod("Initialize",
                    BindingFlags.Public | BindingFlags.Static);
                if (initMethod != null)
                {
                    initMethod.Invoke(null, null);
                    _variableRuntimeStatus = "initialized with design-time defaults";
                }
                else
                {
                    _variableRuntimeStatus = "Initialize() method not found";
                    Debug.LogWarning("[GrimoireBootstrap] VariableRuntime.Initialize() not found.");
                }
            }
            else
            {
                _variableRuntimeStatus = _enablePersistence
                    ? "reset to design-time defaults (no saved data found)"
                    : "reset to design-time defaults";
            }
        }

        // ── LogicRuntime ──────────────────────────────────────────────────────

        private void InitializeLogicRuntime()
        {
            Type runtimeType = GrimoireReflect.FindType("LogicRuntime");
            if (runtimeType == null) return;

            var reset = runtimeType.GetMethod("Reset", BindingFlags.Public | BindingFlags.Static);
            reset?.Invoke(null, null);

            var init = runtimeType.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static);
            init?.Invoke(null, null);

            Debug.Log("[GrimoireBootstrap] LogicRuntime initialized.");
        }

        // ── ObjectRuntime ─────────────────────────────────────────────────────

        private void InitializeObjectRuntime()
        {
            Type runtimeType = GrimoireReflect.FindType("ObjectRuntime");
            if (runtimeType == null)
            {
                _objectRuntimeStatus = "ObjectRuntime not found (import an export first)";
                Debug.LogWarning($"[GrimoireBootstrap] {_objectRuntimeStatus}");
                return;
            }

            // Always reset to Database.Library defaults for a clean slate
            var reset = runtimeType.GetMethod("Reset", BindingFlags.Public | BindingFlags.Static);
            if (reset != null)
            {
                reset.Invoke(null, null);
                Debug.Log("[GrimoireBootstrap] ObjectRuntime reset to Database.Library defaults.");
            }

            // When persistence is enabled, attempt to restore saved state (overrides defaults)
            if (_enablePersistence)
            {
                bool loaded = GrimoireReflect.InvokeStaticBool(runtimeType, "LoadFromPlayerPrefs", _objectsSaveKey);
                if (loaded)
                {
                    _objectRuntimeStatus = $"restored from PlayerPrefs (key: {_objectsSaveKey})";
                    Debug.Log($"[GrimoireBootstrap] Objects restored — {_objectRuntimeStatus}");
                    return;
                }
            }

            // Ensure initialized with defaults if Reset didn't already do it
            if (!GrimoireReflect.IsRuntimeInitialized(runtimeType))
            {
                var initMethod = runtimeType.GetMethod("Initialize",
                    BindingFlags.Public | BindingFlags.Static);
                if (initMethod != null)
                {
                    initMethod.Invoke(null, null);
                    _objectRuntimeStatus = "initialized with Database.Library defaults";
                }
                else
                {
                    _objectRuntimeStatus = "Initialize() method not found";
                    Debug.LogWarning("[GrimoireBootstrap] ObjectRuntime.Initialize() not found.");
                }
            }
            else
            {
                _objectRuntimeStatus = _enablePersistence
                    ? "reset to Database.Library defaults (no saved data found)"
                    : "reset to Database.Library defaults";
            }
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Persistence

        private void OnApplicationQuitting()
        {
            if (_enablePersistence && _saveOnQuit)
            {
                SaveVariables();
                SaveObjects();
            }
        }

        private IEnumerator AutoSaveRoutine()
        {
            var wait = new WaitForSeconds(_autoSaveInterval);
            while (true)
            {
                yield return wait;
                SaveVariables();
                SaveObjects();
            }
        }

        /// <summary>Save current variable values to PlayerPrefs immediately.</summary>
        public void SaveVariables()
        {
            Type runtimeType = GrimoireReflect.FindType("VariableRuntime");
            if (runtimeType == null) return;

            GrimoireReflect.InvokeStaticVoid(runtimeType, "SaveToPlayerPrefs", _variablesSaveKey);
            Debug.Log($"[GrimoireBootstrap] Variables saved (key: {_variablesSaveKey}).");
        }

        /// <summary>Reset all variables to their design-time defaults.</summary>
        public void ResetVariables()
        {
            Type runtimeType = GrimoireReflect.FindType("VariableRuntime");
            if (runtimeType == null) return;

            var reset = runtimeType.GetMethod("Reset", BindingFlags.Public | BindingFlags.Static);
            reset?.Invoke(null, null);
            Debug.Log("[GrimoireBootstrap] Variables reset to defaults.");
        }

        /// <summary>Delete saved variable data from PlayerPrefs.</summary>
        public void ClearSavedVariables()
        {
            PlayerPrefs.DeleteKey(_variablesSaveKey);
            PlayerPrefs.Save();
            Debug.Log($"[GrimoireBootstrap] Saved variable data cleared (key: {_variablesSaveKey}).");
        }

        /// <summary>Save current object field values to PlayerPrefs immediately.</summary>
        public void SaveObjects()
        {
            Type runtimeType = GrimoireReflect.FindType("ObjectRuntime");
            if (runtimeType == null) return;

            GrimoireReflect.InvokeStaticVoid(runtimeType, "SaveToPlayerPrefs", _objectsSaveKey);
            Debug.Log($"[GrimoireBootstrap] Objects saved (key: {_objectsSaveKey}).");
        }

        /// <summary>Reset all object fields to their Database.Library defaults.</summary>
        public void ResetObjects()
        {
            Type runtimeType = GrimoireReflect.FindType("ObjectRuntime");
            if (runtimeType == null) return;

            var reset = runtimeType.GetMethod("Reset", BindingFlags.Public | BindingFlags.Static);
            reset?.Invoke(null, null);
            Debug.Log("[GrimoireBootstrap] Objects reset to Database.Library defaults.");
        }

        /// <summary>Delete saved object field data from PlayerPrefs.</summary>
        public void ClearSavedObjects()
        {
            PlayerPrefs.DeleteKey(_objectsSaveKey);
            PlayerPrefs.Save();
            Debug.Log($"[GrimoireBootstrap] Saved object data cleared (key: {_objectsSaveKey}).");
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Context-menu helpers (editor)

        [ContextMenu("Save Variables Now")]
        private void CtxSaveVariables() => SaveVariables();

        [ContextMenu("Reset Variables to Defaults")]
        private void CtxResetVariables() => ResetVariables();

        [ContextMenu("Clear Saved Variable Data")]
        private void CtxClearSaved() => ClearSavedVariables();

        [ContextMenu("Save Objects Now")]
        private void CtxSaveObjects() => SaveObjects();

        [ContextMenu("Reset Objects to Defaults")]
        private void CtxResetObjects() => ResetObjects();

        [ContextMenu("Clear Saved Object Data")]
        private void CtxClearSavedObjects() => ClearSavedObjects();

        [ContextMenu("Log Status")]
        private void CtxLogStatus()
        {
            Debug.Log($"[GrimoireBootstrap] Status:\n" +
                      $"  Language        : {_activeLanguage}\n" +
                      $"  Variables       : {_variableRuntimeStatus}\n" +
                      $"  Objects         : {_objectRuntimeStatus}\n" +
                      $"  Persistence     : {(_enablePersistence ? "enabled" : "disabled")}\n" +
                      $"  Save on quit    : {_saveOnQuit}\n" +
                      $"  Auto-save (s)   : {(_autoSaveInterval > 0 ? _autoSaveInterval.ToString("F1") : "disabled")}\n" +
                      $"  Variables key   : {_variablesSaveKey}\n" +
                      $"  Objects key     : {_objectsSaveKey}");
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Language helpers

        /// <summary>
        /// Returns true if the exported TranslationKeys class includes the given language code
        /// in at least one key's translation dictionary.
        /// </summary>
        private static bool ExportHasLanguage(string code)
        {
            if (string.IsNullOrEmpty(code)) return false;

            Type tkType = GrimoireReflect.FindType("TranslationKey");
            if (tkType == null) return true;

            Type keysType = GrimoireReflect.FindType("TranslationKeys");
            if (keysType == null) return true;

            try
            {
                var emptyField = keysType.GetField("Empty",
                    BindingFlags.Public | BindingFlags.Static);
                if (emptyField == null) return true;

                object emptyKey = emptyField.GetValue(null);
                if (emptyKey == null) return true;

                var indexer = tkType.GetProperty("Item",
                    BindingFlags.Public | BindingFlags.Instance);
                if (indexer == null) return true;

                indexer.GetValue(emptyKey, new object[] { code });
                return true;
            }
            catch { return true; }
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region System language mapping

        private static string SystemLanguageToCode(SystemLanguage lang)
        {
            switch (lang)
            {
                case SystemLanguage.Afrikaans:           return "af";
                case SystemLanguage.Arabic:              return "ar";
                case SystemLanguage.Basque:              return "eu";
                case SystemLanguage.Belarusian:          return "be";
                case SystemLanguage.Bulgarian:           return "bg";
                case SystemLanguage.Catalan:             return "ca";
                case SystemLanguage.Chinese:             return "zh";
                case SystemLanguage.ChineseSimplified:   return "zh-CN";
                case SystemLanguage.ChineseTraditional:  return "zh-TW";
                case SystemLanguage.Czech:               return "cs";
                case SystemLanguage.Danish:              return "da";
                case SystemLanguage.Dutch:               return "nl";
                case SystemLanguage.English:             return "en";
                case SystemLanguage.Estonian:             return "et";
                case SystemLanguage.Faroese:             return "fo";
                case SystemLanguage.Finnish:             return "fi";
                case SystemLanguage.French:              return "fr";
                case SystemLanguage.German:              return "de";
                case SystemLanguage.Greek:               return "el";
                case SystemLanguage.Hebrew:              return "he";
                case SystemLanguage.Hungarian:           return "hu";
                case SystemLanguage.Icelandic:           return "is";
                case SystemLanguage.Indonesian:          return "id";
                case SystemLanguage.Italian:             return "it";
                case SystemLanguage.Japanese:            return "ja";
                case SystemLanguage.Korean:              return "ko";
                case SystemLanguage.Latvian:             return "lv";
                case SystemLanguage.Lithuanian:          return "lt";
                case SystemLanguage.Norwegian:           return "no";
                case SystemLanguage.Polish:              return "pl";
                case SystemLanguage.Portuguese:          return "pt";
                case SystemLanguage.Romanian:            return "ro";
                case SystemLanguage.Russian:             return "ru";
                case SystemLanguage.SerboCroatian:       return "sr";
                case SystemLanguage.Slovak:              return "sk";
                case SystemLanguage.Slovenian:           return "sl";
                case SystemLanguage.Spanish:             return "es";
                case SystemLanguage.Swedish:             return "sv";
                case SystemLanguage.Thai:                return "th";
                case SystemLanguage.Turkish:             return "tr";
                case SystemLanguage.Ukrainian:           return "uk";
                case SystemLanguage.Vietnamese:          return "vi";
                default:                                 return "en";
            }
        }

        #endregion
    }
}
