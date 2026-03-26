using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace McpUnity.Tools
{
    /// <summary>
    /// Tool that compiles and executes arbitrary C# code in the Unity Editor using Roslyn.
    /// Enables AI agents to access any Unity API not covered by existing MCP tools.
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

                var result = CompileAndExecute(code, extraUsings);
                tcs.SetResult(result);
            }
            catch (Exception ex)
            {
                tcs.SetResult(CreateErrorResponse($"Unexpected error: {ex.Message}"));
            }
        }

        private JObject CompileAndExecute(string userCode, List<string> extraUsings)
        {
            // Generate unique names to avoid assembly conflicts
            var uniqueId = Guid.NewGuid().ToString("N").Substring(0, 8);
            var className = $"__McpDynamic_{uniqueId}";
            var tempDir = Path.GetTempPath();
            var srcPath = Path.Combine(tempDir, $"{className}.cs");
            var dllPath = Path.Combine(tempDir, $"{className}.dll");
            var rspPath = Path.Combine(tempDir, $"{className}.rsp");
            var pdbPath = Path.Combine(tempDir, $"{className}.pdb");

            try
            {
                // 1. Build source code with usings + wrapper class
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

                // 2. Find Roslyn csc
                var (cscExe, cscArgs) = FindCsc();
                if (cscExe == null)
                {
                    return CreateErrorResponse("Could not find Roslyn compiler (csc) in Unity installation.");
                }

                // 3. Build .rsp file with assembly references
                var rspContent = new StringBuilder();
                rspContent.AppendLine("/noconfig");
                rspContent.AppendLine("/target:library");
                rspContent.AppendLine("/nologo");
                rspContent.AppendLine("/nowarn:CS0162,CS0219,CS0168"); // unreachable, unused var, declared not used
                rspContent.AppendLine($"/out:\"{dllPath}\"");
                rspContent.AppendLine("/unsafe"); // allow unsafe if needed

                // Collect assembly references
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        if (asm.IsDynamic) continue;
                        var loc = asm.Location;
                        if (string.IsNullOrEmpty(loc)) continue;
                        if (!File.Exists(loc)) continue;
                        rspContent.AppendLine($"/r:\"{loc}\"");
                    }
                    catch
                    {
                        // Skip assemblies that throw on Location access
                    }
                }

                rspContent.AppendLine($"\"{srcPath}\"");
                File.WriteAllText(rspPath, rspContent.ToString(), Encoding.UTF8);

                // 4. Run csc.exe
                var fullArgs = string.IsNullOrEmpty(cscArgs)
                    ? $"@\"{rspPath}\""
                    : $"{cscArgs} @\"{rspPath}\"";

                var psi = new ProcessStartInfo
                {
                    FileName = cscExe,
                    Arguments = fullArgs,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = tempDir
                };

                string stdout, stderr;
                int exitCode;

                using (var proc = Process.Start(psi))
                {
                    stdout = proc.StandardOutput.ReadToEnd();
                    stderr = proc.StandardError.ReadToEnd();

                    if (!proc.WaitForExit(CompileTimeoutMs))
                    {
                        try { proc.Kill(); } catch { }
                        return CreateErrorResponse("Compilation timed out (15s limit).");
                    }

                    exitCode = proc.ExitCode;
                }

                if (exitCode != 0)
                {
                    var errors = ParseCompileErrors(stdout + "\n" + stderr);
                    return CreateErrorResponse($"Compile error:\n{errors}");
                }

                if (!File.Exists(dllPath))
                {
                    return CreateErrorResponse($"Compilation produced no output. stderr: {stderr}");
                }

                // 5. Load and execute
                var asmBytes = File.ReadAllBytes(dllPath);
                var loadedAsm = Assembly.Load(asmBytes);
                var type = loadedAsm.GetType(className);
                if (type == null)
                {
                    return CreateErrorResponse($"Could not find type '{className}' in compiled assembly.");
                }

                var method = type.GetMethod("Execute", BindingFlags.Public | BindingFlags.Static);
                if (method == null)
                {
                    return CreateErrorResponse("Could not find 'Execute' method in compiled type.");
                }

                object returnValue;
                try
                {
                    returnValue = method.Invoke(null, null);
                }
                catch (TargetInvocationException tie)
                {
                    var inner = tie.InnerException ?? tie;
                    return CreateErrorResponse($"Runtime error: {inner.GetType().Name}: {inner.Message}\n{inner.StackTrace}");
                }

                // 6. Serialize result
                var serialized = Serialize(returnValue, 0);

                return new JObject
                {
                    ["success"] = true,
                    ["type"] = "text",
                    ["message"] = "Code executed successfully.",
                    ["result"] = serialized is JToken jt ? jt : new JValue(serialized?.ToString() ?? "(null)")
                };
            }
            finally
            {
                // Cleanup temp files
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

            // macOS / Linux: use dotnet exec csc.dll
            var cscDll = Path.Combine(roslynDir, "csc.dll");
            if (File.Exists(cscDll))
            {
                // Try to find dotnet
                var dotnet = FindDotnet();
                if (dotnet != null)
                    return (dotnet, $"exec \"{cscDll}\"");
            }

            return (null, null);
        }

        private static string FindDotnet()
        {
            // Check Unity's dotnet first
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

            // Fallback to PATH
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
                {
                    // Adjust line numbers: subtract the wrapper lines (usings + class + method = ~10 lines)
                    errors.Add(trimmed);
                }
            }

            return errors.Count > 0 ? string.Join("\n", errors) : output.Trim();
        }

        private static object Serialize(object value, int depth)
        {
            if (value == null) return null;
            if (depth > MaxSerializeDepth) return value.ToString();

            // Primitives and strings
            if (value is bool || value is int || value is long || value is float || value is double || value is string)
                return value;

            // Enum
            if (value is Enum) return value.ToString();

            // Unity Object — avoid full serialization
            if (value is UnityEngine.Object uObj)
            {
                return new JObject
                {
                    ["_type"] = uObj.GetType().Name,
                    ["name"] = uObj.name,
                    ["instanceId"] = uObj.GetInstanceID()
                };
            }

            // Dictionary
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

            // Enumerable (arrays, lists, etc.)
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

            // Generic object — reflect public fields and properties
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
