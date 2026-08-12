using System;
using System.Collections;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace Grimoire.PluginV2
{
    /// <summary>
    /// Records Grimoire variable and object field changes during a play session and
    /// streams them to the Grimoire platform in real time.
    ///
    /// Setup:
    ///   1. Add this component to a GameObject in your scene (alongside GrimoireBootstrap).
    ///   2. Create a Playthrough on the platform and paste the session ID here.
    ///   3. Press Play – events are sent automatically.
    ///
    /// Works in both the Unity Editor (Play Mode) and standalone builds.
    /// Session ID can also be set at runtime via <see cref="StartSession"/>.
    /// </summary>
    [AddComponentMenu("Grimoire/Grimoire Session Tracker")]
    public class GrimoireSessionTracker : MonoBehaviour
    {
        // ── Inspector ──────────────────────────────────────────────────────────

        [Header("Session")]
        [Tooltip("Session ID from the Grimoire platform (Playthroughs page).")]
        [SerializeField] private string _sessionId;

        [Tooltip("Start tracking automatically on Awake. " +
                 "Disable if you want to call StartSession() manually (e.g. after login).")]
        [SerializeField] private bool _autoStartOnAwake = true;

        [Header("API")]
        [Tooltip("Grimoire API base URL. Leave empty to use the default.")]
        [SerializeField] private string _apiBaseUrl = "https://api.usegrimoire.com/api";

        [Header("Flushing")]
        [Tooltip("How often (in seconds) queued events are sent to the platform.")]
        [SerializeField] private float _flushIntervalSeconds = 3f;

        [Tooltip("Flush immediately when the application quits.")]
        [SerializeField] private bool _flushOnQuit = true;

        // ── Reflection handles ─────────────────────────────────────────────────
        private EventInfo _variableChangedEvent;
        private EventInfo _fieldChangedEvent;
        private Delegate  _variableChangedDelegate;
        private Delegate  _fieldChangedDelegate;

        // ── Status ─────────────────────────────────────────────────────────────
        [Header("Status (read-only)")]
        [SerializeField, HideInInspector] private string _status = "Idle";

        // ── Internal ───────────────────────────────────────────────────────────
        private bool _isTracking;
        private Coroutine _flushRoutine;

        // ── Singleton ──────────────────────────────────────────────────────────
        public static GrimoireSessionTracker Instance { get; private set; }

        /// <summary>Playthrough session ID from the Grimoire platform.</summary>
        public string SessionId
        {
            get => _sessionId;
            set => _sessionId = value ?? "";
        }

        /// <summary>API base URL used when flushing events.</summary>
        public string ApiBaseUrl
        {
            get => _apiBaseUrl;
            set => _apiBaseUrl = value ?? "";
        }

        /// <summary>True while a playthrough session is actively tracking.</summary>
        public bool IsTracking => _isTracking;

        /// <summary>Human-readable status shown in the editor Runtime tab.</summary>
        public string StatusText => _status;

        // ─────────────────────────────────────────────────────────────────────
        #region Unity lifecycle

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning("[GrimoireSessionTracker] Duplicate found – destroying this instance.", gameObject);
                Destroy(gameObject);
                return;
            }

            Instance = this;
        }

        private void Start()
        {
            // Configure GrimoireSession (generated, in export) if available
            ConfigureSession();

            if (_autoStartOnAwake && !string.IsNullOrWhiteSpace(_sessionId))
                StartSession(_sessionId);
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                if (_isTracking && _flushOnQuit)
                    StartCoroutine(FlushAndEnd());

                UnhookEvents();
                Instance = null;
            }
        }

        private void OnApplicationQuit()
        {
            if (_isTracking)
                InvokeSessionEnd();
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Public API

        /// <summary>
        /// Start (or switch to) a session. Safe to call at any time.
        /// </summary>
        public void StartSession(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                Debug.LogWarning("[GrimoireSessionTracker] StartSession called with empty session ID.");
                return;
            }

            if (_isTracking)
                StopSession();

            _sessionId = sessionId;
            _isTracking = true;

            // Tell GrimoireSession (in the export) to begin
            InvokeSessionStart(sessionId);

            // Hook change events
            HookEvents();

            // Start periodic flush coroutine
            if (_flushRoutine != null) StopCoroutine(_flushRoutine);
            _flushRoutine = StartCoroutine(FlushRoutine());

            _status = $"Tracking – {sessionId}";
            Debug.Log($"[GrimoireSessionTracker] Session started: {sessionId}");
        }

        /// <summary>
        /// Log a custom event to the Grimoire event feed from any script.
        /// The caller script and method are captured automatically from the call stack.
        /// </summary>
        /// <example>
        /// GrimoireSessionTracker.Instance.LogEvent("ItemPickedUp", "sword_01");
        /// GrimoireSessionTracker.Instance.LogEvent("DoorOpened");
        /// </example>
        /// <param name="label">Short event label shown in the feed (e.g. "ItemPickedUp").</param>
        /// <param name="message">Optional detail value (e.g. item name, count, description).</param>
        public void LogEvent(string label, string message = null)
        {
            if (!_isTracking || string.IsNullOrWhiteSpace(label)) return;

            CallerInfo caller = CaptureCallerInfo();

            Type sessionType = GrimoireReflect.FindType("GrimoireSession");
            if (sessionType == null)
            {
                Debug.LogWarning("[GrimoireSessionTracker] LogEvent: GrimoireSession not found.");
                return;
            }

            var method = sessionType.GetMethod("LogCustomEvent",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(string), typeof(string), typeof(string), typeof(string), typeof(string) },
                null);

            if (method != null)
            {
                method.Invoke(null, new object[]
                {
                    label, message ?? "",
                    caller.Script, caller.Method, caller.GameObjectName
                });
            }
            else
            {
                // Fallback: use basic TrackEvent if LogCustomEvent is not available in the export
                var basic = sessionType.GetMethod("TrackEvent",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(string), typeof(string), typeof(string), typeof(string), typeof(string) },
                    null);
                basic?.Invoke(null, new object[] { "custom_log", label, null, message ?? "", "string" });
            }
        }

        /// <summary>
        /// Stop the current session. Flushes remaining events and sends session_ended.
        /// </summary>
        public void StopSession()
        {
            if (!_isTracking) return;

            _isTracking = false;

            if (_flushRoutine != null)
            {
                StopCoroutine(_flushRoutine);
                _flushRoutine = null;
            }

            UnhookEvents();
            InvokeSessionEnd();

            _status = "Idle";
            Debug.Log("[GrimoireSessionTracker] Session stopped.");
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Session integration (GrimoireSession in the export)

        /// <summary>
        /// Configures GrimoireSession.HttpFlushSink to use UnityWebRequest.
        /// Called once on Start, before any session begins.
        /// </summary>
        private void ConfigureSession()
        {
            Type sessionType = GrimoireReflect.FindType("GrimoireSession");
            if (sessionType == null)
            {
                Debug.LogWarning("[GrimoireSessionTracker] GrimoireSession not found in loaded assemblies. " +
                                 "Import an export that includes GrimoireSession.cs.");
                return;
            }

            // Set API base URL
            if (!string.IsNullOrWhiteSpace(_apiBaseUrl))
            {
                var apiProp = sessionType.GetProperty("ApiBaseUrl",
                    BindingFlags.Public | BindingFlags.Static);
                if (apiProp != null && apiProp.CanWrite)
                    apiProp.SetValue(null, _apiBaseUrl.TrimEnd('/'));
            }

            // Wire up the HTTP flush sink
            // Signature: Action<string url, string json, Action<bool> onComplete>
            var sinkField = sessionType.GetField("HttpFlushSink",
                BindingFlags.Public | BindingFlags.Static);
            if (sinkField != null)
            {
                Action<string, string, Action<bool>> sink = (url, json, onComplete) =>
                    StartCoroutine(PostJson(url, json, onComplete));
                sinkField.SetValue(null, sink);
                Debug.Log("[GrimoireSessionTracker] GrimoireSession.HttpFlushSink configured.");
            }
        }

        private void InvokeSessionStart(string sessionId)
        {
            Type sessionType = GrimoireReflect.FindType("GrimoireSession");
            if (sessionType == null) return;

            var method = sessionType.GetMethod("StartSession",
                BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeof(string) }, null);
            method?.Invoke(null, new object[] { sessionId });
        }

        private void InvokeSessionEnd()
        {
            Type sessionType = GrimoireReflect.FindType("GrimoireSession");
            if (sessionType == null) return;

            var method = sessionType.GetMethod("EndSession",
                BindingFlags.Public | BindingFlags.Static,
                null, Type.EmptyTypes, null);
            method?.Invoke(null, null);
        }

        private void InvokeFlush()
        {
            Type sessionType = GrimoireReflect.FindType("GrimoireSession");
            if (sessionType == null) return;

            var method = sessionType.GetMethod("Flush",
                BindingFlags.Public | BindingFlags.Static,
                null, Type.EmptyTypes, null);
            method?.Invoke(null, null);
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Change event hooks (reflection)

        /// <summary>
        /// Subscribes to VariableRuntime.OnVariableChanged and ObjectRuntime.OnFieldChanged
        /// via reflection so this file never needs to reference generated code directly.
        /// </summary>
        private void HookEvents()
        {
            HookVariableRuntime();
            HookObjectRuntime();
        }

        private void UnhookEvents()
        {
            try
            {
                _variableChangedEvent?.RemoveEventHandler(null, _variableChangedDelegate);
                _fieldChangedEvent?.RemoveEventHandler(null, _fieldChangedDelegate);
            }
            catch { }
            _variableChangedEvent = null;
            _fieldChangedEvent    = null;
            _variableChangedDelegate = null;
            _fieldChangedDelegate    = null;
        }

        private void HookVariableRuntime()
        {
            Type runtimeType = GrimoireReflect.FindType("VariableRuntime");
            if (runtimeType == null) return;

            _variableChangedEvent = runtimeType.GetEvent("OnVariableChanged",
                BindingFlags.Public | BindingFlags.Static);
            if (_variableChangedEvent == null) return;

            // Delegate signature: void(string group, string name, object oldValue, object newValue)
            _variableChangedDelegate = CreateDelegate(
                _variableChangedEvent.EventHandlerType,
                this,
                nameof(OnVariableChanged));

            if (_variableChangedDelegate != null)
                _variableChangedEvent.AddEventHandler(null, _variableChangedDelegate);
        }

        private void HookObjectRuntime()
        {
            Type runtimeType = GrimoireReflect.FindType("ObjectRuntime");
            if (runtimeType == null) return;

            _fieldChangedEvent = runtimeType.GetEvent("OnFieldChanged",
                BindingFlags.Public | BindingFlags.Static);
            if (_fieldChangedEvent == null) return;

            // Delegate signature: void(string itemKey, string fieldName, object oldValue, object newValue)
            _fieldChangedDelegate = CreateDelegate(
                _fieldChangedEvent.EventHandlerType,
                this,
                nameof(OnObjectFieldChanged));

            if (_fieldChangedDelegate != null)
                _fieldChangedEvent.AddEventHandler(null, _fieldChangedDelegate);
        }

        // ── Event handlers ────────────────────────────────────────────────────

        // Called by VariableRuntime.OnVariableChanged
        private void OnVariableChanged(string group, string name, object oldValue, object newValue)
        {
            if (!_isTracking) return;

            string key     = $"{group}.{name}";
            string type    = InferValueType(newValue);
            CallerInfo caller = CaptureCallerInfo();
            TrackEventFull("variable_changed", key, FormatValue(oldValue), FormatValue(newValue), type,
                objectId: group, fieldName: name, caller: caller);
        }

        // Called by ObjectRuntime.OnFieldChanged
        private void OnObjectFieldChanged(string itemKey, string fieldName, object oldValue, object newValue)
        {
            if (!_isTracking) return;

            string key  = $"{itemKey}.{fieldName}";
            string type = InferValueType(newValue);
            CallerInfo caller = CaptureCallerInfo();
            TrackEventFull("object_field_changed", key, FormatValue(oldValue), FormatValue(newValue), type,
                objectId: itemKey, fieldName: fieldName, caller: caller);
        }

        // ── TrackEventFull – preferred path (sends structured context) ─────────

        private void TrackEventFull(
            string type, string key,
            string oldVal, string newVal, string valueType,
            string objectId, string fieldName, CallerInfo caller)
        {
            Type sessionType = GrimoireReflect.FindType("GrimoireSession");
            if (sessionType == null) return;

            // Try the extended overload first (available when the generated session supports it)
            var extMethod = sessionType.GetMethod("TrackEventEx",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[]
                {
                    typeof(string), typeof(string), typeof(string), typeof(string), typeof(string),
                    typeof(string), typeof(string), typeof(string), typeof(string), typeof(string)
                },
                null);

            if (extMethod != null)
            {
                extMethod.Invoke(null, new object[]
                {
                    type, key, oldVal, newVal, valueType,
                    objectId, fieldName,
                    caller.Script, caller.Method, caller.GameObjectName
                });
                return;
            }

            // Fall back: pack extra fields as JSON into a special TrackEventWithContext overload,
            // or fall all the way back to the basic 5-parameter TrackEvent.
            var callerJson = caller.ToJson();
            var ctxMethod = sessionType.GetMethod("TrackEventWithContext",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[]
                {
                    typeof(string), typeof(string), typeof(string), typeof(string), typeof(string),
                    typeof(string), typeof(string), typeof(string)
                },
                null);

            if (ctxMethod != null)
            {
                ctxMethod.Invoke(null, new object[]
                {
                    type, key, oldVal, newVal, valueType,
                    objectId, fieldName, callerJson
                });
                return;
            }

            // Ultimate fallback: legacy 5-parameter method
            var basicMethod = sessionType.GetMethod("TrackEvent",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(string), typeof(string), typeof(string), typeof(string), typeof(string) },
                null);
            basicMethod?.Invoke(null, new object[] { type, key, oldVal, newVal, valueType });
        }

        // ── Caller capture ─────────────────────────────────────────────────────

        /// <summary>
        /// Captures the Unity call-site (script class, method, and optionally the
        /// active GameObject) that triggered the current change event.
        /// Walks past internal Grimoire frames so the reported caller is always
        /// the user's own script.
        /// </summary>
        private static CallerInfo CaptureCallerInfo()
        {
            try
            {
                var trace = new System.Diagnostics.StackTrace(skipFrames: 2, fNeedFileInfo: false);
                for (int i = 0; i < trace.FrameCount; i++)
                {
                    var frame  = trace.GetFrame(i);
                    var method = frame?.GetMethod();
                    if (method == null) continue;

                    string ns    = method.DeclaringType?.Namespace ?? "";
                    string typeName = method.DeclaringType?.Name ?? "";

                    // Skip internal Grimoire / Unity / Mono frames
                    if (ns.StartsWith("Grimoire", StringComparison.Ordinal)) continue;
                    if (ns.StartsWith("UnityEngine", StringComparison.Ordinal)) continue;
                    if (ns.StartsWith("Unity", StringComparison.Ordinal)) continue;
                    if (ns.StartsWith("System", StringComparison.Ordinal)) continue;
                    if (typeName.StartsWith("<", StringComparison.Ordinal)) continue; // compiler-generated

                    return new CallerInfo
                    {
                        Script         = typeName,
                        Method         = method.Name,
                        GameObjectName = null, // resolved at runtime below
                    };
                }
            }
            catch { }

            return new CallerInfo { Script = "Unknown", Method = "Unknown", GameObjectName = null };
        }

        private struct CallerInfo
        {
            public string Script;
            public string Method;
            public string GameObjectName;

            public string ToJson()
            {
                string s = EscapeJson(Script ?? "");
                string m = EscapeJson(Method ?? "");
                string g = EscapeJson(GameObjectName ?? "");
                return $"{{\"script\":\"{s}\",\"method\":\"{m}\",\"gameObject\":\"{g}\"}}";
            }

            private static string EscapeJson(string v) =>
                v.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Flush coroutine

        private IEnumerator FlushRoutine()
        {
            var wait = new WaitForSeconds(_flushIntervalSeconds);
            while (_isTracking)
            {
                yield return wait;
                InvokeFlush();
            }
        }

        private IEnumerator FlushAndEnd()
        {
            InvokeSessionEnd();
            yield return new WaitForSeconds(0.1f);
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region HTTP (UnityWebRequest)

        private IEnumerator PostJson(string url, string json, Action<bool> onComplete)
        {
            byte[] body = Encoding.UTF8.GetBytes(json);

            using (var req = new UnityWebRequest(url, "POST"))
            {
                req.uploadHandler   = new UploadHandlerRaw(body);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.timeout = 15;

                yield return req.SendWebRequest();

                bool ok = req.result == UnityWebRequest.Result.Success;
                if (!ok)
                    Debug.LogWarning($"[GrimoireSessionTracker] Flush failed ({req.responseCode}): {req.error}");

                onComplete?.Invoke(ok);
            }
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Reflection helpers

        /// <summary>
        /// Creates a delegate of the exact event handler type that calls the named instance method.
        /// Falls back to null if the signatures do not match.
        /// </summary>
        private Delegate CreateDelegate(Type delegateType, object target, string methodName)
        {
            if (delegateType == null) return null;
            try
            {
                var method = target.GetType().GetMethod(
                    methodName,
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (method == null) return null;
                return Delegate.CreateDelegate(delegateType, target, method, throwOnBindFailure: false);
            }
            catch
            {
                return null;
            }
        }

        private static string FormatValue(object value)
        {
            if (value == null) return null;
            if (value is float f)  return f.ToString("G");
            if (value is double d) return d.ToString("G");
            return value.ToString();
        }

        private static string InferValueType(object value)
        {
            if (value is bool)               return "boolean";
            if (value is int || value is long || value is float || value is double) return "number";
            return "string";
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Context-menu helpers

        [ContextMenu("Start Session")]
        private void CtxStartSession()
        {
            if (!Application.isPlaying) { Debug.Log("[GrimoireSessionTracker] Only usable in Play Mode."); return; }
            StartSession(_sessionId);
        }

        [ContextMenu("Stop Session")]
        private void CtxStopSession() => StopSession();

        [ContextMenu("Flush Now")]
        private void CtxFlushNow()
        {
            if (!Application.isPlaying) return;
            InvokeFlush();
        }

        [ContextMenu("Log Status")]
        private void CtxLogStatus()
        {
            Debug.Log($"[GrimoireSessionTracker] Status: {_status}\n" +
                      $"  Tracking   : {_isTracking}\n" +
                      $"  Session ID : {_sessionId}\n" +
                      $"  Flush (s)  : {_flushIntervalSeconds}");
        }

        #endregion
    }
}
