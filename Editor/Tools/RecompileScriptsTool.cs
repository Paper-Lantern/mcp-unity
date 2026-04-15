using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using McpUnity.Utils;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace McpUnity.Tools {
    /// <summary>
    /// Tool to recompile all scripts in the Unity project.
    /// Returns immediately with "compilation_triggered" — does NOT wait for compilation.
    /// Domain Reload destroys all C# state, so holding a TCS across compilation is impossible.
    /// The client should wait 30-60s then call get_console_logs to check results.
    /// </summary>
    public class RecompileScriptsTool : McpToolBase
    {
        /// <summary>
        /// Tracks whether a compilation cycle is actively in progress (from our request).
        /// Prevents re-entrant AssetDatabase.Refresh + RequestScriptCompilation calls.
        /// Uses Interlocked for thread safety (read from WebSocket thread, write from main thread).
        /// </summary>
        private static int _compilationInProgress = 0; // 0=false, 1=true

        public RecompileScriptsTool()
        {
            Name = "recompile_scripts";
            Description = "Recompiles all scripts in the Unity project";
            IsAsync = true;
        }

        public override void ExecuteAsync(JObject parameters, TaskCompletionSource<JObject> tcs)
        {
            var returnWithLogs = GetBoolParameter(parameters, "returnWithLogs", true);
            var logsLimit = Mathf.Clamp(GetIntParameter(parameters, "logsLimit", 100), 0, 1000);

            // [SAFE GUARD] Play Mode — Auto Refresh + assembly reload during Play causes freeze
            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                McpLogger.LogWarning("recompile_scripts called during Play Mode — blocked to prevent freeze");
                tcs.SetResult(new JObject
                {
                    ["success"] = false,
                    ["type"] = "text",
                    ["code"] = "PLAYING_SKIP",
                    ["message"] = "Cannot recompile while Unity is in Play Mode (Asset Pipeline Auto Refresh + assembly reload causes freeze). Exit Play Mode first.",
                    ["blocked"] = "playing"
                });
                return;
            }

            // [SAFE GUARD] Already compiling — return busy immediately
            if (EditorApplication.isCompiling ||
                System.Threading.Interlocked.CompareExchange(ref _compilationInProgress, 0, 0) == 1)
            {
                McpLogger.LogInfo("Compilation already in progress — returning busy status");
                tcs.SetResult(new JObject
                {
                    ["success"] = true,
                    ["type"] = "text",
                    ["message"] = "⏳ Unity is currently compiling. Do NOT retry — wait 30-60 seconds and try again. " +
                                  "Calling recompile_scripts while compilation is in progress causes Unity to become unresponsive.",
                    ["logs"] = new JArray(),
                    ["compiling"] = true
                });
                return;
            }

            // Return immediately — compilation will proceed asynchronously.
            // Domain Reload will destroy this instance and drop the WebSocket connection.
            // The client should wait and then check get_console_logs for results.
            tcs.SetResult(new JObject
            {
                ["success"] = true,
                ["type"] = "text",
                ["message"] = "Compilation triggered. Unity will recompile scripts and perform a domain reload. " +
                              "Wait 30-60 seconds, then check results with get_console_logs(logType='error'). " +
                              "The WebSocket connection will drop during domain reload and auto-reconnect." +
                              (returnWithLogs ? $" (returnWithLogs: true, logsLimit: {logsLimit} — use get_console_logs after reload)" : ""),
                ["logs"] = new JArray(),
                ["compiling"] = true,
                ["action"] = "compilation_triggered"
            });

            // Now trigger compilation (will cause domain reload if scripts changed)
            System.Threading.Interlocked.Exchange(ref _compilationInProgress, 1);
            StartCompilationTracking();

            McpLogger.LogInfo("Refreshing AssetDatabase to detect external file changes...");
            AssetDatabase.Refresh();

            McpLogger.LogInfo("Recompiling all scripts in the Unity project");
            CompilationPipeline.RequestScriptCompilation();
        }

        // ── Compilation tracking (for logging only, not for TCS) ──────

        private readonly List<CompilerMessage> _compilationLogs = new List<CompilerMessage>();
        private int _processedAssemblies = 0;

        private void StartCompilationTracking()
        {
            _compilationLogs.Clear();
            _processedAssemblies = 0;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
            CompilationPipeline.compilationFinished += OnCompilationFinished;
        }

        private void StopCompilationTracking()
        {
            CompilationPipeline.assemblyCompilationFinished -= OnAssemblyCompilationFinished;
            CompilationPipeline.compilationFinished -= OnCompilationFinished;
        }

        private void OnAssemblyCompilationFinished(string assemblyPath, CompilerMessage[] messages)
        {
            _processedAssemblies++;
            _compilationLogs.AddRange(messages);
        }

        /// <summary>
        /// Fires after compilation completes (before domain reload if scripts changed).
        /// Stores results in SessionState for post-reload retrieval.
        /// Does NOT try to complete any TCS — it was already completed in ExecuteAsync.
        /// </summary>
        private void OnCompilationFinished(object _)
        {
            int errorsCount = _compilationLogs.Count(l => l.type == CompilerMessageType.Error);
            int warningsCount = _compilationLogs.Count(l => l.type == CompilerMessageType.Warning);

            McpLogger.LogInfo(
                $"Recompilation completed. Processed {_processedAssemblies} assemblies: " +
                $"{errorsCount} error(s), {warningsCount} warning(s)");

            // Persist in SessionState (survives domain reload)
            SessionState.SetBool("McpUnity_LastCompilationHadErrors", errorsCount > 0);
            SessionState.SetInt("McpUnity_LastCompilationErrorCount", errorsCount);
            SessionState.SetInt("McpUnity_LastCompilationWarningCount", warningsCount);

            System.Threading.Interlocked.Exchange(ref _compilationInProgress, 0);
            StopCompilationTracking();
        }

    }
}
