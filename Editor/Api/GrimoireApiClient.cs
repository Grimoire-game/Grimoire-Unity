using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine.Networking;

namespace Grimoire.PluginV2.Editor
{
    public enum ApiAuth
    {
        /// <summary>Authorization: Bearer (signed-in user JWT).</summary>
        Bearer,

        /// <summary>No credentials (login endpoints).</summary>
        None,
    }

    public class ApiResult<T>
    {
        public bool Success;
        public T Data;
        public string Error;
        public string Code;
        public long HttpStatus;

        public static ApiResult<T> Ok(T data, long status = 200) =>
            new ApiResult<T> { Success = true, Data = data, HttpStatus = status };

        public static ApiResult<T> Fail(string error, string code = null, long status = 0) =>
            new ApiResult<T> { Success = false, Error = error, Code = code, HttpStatus = status };
    }

    /// <summary>
    /// HTTP client for the Grimoire Public API v1.
    ///
    /// Built on UnityWebRequest wrapped in Tasks: `operation.completed` fires on
    /// the editor main thread, so continuations may touch EditorPrefs and the
    /// GUI without dispatching.
    /// </summary>
    public static class GrimoireApiClient
    {
        private const int RequestTimeoutSeconds = 30;

        // -----------------------------------------------------------------
        // Endpoints
        // -----------------------------------------------------------------

        /// <summary>GET /api/v1/objects — summaries for pickers and key resolution.</summary>
        public static async Task<ApiResult<ObjectSummary[]>> ListObjectsAsync(
            string gameId, string search = null, int limit = 50, int offset = 0)
        {
            var url = BuildUrl("/api/v1/objects", new Dictionary<string, string>
            {
                ["game_id"] = gameId,
                ["search"] = string.IsNullOrWhiteSpace(search) ? null : search.Trim(),
                ["limit"] = limit.ToString(),
                ["offset"] = offset.ToString(),
            });

            var result = await SendAsync<ListEnvelope<ObjectSummary>>("GET", url, null, ApiAuth.Bearer);
            return result.Success
                ? ApiResult<ObjectSummary[]>.Ok(result.Data.data ?? Array.Empty<ObjectSummary>())
                : ApiResult<ObjectSummary[]>.Fail(result.Error, result.Code, result.HttpStatus);
        }

        /// <summary>
        /// GET /api/v1/objects/{id} — the Object View Document, tasks included.
        /// </summary>
        public static async Task<ApiResult<ObjectViewDocument>> GetObjectViewAsync(
            string gameId, string objectId, string locale = null)
        {
            var url = BuildUrl($"/api/v1/objects/{UnityWebRequest.EscapeURL(objectId)}", new Dictionary<string, string>
            {
                ["game_id"] = gameId,
                ["locale"] = string.IsNullOrWhiteSpace(locale) ? null : locale.Trim(),
            });

            var result = await SendAsync<SingleEnvelope<ObjectViewDocument>>("GET", url, null, ApiAuth.Bearer);
            if (!result.Success)
            {
                return ApiResult<ObjectViewDocument>.Fail(result.Error, result.Code, result.HttpStatus);
            }

            ObjectViewSchema.WarnOnVersionMismatch(result.Data.data?.schema_version);
            return ApiResult<ObjectViewDocument>.Ok(result.Data.data);
        }

        /// <summary>GET /api/v1/statuses?domain=tasks — valid task workflow statuses.</summary>
        public static async Task<ApiResult<WorkflowStatusEntry[]>> GetTaskStatusesAsync(string gameId)
        {
            var url = BuildUrl("/api/v1/statuses", new Dictionary<string, string>
            {
                ["game_id"] = gameId,
                ["domain"] = "tasks",
            });

            var result = await SendAsync<SingleEnvelope<WorkflowStatusData>>("GET", url, null, ApiAuth.Bearer);
            return result.Success
                ? ApiResult<WorkflowStatusEntry[]>.Ok(result.Data.data?.statuses ?? Array.Empty<WorkflowStatusEntry>())
                : ApiResult<WorkflowStatusEntry[]>.Fail(result.Error, result.Code, result.HttpStatus);
        }

        /// <summary>
        /// PATCH /api/v1/tasks/{id} — update a task's workflow status. Bearer
        /// JWT only; the API rejects API keys on PATCH.
        /// </summary>
        public static async Task<ApiResult<GrimoireTask>> UpdateTaskStatusAsync(
            string gameId, string taskId, string statusKey)
        {
            var url = BuildUrl($"/api/v1/tasks/{UnityWebRequest.EscapeURL(taskId)}", new Dictionary<string, string>
            {
                ["game_id"] = gameId,
            });

            var body = JsonConvert.SerializeObject(new Dictionary<string, string> { ["status"] = statusKey });
            var result = await SendAsync<SingleEnvelope<GrimoireTask>>("PATCH", url, body, ApiAuth.Bearer);
            return result.Success
                ? ApiResult<GrimoireTask>.Ok(result.Data.data)
                : ApiResult<GrimoireTask>.Fail(result.Error, result.Code, result.HttpStatus);
        }

        /// <summary>POST /api/v1/auth/login — session or 2FA challenge.</summary>
        public static async Task<ApiResult<AuthSessionData>> LoginAsync(string email, string password)
        {
            var body = JsonConvert.SerializeObject(new Dictionary<string, string>
            {
                ["email"] = email,
                ["password"] = password,
            });

            var result = await SendAsync<SingleEnvelope<AuthSessionData>>(
                "POST", BuildUrl("/api/v1/auth/login", null), body, ApiAuth.None);
            return result.Success
                ? ApiResult<AuthSessionData>.Ok(result.Data.data)
                : ApiResult<AuthSessionData>.Fail(result.Error, result.Code, result.HttpStatus);
        }

        /// <summary>POST /api/v1/auth/verify-2fa — exchange temp token + code for a session.</summary>
        public static async Task<ApiResult<AuthSessionData>> Verify2faAsync(string tempToken, string code)
        {
            var body = JsonConvert.SerializeObject(new Dictionary<string, string>
            {
                ["temp_token"] = tempToken,
                ["code"] = code,
            });

            var result = await SendAsync<SingleEnvelope<AuthSessionData>>(
                "POST", BuildUrl("/api/v1/auth/verify-2fa", null), body, ApiAuth.None);
            return result.Success
                ? ApiResult<AuthSessionData>.Ok(result.Data.data)
                : ApiResult<AuthSessionData>.Fail(result.Error, result.Code, result.HttpStatus);
        }

        /// <summary>GET /api/v1/games — games the signed-in user can access.</summary>
        public static async Task<ApiResult<GameDirectoryEntry[]>> ListGamesAsync(string companyId = null)
        {
            var url = BuildUrl("/api/v1/games", new Dictionary<string, string>
            {
                ["company_id"] = string.IsNullOrWhiteSpace(companyId) ? null : companyId.Trim(),
            });

            var result = await SendAsync<ListEnvelope<GameDirectoryEntry>>("GET", url, null, ApiAuth.Bearer);
            return result.Success
                ? ApiResult<GameDirectoryEntry[]>.Ok(result.Data.data ?? Array.Empty<GameDirectoryEntry>())
                : ApiResult<GameDirectoryEntry[]>.Fail(result.Error, result.Code, result.HttpStatus);
        }

        /// <summary>POST /api/v1/auth/refresh — mint a new JWT from the current (even expired) one.</summary>
        public static async Task<ApiResult<AuthSessionData>> RefreshAsync()
        {
            var result = await SendAsync<SingleEnvelope<AuthSessionData>>(
                "POST", BuildUrl("/api/v1/auth/refresh", null), null, ApiAuth.Bearer);
            return result.Success
                ? ApiResult<AuthSessionData>.Ok(result.Data.data)
                : ApiResult<AuthSessionData>.Fail(result.Error, result.Code, result.HttpStatus);
        }

        // -----------------------------------------------------------------
        // Plumbing
        // -----------------------------------------------------------------

        private static string BuildUrl(string path, Dictionary<string, string> query)
        {
            var builder = new StringBuilder(GrimoireSettings.ApiBaseUrl);
            builder.Append(path);

            if (query != null)
            {
                var first = true;
                foreach (var pair in query)
                {
                    if (pair.Value == null)
                    {
                        continue;
                    }

                    builder.Append(first ? '?' : '&');
                    builder.Append(pair.Key).Append('=').Append(UnityWebRequest.EscapeURL(pair.Value));
                    first = false;
                }
            }

            return builder.ToString();
        }

        private static async Task<ApiResult<T>> SendAsync<T>(
            string method, string url, string jsonBody, ApiAuth auth) where T : ApiEnvelope
        {
            byte[] bodyBytes = null;
            if (jsonBody != null)
            {
                bodyBytes = Encoding.UTF8.GetBytes(jsonBody);
            }

            using (var request = CreateRequest(method, url, bodyBytes))
            {
                request.timeout = RequestTimeoutSeconds;

                if (bodyBytes != null)
                {
                    request.SetRequestHeader("Content-Type", "application/json");
                }

                if (auth == ApiAuth.Bearer)
                {
                    var token = GrimoireSettings.SessionToken;
                    if (string.IsNullOrEmpty(token))
                    {
                        return ApiResult<T>.Fail(
                            "Not signed in. Sign in with your Grimoire account first.",
                            "missing_credentials");
                    }

                    request.SetRequestHeader("Authorization", $"Bearer {token}");
                }

                await AwaitRequest(request);

                var responseBody = request.downloadHandler?.text;

                if (request.result != UnityWebRequest.Result.Success)
                {
                    return ApiResult<T>.Fail(
                        DescribeFailure(request, responseBody, out var code),
                        code,
                        request.responseCode);
                }

                try
                {
                    var parsed = JsonConvert.DeserializeObject<T>(responseBody);
                    if (parsed == null || !parsed.success)
                    {
                        return ApiResult<T>.Fail(
                            parsed?.error ?? "Malformed response from the Grimoire API.",
                            parsed?.code,
                            request.responseCode);
                    }

                    return ApiResult<T>.Ok(parsed, request.responseCode);
                }
                catch (Exception exception)
                {
                    return ApiResult<T>.Fail(
                        $"Could not read the Grimoire API response: {exception.Message}",
                        "parse_error",
                        request.responseCode);
                }
            }
        }

        /// <summary>
        /// UnityWebRequest does not construct PATCH/PUT with bodies reliably on
        /// every platform; PUT + method override is the common editor workaround.
        /// </summary>
        private static UnityWebRequest CreateRequest(string method, string url, byte[] bodyBytes)
        {
            UnityWebRequest request;

            if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
            {
                request = UnityWebRequest.Get(url);
            }
            else if (string.Equals(method, "DELETE", StringComparison.OrdinalIgnoreCase))
            {
                request = UnityWebRequest.Delete(url);
            }
            else if (bodyBytes != null &&
                     (string.Equals(method, "PATCH", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(method, "PUT", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase)))
            {
                request = UnityWebRequest.Put(url, bodyBytes);
                request.method = method.ToUpperInvariant();
            }
            else
            {
                request = new UnityWebRequest(url, method);
                if (bodyBytes != null)
                {
                    request.uploadHandler = new UploadHandlerRaw(bodyBytes);
                }
            }

            if (request.downloadHandler == null)
            {
                request.downloadHandler = new DownloadHandlerBuffer();
            }

            return request;
        }

        /// <summary>
        /// `operation.completed` is invoked on the editor main thread, so
        /// everything after the await stays on it.
        /// </summary>
        private static Task AwaitRequest(UnityWebRequest request)
        {
            var completion = new TaskCompletionSource<bool>();
            request.SendWebRequest().completed += _ => completion.TrySetResult(true);
            return completion.Task;
        }

        /// <summary>
        /// The API answers failures with a stable `code` plus a human message;
        /// codes a user can act on get a pointed hint appended.
        /// </summary>
        private static string DescribeFailure(UnityWebRequest request, string body, out string code)
        {
            code = null;

            if (!string.IsNullOrEmpty(body))
            {
                ApiEnvelope envelope = null;
                try
                {
                    envelope = JsonConvert.DeserializeObject<ApiEnvelope>(body);
                }
                catch (Exception)
                {
                    // Not a Grimoire error envelope; fall through to the raw failure.
                }

                if (envelope != null && !string.IsNullOrEmpty(envelope.error))
                {
                    code = envelope.code;
                    switch (envelope.code)
                    {
                        case "invalid_credentials":
                        case "missing_credentials":
                            return $"{envelope.error} Sign in with your Grimoire account in the Object Widget.";
                        case "insufficient_scope":
                            return $"{envelope.error} Your account may not have access to this action in Grimoire.";
                        case "rate_limited":
                            return $"{envelope.error} Wait a moment and try again.";
                        case "jwt_not_accepted":
                        case "api_key_not_accepted":
                        case "user_session_required":
                            return $"{envelope.error} Sign in with your Grimoire account in the Object Widget.";
                        default:
                            return envelope.error;
                    }
                }
            }

            return $"{request.error} (HTTP {request.responseCode})";
        }
    }
}
