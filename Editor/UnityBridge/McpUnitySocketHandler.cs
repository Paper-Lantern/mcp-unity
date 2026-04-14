using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEditor;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WebSocketSharp;
using WebSocketSharp.Server;
using McpUnity.Tools;
using McpUnity.Resources;
using Unity.EditorCoroutines.Editor;
using System.Collections;
using System.Collections.Specialized;
using McpUnity.Utils;

namespace McpUnity.Unity
{
    /// <summary>
    /// WebSocket handler for MCP Unity communications
    /// </summary>
    public class McpUnitySocketHandler : WebSocketBehavior
    {
        private readonly McpUnityServer _server;

        public McpUnitySocketHandler(McpUnityServer server)
        {
            _server = server;
        }

        public static JObject CreateErrorResponse(string message, string errorType)
        {
            return new JObject
            {
                ["error"] = new JObject
                {
                    ["type"] = errorType,
                    ["message"] = message
                }
            };
        }

        // ── Concurrency & Timeout ─────────────────────────────────────

        private static int _activeRequests = 0;
        private const int MAX_CONCURRENT_REQUESTS = 3;
        private const double REQUEST_TIMEOUT_SECONDS = 60.0;

        private class PendingRequest
        {
            public long StartTicks; // DateTimeOffset.UtcNow.Ticks — thread-safe
            public TaskCompletionSource<JObject> Tcs;
            public string Method;
            public string RequestId;
        }

        private static readonly Dictionary<string, PendingRequest> _pendingRequests
            = new Dictionary<string, PendingRequest>();
        private static readonly object _pendingLock = new object();
        private static int _timeoutCheckRegistered_int = 0; // 0=false, 1=true (Interlocked)
        private static int _requestCounter = 0; // fallback key when requestId is null

        /// <summary>
        /// Register EditorApplication.update callback for timeout checking (once).
        /// Called from OnMessage (WebSocket thread) — uses Interlocked for thread safety.
        /// EditorApplication.update += is safe from any thread (Unity dispatches to main thread).
        /// </summary>
        private static void EnsureTimeoutChecker()
        {
            if (Interlocked.CompareExchange(ref _timeoutCheckRegistered_int, 1, 0) == 0)
            {
                EditorApplication.update += CheckTimeouts;
            }
        }

        /// <summary>
        /// Runs every editor frame. Scans pending requests for timeouts.
        /// Early-returns when no requests are pending (zero overhead).
        /// </summary>
        private static void CheckTimeouts()
        {
            List<PendingRequest> timedOut = null;

            lock (_pendingLock)
            {
                if (_pendingRequests.Count == 0) return;

                long nowTicks = DateTimeOffset.UtcNow.Ticks;
                long timeoutTicks = (long)(REQUEST_TIMEOUT_SECONDS * TimeSpan.TicksPerSecond);
                List<string> keysToRemove = null;

                foreach (var kvp in _pendingRequests)
                {
                    if (nowTicks - kvp.Value.StartTicks > timeoutTicks)
                    {
                        if (keysToRemove == null) keysToRemove = new List<string>();
                        keysToRemove.Add(kvp.Key);
                        if (timedOut == null) timedOut = new List<PendingRequest>();
                        timedOut.Add(kvp.Value);
                    }
                }

                if (keysToRemove != null)
                {
                    foreach (var key in keysToRemove)
                        _pendingRequests.Remove(key);
                }
            }

            if (timedOut == null) return;

            foreach (var pending in timedOut)
            {
                McpLogger.LogWarning(
                    $"[TIMEOUT] Request '{pending.Method}' (id: {pending.RequestId}) " +
                    $"timed out after {REQUEST_TIMEOUT_SECONDS}s — auto-completing with error");

                // TrySetResult: safe if already completed by the tool
                pending.Tcs.TrySetResult(CreateErrorResponse(
                    $"Request timed out after {REQUEST_TIMEOUT_SECONDS} seconds. " +
                    "The tool may be stuck or Unity main thread was blocked. " +
                    "Wait a few seconds and retry.",
                    "timeout"
                ));
                // ContinueWith callback will fire → _activeRequests decremented
            }
        }

        /// <summary>
        /// Register a pending request for timeout tracking.
        /// </summary>
        private static string TrackRequest(string requestId, string method, TaskCompletionSource<JObject> tcs)
        {
            string key = requestId ?? $"_auto_{Interlocked.Increment(ref _requestCounter)}";
            lock (_pendingLock)
            {
                _pendingRequests[key] = new PendingRequest
                {
                    StartTicks = DateTimeOffset.UtcNow.Ticks,
                    Tcs = tcs,
                    Method = method,
                    RequestId = key
                };
            }
            return key;
        }

        /// <summary>
        /// Remove a completed request from timeout tracking.
        /// </summary>
        private static void UntrackRequest(string key)
        {
            lock (_pendingLock)
            {
                _pendingRequests.Remove(key);
            }
        }

        // ── OnMessage ─────────────────────────────────────────────────

        protected override void OnMessage(MessageEventArgs e)
        {
            try
            {
                McpLogger.LogInfo($"WebSocket message received: {e.Data}");
                JObject requestJson;
                try
                {
                    requestJson = JObject.Parse(e.Data);
                }
                catch (JsonReaderException jre)
                {
                    McpLogger.LogError($"Invalid JSON received: {jre.Message}. Data: {e.Data}");
                    Send(CreateResponse(null, CreateErrorResponse($"Invalid JSON format: {jre.Message}", "invalid_json")).ToString(Formatting.None));
                    return;
                }

                var method = requestJson["method"]?.ToString();
                var parameters = requestJson["params"] as JObject ?? new JObject();
                var requestId = requestJson["id"]?.ToString();

                // [SAFE GUARD] Reject if too many requests are already in flight
                int current = Interlocked.CompareExchange(ref _activeRequests, 0, 0);
                if (current >= MAX_CONCURRENT_REQUESTS)
                {
                    McpLogger.LogWarning($"Request '{method}' rejected — {current} requests already active (max {MAX_CONCURRENT_REQUESTS})");
                    Send(CreateResponse(requestId, CreateErrorResponse(
                        $"Unity is processing {current} requests. Wait 10-30 seconds and retry. Do NOT call multiple MCP tools simultaneously.",
                        "busy"
                    )).ToString(Formatting.None));
                    return;
                }

                Interlocked.Increment(ref _activeRequests);
                EnsureTimeoutChecker();

                var tcs = new TaskCompletionSource<JObject>();
                string trackingKey = TrackRequest(requestId, method, tcs);

                if (string.IsNullOrEmpty(method))
                {
                    tcs.SetResult(CreateErrorResponse("Missing method in request", "invalid_request"));
                }
                else if (_server.TryGetTool(method, out var tool))
                {
                    EditorCoroutineUtility.StartCoroutineOwnerless(ExecuteTool(tool, parameters, tcs));
                }
                else if (_server.TryGetResource(method, out var resource))
                {
                    EditorCoroutineUtility.StartCoroutineOwnerless(FetchResourceCoroutine(resource, parameters, tcs));
                }
                else
                {
                    tcs.SetResult(CreateErrorResponse($"Unknown method: {method}", "unknown_method"));
                }

                // NON-BLOCKING: respond via callback when main thread work completes.
                tcs.Task.ContinueWith(task =>
                {
                    Interlocked.Decrement(ref _activeRequests);
                    UntrackRequest(trackingKey);
                    try
                    {
                        JObject responseJson = task.IsFaulted
                            ? CreateErrorResponse($"Internal error: {task.Exception?.InnerException?.Message}", "internal_error")
                            : task.Result;
                        JObject jsonRpcResponse = CreateResponse(requestId, responseJson);
                        string responseStr = jsonRpcResponse.ToString(Formatting.None);
                        McpLogger.LogInfo($"WebSocket response for '{requestId}': {responseStr.Substring(0, Math.Min(200, responseStr.Length))}...");
                        Send(responseStr);
                    }
                    catch (Exception sendEx)
                    {
                        McpLogger.LogError($"Error sending response for '{requestId}': {sendEx.Message}");
                    }
                }, TaskScheduler.Default);
            }
            catch (Exception ex)
            {
                McpLogger.LogError($"Error processing message: {ex.Message}");
                Interlocked.Decrement(ref _activeRequests);
                try
                {
                    Send(CreateErrorResponse($"Internal server error: {ex.Message}", "internal_error").ToString(Formatting.None));
                }
                catch { /* WebSocket may already be closed */ }
            }
        }

        // ── Connection lifecycle ──────────────────────────────────────

        protected override void OnOpen()
        {
            var inactiveIds = Sessions.InactiveIDs.ToList();
            if (inactiveIds.Count > 0)
            {
                foreach (var oldId in inactiveIds)
                {
                    _server.Clients.TryRemove(oldId, out _);
                    try
                    {
                        Sessions.CloseSession(oldId, CloseStatusCode.Normal, "Stale session cleanup");
                    }
                    catch (Exception ex)
                    {
                        McpLogger.LogWarning($"Error closing stale session {oldId}: {ex.Message}");
                    }
                }
                McpLogger.LogInfo($"Cleaned up {inactiveIds.Count} inactive session(s)");
            }

            string clientName = "";
            NameValueCollection headers = Context.Headers;
            if (headers != null && headers.Contains("X-Client-Name"))
            {
                clientName = headers["X-Client-Name"];
            }

            _server.Clients[ID] = clientName;
            McpLogger.LogInfo($"WebSocket client connected (ID: {ID}, Name: {(string.IsNullOrEmpty(clientName) ? "Unknown" : clientName)}, Total clients: {_server.Clients.Count})");
        }

        protected override void OnClose(CloseEventArgs e)
        {
            _server.Clients.TryGetValue(ID, out string clientName);
            _server.Clients.TryRemove(ID, out _);
            McpLogger.LogInfo($"WebSocket client '{clientName}' disconnected: {e.Reason} (Remaining clients: {_server.Clients.Count})");
        }

        protected override void OnError(ErrorEventArgs e)
        {
            McpLogger.LogError($"WebSocket error: {e.Message}");
        }

        // ── Tool & Resource execution ─────────────────────────────────

        private IEnumerator ExecuteTool(McpToolBase tool, JObject parameters, TaskCompletionSource<JObject> tcs)
        {
            try
            {
                if (tool.IsAsync)
                {
                    tool.ExecuteAsync(parameters, tcs);
                }
                else
                {
                    var result = tool.Execute(parameters);
                    tcs.TrySetResult(result);
                }
            }
            catch (Exception ex)
            {
                McpLogger.LogError($"Error executing tool {tool.Name}: {ex.Message}\n{ex.StackTrace}");
                tcs.TrySetResult(CreateErrorResponse(
                    $"Failed to execute tool {tool.Name}: {ex.Message}",
                    "tool_execution_error"
                ));
            }

            yield return null;
        }

        private IEnumerator FetchResourceCoroutine(McpResourceBase resource, JObject parameters, TaskCompletionSource<JObject> tcs)
        {
            try
            {
                if (resource.IsAsync)
                {
                    resource.FetchAsync(parameters, tcs);
                }
                else
                {
                    var result = resource.Fetch(parameters);
                    tcs.TrySetResult(result);
                }
            }
            catch (Exception ex)
            {
                McpLogger.LogError($"Error fetching resource {resource.Name}: {ex.Message}\n{ex.StackTrace}");
                tcs.TrySetResult(CreateErrorResponse(
                    $"Failed to fetch resource {resource.Name}: {ex.Message}",
                    "resource_fetch_error"
                ));
            }
            yield return null;
        }

        private JObject CreateResponse(string requestId, JObject result)
        {
            JObject jsonRpcResponse = new JObject
            {
                ["id"] = requestId
            };

            if (result.TryGetValue("error", out var errorObj))
            {
                jsonRpcResponse["error"] = errorObj;
            }
            else
            {
                jsonRpcResponse["result"] = result;
            }

            return jsonRpcResponse;
        }
    }
}
