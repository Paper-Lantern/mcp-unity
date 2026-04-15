using UnityEditor;
using Newtonsoft.Json.Linq;

namespace McpUnity.Tools
{
    /// <summary>
    /// Lightweight query tool for Unity Editor state flags (Playing/Compiling/etc).
    /// Side-effect free; safe to call during Play Mode without risking Auto Refresh freeze.
    /// Agents should precheck this before any file-change / assembly-reload inducing call.
    /// </summary>
    public class GetEditorStateTool : McpToolBase
    {
        public GetEditorStateTool()
        {
            Name = "get_editor_state";
            Description = "Returns Unity Editor state flags (isPlaying, isPlayingOrWillChangePlaymode, " +
                          "isCompiling, isUpdating, isPaused). Safe to call anytime — no side effects, " +
                          "no Asset Pipeline triggers. Use this before execute_csharp / recompile_scripts " +
                          "to avoid Play Mode freeze.";
            IsAsync = false;
        }

        public override JObject Execute(JObject parameters)
        {
            return new JObject
            {
                ["success"] = true,
                ["type"] = "text",
                ["message"] = "Editor state snapshot",
                ["isPlaying"] = EditorApplication.isPlaying,
                ["isPlayingOrWillChangePlaymode"] = EditorApplication.isPlayingOrWillChangePlaymode,
                ["isCompiling"] = EditorApplication.isCompiling,
                ["isUpdating"] = EditorApplication.isUpdating,
                ["isPaused"] = EditorApplication.isPaused
            };
        }
    }
}
