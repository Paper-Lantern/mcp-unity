using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Unity.EditorCoroutines.Editor;
using Debug = UnityEngine.Debug;

namespace McpUnity.Tools
{
    /// <summary>
    /// Tool that compiles and executes arbitrary C# code in the Unity Editor using Roslyn.
    /// Phase 1 (main thread): generate source files.
    /// Phase 2 (ThreadPool): run csc.exe — main thread stays responsive.
    /// Phase 3 (main thread): load assembly + invoke.
    /// </summary>
    public class ExecuteCsharpTool : McpToolBase
    {
        private const int CompileTimeoutMs = 15000;
        private const int MaxCollectionItems = 100;
        private const int MaxSerializeDepth = 3;

        private static readonly string[] DefaultUsings =
        {
            "System",
            "System.Linq",
            "System.Collections.Generic",
            "System.IO",
            "System.Text",
            "UnityEngine",
            "UnityEditor"
        };

        public ExecuteCsharpTool()
        {
            Name = "execute_csharp";
            Description = "Compiles and executes arbitrary C# code in the Unity Editor. " +
                          "Returns the result of the last expression. " +
                          "The code body is placed inside a static method — write statements and end with a return.";
            IsAsync = true;
        }

        public override void ExecuteAsync(JObject parameters, TaskCompletionSource<JObject> tcs)
        {
            try
            {
                var code = parameters?["code"]?.ToString();
                if (string.IsNullOrWhiteSpace(code))
                {
                    tcs.SetResult(CreateErrorResponse("Parameter 'code' is required and must not be empty."));
                    return;
                }

                var extraUsings = new List<string>();
                if (parameters?["usings"] is JArray usingsArray)
                {
                    foreach (var u in usingsArray)
                        extraUsings.Add(u.ToString());
                }

                // [SAFE GUARD] Play Mode freeze prevention — block file-change / assembly-reload inducing APIs.
                // Pure read-only reflection code (e.g. ArchitectureVerifier.VerifyAll) still passes.
                if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
                {
                    string[] forbiddenApis = {
                        "CompilationPipeline.RequestScriptCompilation",
                        "AssetDatabase.Refresh",
                        "AssetDatabase.ImportAsset",
                        "AssetDatabase.SaveAssets",
                        "AssetDatabase.ForceReserializeAssets",
                        "EditorApplication.ExecuteMenuItem"
                    };
                    foreach (var forbidden in forbiddenApis)
                    {
                        if (code.IndexOf(forbidden, StringComparison.Ordinal) >= 0)
                        {
                            tcs.SetResult(new JObject
                            {
                                ["success"] = false,
                                ["type"] = "text",
                                ["code"] = "PLAYING_SKIP",
                                ["message"] = $"PLAYING_SKIP: forbidden API '{forbidden}' while isPlaying. Exit Play Mode first."
                            });
                            return;
                        }
                    }
                }

                // Dispatch to coroutine so Phase 2 (csc process) runs on ThreadPool
                EditorCoroutineUtility.StartCoroutineOwnerless(
                    CompileAndExecuteCoroutine(code, extraUsings, tcs));
            }
            catch (Exception ex)
            {
                tcs.TrySetResult(CreateErrorResponse($"Unexpected error: {ex.Message}"));
            }
        }

        private IEnumerator CompileAndExecuteCoroutine(string userCode, List<string> extraUsings,
            TaskCompletionSource<JObject> tcs)
        {
            // ── Phase 1: Generate source (main thread, fast) ──────────
            var uniqueId = Guid.NewGuid().ToString("N").Substring(0, 8);
            var className = $"__McpDynamic_{uniqueId}";
            var tempDir = Path.GetTempPath();
            var srcPath = Path.Combine(tempDir, $"{className}.cs");
            var dllPath = Path.Combine(tempDir, $"{className}.dll");
            var rspPath = Path.Combine(tempDir, $"{className}.rsp");
            var pdbPath = Path.Combine(tempDir, $"{className}.pdb");

            var (cscExe, cscArgs) = FindCsc();
            if (cscExe == null)
            {
                tcs.TrySetResult(CreateErrorResponse("Could not find Roslyn compiler (csc) in Unity installation."));
                yield break;
            }

            // Build source
            var allUsings = DefaultUsings.Concat(extraUsings).Distinct();
            var sb = new StringBuilder();
            foreach (var u in allUsings)
                sb.AppendLine($"using {u};");
            sb.AppendLine();
            sb.AppendLine($"public static class {className}");
            sb.AppendLine("{");
            sb.AppendLine("    public static object Execute()");
            sb.AppendLine("    {");
            sb.AppendLine($"        {userCode}");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            File.WriteAllText(srcPath, sb.ToString(), Encoding.UTF8);

            // Build RSP
            var rspContent = new StringBuilder();
            rspContent.AppendLine("/noconfig");
            rspContent.AppendLine("/target:library");
            rspContent.AppendLine("/nologo");
            rspContent.AppendLine("/nowarn:CS0162,CS0219,CS0168");
            rspContent.AppendLine($"/out:\"{dllPath}\"");
            rspContent.AppendLine("/unsafe");

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (asm.IsDynamic) continue;
                    var loc = asm.Location;
                    if (string.IsNullOrEmpty(loc) || !File.Exists(loc)) continue;
                    rspContent.AppendLine($"/r:\"{loc}\"");
                }
                catch { }
            }
            rspContent.AppendLine($"\"{srcPath}\"");
            File.WriteAllText(rspPath, rspContent.ToString(), Encoding.UTF8);

            var fullArgs = string.IsNullOrEmpty(cscArgs)
                ? $"@\"{rspPath}\""
                : $"{cscArgs} @\"{rspPath}\"";

            // ── Phase 2: Run csc on ThreadPool (main thread free) ─────
            string stdout = null, stderr = null;
            int exitCode = -1;
            bool timedOut = false;
            int compileDone = 0; // 0 = running, 1 = done

            string capturedCscExe = cscExe;
            string capturedFullArgs = fullArgs;
            string capturedTempDir = tempDir;

            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = capturedCscExe,
                        Arguments = capturedFullArgs,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        WorkingDirectory = capturedTempDir
                    };

                    using (var proc = Process.Start(psi))
                    {
                        stdout = proc.StandardOutput.ReadToEnd();
                        stderr = proc.StandardError.ReadToEnd();
                        if (!proc.WaitForExit(CompileTimeoutMs))
                        {
                            try { proc.Kill(); } catch { }
                            timedOut = true;
                        }
                        else
                        {
                            exitCode = proc.ExitCode;
                        }
                    }
                }
                catch (Exception ex)
                {
                    stderr = ex.Message;
                }
                finally
                {
                    Interlocked.Exchange(ref compileDone, 1);
                }
            });

            // Yield until compilation completes — main thread stays responsive
            while (Interlocked.CompareExchange(ref compileDone, 0, 0) == 0)
            {
                yield return null;
            }

            // ── Phase 3: Load + invoke (main thread, required for Unity API) ──
            try
            {
                if (timedOut)
                {
                    tcs.TrySetResult(CreateErrorResponse("Compilation timed out (15s limit)."));
                    yield break;
                }

                if (exitCode != 0)
                {
                    var errors = ParseCompileErrors(stdout + "\n" + stderr);
                    tcs.TrySetResult(CreateErrorResponse($"Compile error:\n{errors}"));
                    yield break;
                }

                if (!File.Exists(dllPath))
                {
                    tcs.TrySetResult(CreateErrorResponse($"Compilation produced no output. stderr: {stderr}"));
                    yield break;
                }

                var asmBytes = File.ReadAllBytes(dllPath);
                var loadedAsm = Assembly.Load(asmBytes);
                var type = loadedAsm.GetType(className);
                if (type == null)
                {
                    tcs.TrySetResult(CreateErrorResponse($"Could not find type '{className}' in compiled assembly."));
                    yield break;
                }

                var method = type.GetMethod("Execute", BindingFlags.Public | BindingFlags.Static);
                if (method == null)
                {
                    tcs.TrySetResult(CreateErrorResponse("Could not find 'Execute' method in compiled type."));
                    yield break;
                }

                object returnValue;
                try
                {
                    returnValue = method.Invoke(null, null);
                }
                catch (TargetInvocationException tie)
                {
                    var inner = tie.InnerException ?? tie;
                    tcs.TrySetResult(CreateErrorResponse($"Runtime error: {inner.GetType().Name}: {inner.Message}\n{inner.StackTrace}"));
                    yield break;
                }

                var serialized = Serialize(returnValue, 0);
                tcs.TrySetResult(new JObject
                {
                    ["success"] = true,
                    ["type"] = "text",
                    ["message"] = "Code executed successfully.",
                    ["result"] = serialized is JToken jt ? jt : new JValue(serialized?.ToString() ?? "(null)")
                });
            }
            catch (Exception ex)
            {
                tcs.TrySetResult(CreateErrorResponse($"Unexpected error: {ex.Message}"));
            }
            finally
            {
                TryDelete(srcPath);
                TryDelete(dllPath);
                TryDelete(rspPath);
                TryDelete(pdbPath);
            }
        }

        private static (string exe, string args) FindCsc()
        {
            var roslynDir = Path.Combine(EditorApplication.applicationContentsPath, "DotNetSdkRoslyn");

            if (Application.platform == RuntimePlatform.WindowsEditor)
            {
                var cscExe = Path.Combine(roslynDir, "csc.exe");
                if (File.Exists(cscExe))
                    return (cscExe, null);
            }

            var cscDll = Path.Combine(roslynDir, "csc.dll");
            if (File.Exists(cscDll))
            {
                var dotnet = FindDotnet();
                if (dotnet != null)
                    return (dotnet, $"exec \"{cscDll}\"");
            }

            return (null, null);
        }

        private static string FindDotnet()
        {
            var unityDotnet = Path.Combine(EditorApplication.applicationContentsPath, "dotnet");
            if (Application.platform == RuntimePlatform.WindowsEditor)
            {
                var exe = Path.Combine(unityDotnet, "dotnet.exe");
                if (File.Exists(exe)) return exe;
            }
            else
            {
                var exe = Path.Combine(unityDotnet, "dotnet");
                if (File.Exists(exe)) return exe;
            }
            return "dotnet";
        }

        private static string ParseCompileErrors(string output)
        {
            if (string.IsNullOrEmpty(output)) return "(no output)";

            var lines = output.Split('\n');
            var errors = new List<string>();

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.Contains("error CS") || trimmed.Contains("warning CS"))
                    errors.Add(trimmed);
            }

            return errors.Count > 0 ? string.Join("\n", errors) : output.Trim();
        }

        private static object Serialize(object value, int depth)
        {
            if (value == null) return null;
            if (depth > MaxSerializeDepth) return value.ToString();

            if (value is bool || value is int || value is long || value is float || value is double || value is string)
                return value;

            if (value is Enum) return value.ToString();

            if (value is UnityEngine.Object uObj)
            {
                return new JObject
                {
                    ["_type"] = uObj.GetType().Name,
                    ["name"] = uObj.name,
                    ["instanceId"] = uObj.GetInstanceID()
                };
            }

            if (value is System.Collections.IDictionary dict)
            {
                var jObj = new JObject();
                int count = 0;
                foreach (System.Collections.DictionaryEntry entry in dict)
                {
                    if (count++ >= MaxCollectionItems)
                    {
                        jObj[$"...(truncated, {dict.Count} total)"] = true;
                        break;
                    }
                    jObj[entry.Key.ToString()] = JToken.FromObject(Serialize(entry.Value, depth + 1) ?? "null");
                }
                return jObj;
            }

            if (value is System.Collections.IEnumerable enumerable && !(value is string))
            {
                var jArr = new JArray();
                int count = 0;
                foreach (var item in enumerable)
                {
                    if (count++ >= MaxCollectionItems)
                    {
                        jArr.Add($"...(truncated at {MaxCollectionItems})");
                        break;
                    }
                    var serialized = Serialize(item, depth + 1);
                    jArr.Add(serialized != null ? JToken.FromObject(serialized) : JValue.CreateNull());
                }
                return jArr;
            }

            var type = value.GetType();
            if (type.IsPrimitive || type == typeof(decimal)) return value;

            var result = new JObject();
            result["_type"] = type.Name;

            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                try
                {
                    var fieldVal = field.GetValue(value);
                    result[field.Name] = JToken.FromObject(Serialize(fieldVal, depth + 1) ?? "null");
                }
                catch { }
            }

            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!prop.CanRead || prop.GetIndexParameters().Length > 0) continue;
                try
                {
                    var propVal = prop.GetValue(value);
                    result[prop.Name] = JToken.FromObject(Serialize(propVal, depth + 1) ?? "null");
                }
                catch { }
            }

            return result;
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private static JObject CreateErrorResponse(string message)
        {
            return new JObject
            {
                ["success"] = false,
                ["type"] = "text",
                ["message"] = message
            };
        }
    }
}
