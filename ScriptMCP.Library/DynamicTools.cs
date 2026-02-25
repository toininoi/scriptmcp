using System.ComponentModel;
using System.Reflection;
using System.Reflection.Metadata;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Server;

namespace ScriptMCP.Library;

// ── Serialization models ──────────────────────────────────────────────────────

public class DynParam
{
    [JsonPropertyName("Name")]        public string Name        { get; set; } = "";
    [JsonPropertyName("Type")]        public string Type        { get; set; } = "string";
    [JsonPropertyName("Description")] public string Description { get; set; } = "";
}

public class DynamicFunction
{
    [JsonPropertyName("Name")]                public string        Name                { get; set; } = "";
    [JsonPropertyName("Description")]         public string        Description         { get; set; } = "";
    [JsonPropertyName("Parameters")]          public List<DynParam> Parameters         { get; set; } = new();
    [JsonPropertyName("FunctionType")]        public string        FunctionType        { get; set; } = "code";
    [JsonPropertyName("Body")]                public string        Body                { get; set; } = "";
    [JsonPropertyName("OutputInstructions")]  public string?       OutputInstructions  { get; set; }
}

// ── DynamicTools ──────────────────────────────────────────────────────────────

public class DynamicTools
{
    private static bool _initialized;
    private static readonly object _initLock = new();

    // ── Lazy-compiled ScriptMCP helper assembly ────────────────────────────────
    private static readonly Lazy<(byte[] bytes, MetadataReference reference)> _helperAssembly = new(CompileHelperAssembly);

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Path to the SQLite database file. Set by McpConstants.ResolveSavePath().
    /// </summary>
    public static string SavePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ScriptMCP",
        "tools.db");

    private static string ConnectionString => $"Data Source={SavePath}";

    public DynamicTools() => Initialize();

    // ── Initialization ────────────────────────────────────────────────────────

    private void Initialize()
    {
        lock (_initLock)
        {
            if (_initialized) return;
            _initialized = true;

            EnsureDatabase();
            MigrateFromJson();
            PreloadAssemblies();
        }
    }

    /// <summary>
    /// Explicitly load key assemblies into the AppDomain so Roslyn can resolve
    /// forwarded types in single-file publish mode (Strategy 2).
    /// </summary>
    private static void PreloadAssemblies()
    {
        // Touch types to load their declaring assemblies
        _ = typeof(System.Net.Http.HttpClient);
        _ = typeof(System.Net.HttpStatusCode);
        _ = typeof(System.Text.Json.JsonDocument);
        _ = typeof(System.Text.RegularExpressions.Regex);
        _ = typeof(System.Diagnostics.Process);
        _ = typeof(System.Globalization.CultureInfo);

        // Explicitly load forwarding assemblies that Roslyn needs for type resolution
        foreach (var name in new[]
        {
            "System.Net.Http",
            "System.Net.Primitives",
            "System.Text.Json",
            "System.Diagnostics.Process",
            "System.Collections",
            "System.Linq",
            "System.Runtime",
        })
        {
            try { Assembly.Load(name); } catch { }
        }
    }

    private static void EnsureDatabase()
    {
        var dir = Path.GetDirectoryName(SavePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS functions (
                name                TEXT PRIMARY KEY COLLATE NOCASE,
                description         TEXT NOT NULL,
                parameters          TEXT NOT NULL,
                function_type       TEXT NOT NULL DEFAULT 'code',
                body                TEXT NOT NULL,
                compiled_assembly   BLOB,
                output_instructions TEXT
            );";
        cmd.ExecuteNonQuery();

        // Migrate: add output_instructions column if missing (existing DBs)
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA table_info(functions)";
        bool hasOutputInstructions = false;
        using (var reader = pragma.ExecuteReader())
        {
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), "output_instructions", StringComparison.OrdinalIgnoreCase))
                { hasOutputInstructions = true; break; }
            }
        }
        if (!hasOutputInstructions)
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE functions ADD COLUMN output_instructions TEXT";
            alter.ExecuteNonQuery();
        }
    }

    private void MigrateFromJson()
    {
        // Look for JSON file in same directory or parent directory
        var dbDir = Path.GetDirectoryName(SavePath) ?? Directory.GetCurrentDirectory();
        var jsonPath = Path.Combine(dbDir, "dynamic_functions.json");
        if (!File.Exists(jsonPath))
        {
            var parentJson = Path.GetFullPath(Path.Combine(dbDir, "..", "dynamic_functions.json"));
            if (File.Exists(parentJson))
                jsonPath = parentJson;
            else
                return;
        }

        // Only migrate if DB is empty
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();

        using (var countCmd = conn.CreateCommand())
        {
            countCmd.CommandText = "SELECT COUNT(*) FROM functions";
            var count = (long)countCmd.ExecuteScalar()!;
            if (count > 0) return;
        }

        try
        {
            var json = File.ReadAllText(jsonPath);
            var funcs = JsonSerializer.Deserialize<List<DynamicFunction>>(json, ReadOptions);
            if (funcs == null || funcs.Count == 0) return;

            int migrated = 0;
            foreach (var func in funcs)
            {
                byte[]? assemblyBytes = null;
                if (!IsInstructions(func))
                {
                    var (bytes, errors) = CompileFunction(func);
                    if (bytes == null)
                    {
                        Console.Error.WriteLine($"Migration: failed to compile '{func.Name}': {errors}");
                        // Store without compiled assembly — will fail at call time but data is preserved
                    }
                    assemblyBytes = bytes;
                }

                InsertFunction(conn, func, assemblyBytes);
                migrated++;
            }

            Console.Error.WriteLine($"Migrated {migrated} function(s) from {jsonPath} to SQLite.");

            // Rename old JSON file
            var backupPath = jsonPath + ".migrated";
            try { File.Move(jsonPath, backupPath, overwrite: true); }
            catch { /* best effort */ }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"JSON migration failed: {ex.Message}");
        }
    }

    // ── Listing ───────────────────────────────────────────────────────────────

    [McpServerTool(Name = "list_dynamic_functions")]
    [Description("Lists all registered dynamic functions with their name, description, and parameter signatures")]
    public string ListDynamicFunctions()
    {
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name, description, parameters, function_type FROM functions";

        using var reader = cmd.ExecuteReader();
        var sb = new StringBuilder();
        bool any = false;

        while (reader.Read())
        {
            any = true;
            var name = reader.GetString(0);
            var description = reader.GetString(1);
            var parametersJson = reader.GetString(2);
            var functionType = reader.GetString(3);
            var isInstr = string.Equals(functionType, "instructions", StringComparison.OrdinalIgnoreCase);

            var dynParams = JsonSerializer.Deserialize<List<DynParam>>(parametersJson, ReadOptions)
                            ?? new List<DynParam>();

            sb.AppendLine($"Name: {name}");
            sb.AppendLine($"  Type: {functionType} → " +
                          (isInstr
                              ? "read and follow the instructions inside"
                              : "execute with call_dynamic_function"));
            sb.AppendLine($"  Description: {description}");

            if (dynParams.Count == 0)
            {
                sb.AppendLine("  Parameters: (none)");
            }
            else
            {
                sb.AppendLine("  Parameters:");
                foreach (var p in dynParams)
                    sb.AppendLine($"    - {p.Name} ({p.Type}): {p.Description}");
            }
            sb.AppendLine();
        }

        return any ? sb.ToString().TrimEnd() : "No dynamic functions registered.";
    }

    // ── Deletion ──────────────────────────────────────────────────────────────

    [McpServerTool(Name = "delete_dynamic_function")]
    [Description("Deletes a registered dynamic function from the database by name")]
    public string DeleteDynamicFunction(
        [Description("The name of the dynamic function to delete")] string name)
    {
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM functions WHERE name = @name";
        cmd.Parameters.AddWithValue("@name", name);

        var rows = cmd.ExecuteNonQuery();
        return rows > 0
            ? $"Function '{name}' deleted successfully."
            : $"Function '{name}' not found.";
    }

    // ── Inspection ─────────────────────────────────────────────────────────────

    [McpServerTool(Name = "inspect_dynamic_function")]
    [Description("Inspects a registered dynamic function and returns its full details including source code, parameters, and metadata in a pretty-printed format")]
    public string InspectDynamicFunction(
        [Description("The name of the dynamic function to inspect")] string name)
    {
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name, description, parameters, function_type, body, compiled_assembly, output_instructions FROM functions WHERE name = @name";
        cmd.Parameters.AddWithValue("@name", name);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return $"Function '{name}' not found. Use list_dynamic_functions to see available functions.";

        var funcName            = reader.GetString(0);
        var description         = reader.GetString(1);
        var parametersJson      = reader.GetString(2);
        var functionType        = reader.GetString(3);
        var body                = reader.GetString(4);
        var hasAssembly         = !reader.IsDBNull(5);
        var outputInstructions  = reader.IsDBNull(6) ? null : reader.GetString(6);

        var dynParams = JsonSerializer.Deserialize<List<DynParam>>(parametersJson, ReadOptions)
                        ?? new List<DynParam>();

        var isInstr = string.Equals(functionType, "instructions", StringComparison.OrdinalIgnoreCase);

        var sb = new StringBuilder();
        sb.AppendLine($"Function: {funcName}");
        sb.AppendLine($"Type:        {functionType}");
        sb.AppendLine($"Description: {description}");
        sb.AppendLine($"Compiled:    {(isInstr ? "N/A (instructions)" : hasAssembly ? "Yes" : "No (missing assembly)")}");
        sb.AppendLine();

        if (dynParams.Count == 0)
        {
            sb.AppendLine("Parameters: (none)");
        }
        else
        {
            sb.AppendLine("Parameters:");
            foreach (var p in dynParams)
                sb.AppendLine($"  - {p.Name} ({p.Type}): {p.Description}");
        }

        sb.AppendLine();
        sb.AppendLine($"Source ({(isInstr ? "Instructions" : "C# Code")}):");

        var lines = body.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var lineNum = (i + 1).ToString().PadLeft(3);
            sb.AppendLine($"  {lineNum} | {lines[i].TrimEnd('\r')}");
        }

        if (!string.IsNullOrWhiteSpace(outputInstructions))
        {
            sb.AppendLine();
            sb.AppendLine($"Output Instructions: {outputInstructions}");
        }

        return sb.ToString().TrimEnd();
    }

    // ── Registration ──────────────────────────────────────────────────────────

    [McpServerTool(Name = "register_dynamic_function")]
    [Description("Registers a new dynamic function that can be called later. Use functionType 'instructions' " +
                 "for plain English instructions (supports {paramName} substitution). " +
                 "Use functionType 'code' for C# script bodies that are compiled and executed at runtime via Roslyn.")]
    public string RegisterDynamicFunction(
        [Description("Function name")] string name,
        [Description("Description of what the function does")] string description,
        [Description("JSON array of parameters, e.g. [{\"name\":\"x\",\"type\":\"int\",\"description\":\"The number\"}]")]
            string parameters,
        [Description("Plain English instructions (supports {paramName} substitution) or C# body depending on functionType")]
            string body,
        [Description("Function type: 'instructions' for plain English (recommended), or 'code' for C# (compiled at runtime)")]
            string functionType = "instructions",
        [Description("Optional instructions for how to present/format the output after execution (e.g. 'present as a markdown table', 'summarize in bullet points')")]
            string outputInstructions = "")
    {
        try
        {
            var dynParams = JsonSerializer.Deserialize<List<DynParam>>(parameters, ReadOptions)
                            ?? new List<DynParam>();

            var func = new DynamicFunction
            {
                Name                = name,
                Description         = description,
                Parameters          = dynParams,
                FunctionType        = functionType ?? "instructions",
                Body                = body,
                OutputInstructions  = string.IsNullOrWhiteSpace(outputInstructions) ? null : outputInstructions,
            };

            byte[]? assemblyBytes = null;

            if (!IsInstructions(func))
            {
                var (bytes, errors) = CompileFunction(func);
                if (bytes == null)
                    return $"Compilation failed:\n{errors}";
                assemblyBytes = bytes;
            }

            using var conn = new SqliteConnection(ConnectionString);
            conn.Open();
            InsertFunction(conn, func, assemblyBytes);

            return $"{(IsInstructions(func) ? "Instructions" : "Code")} function '{func.Name}' registered successfully " +
                   $"with {func.Parameters.Count} parameter(s).";
        }
        catch (Exception ex)
        {
            return $"Registration failed: {ex.Message}";
        }
    }

    // ── Compilation ──────────────────────────────────────────────────────────

    [McpServerTool(Name = "compile_dynamic_function")]
    [Description("Compiles a registered code function from its stored source. " +
                 "Use this after a ScriptMCP update to rebuild functions against the latest runtime.")]
    public string CompileDynamicFunction(
        [Description("The name of the dynamic function to recompile")] string name)
    {
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();

        using var readCmd = conn.CreateCommand();
        readCmd.CommandText = "SELECT parameters, function_type, body FROM functions WHERE name = @name";
        readCmd.Parameters.AddWithValue("@name", name);

        using var reader = readCmd.ExecuteReader();
        if (!reader.Read())
            return $"Function '{name}' not found.";

        var parametersJson = reader.GetString(0);
        var functionType   = reader.GetString(1);
        var body           = reader.GetString(2);
        reader.Close();

        if (string.Equals(functionType, "instructions", StringComparison.OrdinalIgnoreCase))
            return $"Function '{name}' is an instructions function — nothing to compile.";

        var dynParams = JsonSerializer.Deserialize<List<DynParam>>(parametersJson, ReadOptions)
                        ?? new List<DynParam>();

        var func = new DynamicFunction
        {
            Name         = name,
            FunctionType = functionType,
            Body         = body,
            Parameters   = dynParams,
        };

        var (bytes, errors) = CompileFunction(func);
        if (bytes == null)
            return $"Recompilation failed:\n{errors}";

        using var updateCmd = conn.CreateCommand();
        updateCmd.CommandText = "UPDATE functions SET compiled_assembly = @asm WHERE name = @name";
        updateCmd.Parameters.AddWithValue("@name", name);
        updateCmd.Parameters.AddWithValue("@asm", bytes);
        updateCmd.ExecuteNonQuery();

        return $"Function '{name}' recompiled successfully.";
    }

    // ── Save (kept for backward compatibility but is now a no-op) ─────────────

    [McpServerTool(Name = "save_dynamic_functions")]
    [Description("Saves all registered dynamic functions to disk as JSON so they persist across server restarts")]
    public string SaveDynamicFunctions()
    {
        return "Functions are now automatically persisted to SQLite on registration. No manual save needed.";
    }

    // ── Invocation ────────────────────────────────────────────────────────────

    [McpServerTool(Name = "call_dynamic_function")]
    [Description("Calls a previously registered dynamic function with the given arguments")]
    public string CallDynamicFunction(
        [Description("The name of the dynamic function to call")] string name,
        [Description("JSON object of argument values, e.g. {\"x\": 5}")] string arguments = "{}")
    {
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name, description, parameters, function_type, body, compiled_assembly, output_instructions FROM functions WHERE name = @name";
        cmd.Parameters.AddWithValue("@name", name);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return $"Dynamic function '{name}' not found. " +
                   "Use list_dynamic_functions to see available functions.";

        var functionType = reader.GetString(3);
        var body = reader.GetString(4);
        var parametersJson = reader.GetString(2);
        var outputInstructions = reader.IsDBNull(6) ? null : reader.GetString(6);
        var dynParams = JsonSerializer.Deserialize<List<DynParam>>(parametersJson, ReadOptions)
                        ?? new List<DynParam>();

        string result;

        if (string.Equals(functionType, "instructions", StringComparison.OrdinalIgnoreCase))
        {
            result = ExecuteInstructions(body, dynParams, arguments);
        }
        else
        {
            // Code function — load compiled assembly
            if (reader.IsDBNull(5))
                return $"Function '{name}' has no compiled assembly. Re-register it to compile.";

            var assemblyBytes = (byte[])reader[5];
            result = ExecuteCompiledCode(name, assemblyBytes, dynParams, arguments);
        }

        if (!string.IsNullOrWhiteSpace(outputInstructions))
            result += $"\n\n[Output Instructions]: {outputInstructions}";

        return result;
    }

    // ── Out-of-process invocation ──────────────────────────────────────────────

    [McpServerTool(Name = "call_dynamic_process")]
    [Description("Calls a dynamic function in a separate process (out-of-process execution). " +
                 "Useful for parallel execution or isolating side effects.")]
    public string CallDynamicProcess(
        [Description("The name of the dynamic function to call")] string name,
        [Description("JSON object of argument values, e.g. {\"x\": 5}")] string arguments = "{}")
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
            return "Error: unable to resolve the current executable path.";

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--exec");
            psi.ArgumentList.Add(name);
            psi.ArgumentList.Add(arguments);

            var proc = System.Diagnostics.Process.Start(psi)!;
            proc.StandardInput.Close();

            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();

            if (!proc.WaitForExit(120_000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return $"Error: process timed out after 120 seconds.";
            }

            var stdout = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();

            return proc.ExitCode == 0
                ? stdout
                : $"Error (exit code {proc.ExitCode}):\n{stderr}\n{stdout}".Trim();
        }
        catch (Exception ex)
        {
            return $"Error spawning process: {ex.Message}";
        }
    }

    // ── Compilation ───────────────────────────────────────────────────────────

    private static (byte[]? bytes, string? errors) CompileFunction(DynamicFunction func)
    {
        var preamble = new StringBuilder();
        foreach (var param in func.Parameters)
        {
            string csType = (param.Type?.ToLowerInvariant()) switch
            {
                "int"    => "int",
                "long"   => "long",
                "double" => "double",
                "float"  => "float",
                "bool"   => "bool",
                _        => "string",
            };

            string defaultValue = csType switch
            {
                "int"    => "0",
                "long"   => "0L",
                "double" => "0.0",
                "float"  => "0f",
                "bool"   => "false",
                _        => "\"\"",
            };

            string parseExpr = csType switch
            {
                "int"    => $"args.ContainsKey(\"{param.Name}\") && int.TryParse(args[\"{param.Name}\"], out var __{param.Name}_v) ? __{param.Name}_v : {defaultValue}",
                "long"   => $"args.ContainsKey(\"{param.Name}\") && long.TryParse(args[\"{param.Name}\"], out var __{param.Name}_v) ? __{param.Name}_v : {defaultValue}",
                "double" => $"args.ContainsKey(\"{param.Name}\") && double.TryParse(args[\"{param.Name}\"], out var __{param.Name}_v) ? __{param.Name}_v : {defaultValue}",
                "float"  => $"args.ContainsKey(\"{param.Name}\") && float.TryParse(args[\"{param.Name}\"], out var __{param.Name}_v) ? __{param.Name}_v : {defaultValue}",
                "bool"   => $"args.ContainsKey(\"{param.Name}\") && bool.TryParse(args[\"{param.Name}\"], out var __{param.Name}_v) ? __{param.Name}_v : {defaultValue}",
                _        => $"args.ContainsKey(\"{param.Name}\") ? args[\"{param.Name}\"] : {defaultValue}",
            };

            preamble.AppendLine($"            {csType} {param.Name} = {parseExpr};");
        }

        var sourceCode = $$"""
            using System;
            using System.Collections.Generic;
            using System.Globalization;
            using System.IO;
            using System.Linq;
            using System.Net;
            using System.Net.Http;
            using System.Text;
            using System.Text.RegularExpressions;
            using System.Threading.Tasks;

            public static class DynamicScript
            {
                public static string Run(Dictionary<string, string> args)
                {
            {{preamble}}
            {{func.Body}}
                }
            }
            """;

        var syntaxTree = CSharpSyntaxTree.ParseText(sourceCode);

        var references = GatherMetadataReferences();
        references.Add(_helperAssembly.Value.reference);

        var compilation = CSharpCompilation.Create(
            assemblyName: $"DynFunc_{func.Name}_{Guid.NewGuid():N}",
            syntaxTrees: new[] { syntaxTree },
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithOptimizationLevel(OptimizationLevel.Release));

        using var peStream = new MemoryStream();
        var emitResult = compilation.Emit(peStream);

        if (!emitResult.Success)
        {
            var errors = emitResult.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString());
            return (null, string.Join("\n", errors));
        }

        return (peStream.ToArray(), null);
    }

    /// <summary>
    /// Resolves MetadataReferences for Roslyn compilation.
    /// Strategy 1 (dotnet run / normal exe): load from DLL files on disk via typeof(object).Assembly.Location.
    /// Strategy 2 (single-file publish): Assembly.Location is empty and DLLs are bundled in the exe,
    ///   so we read raw metadata directly from loaded assemblies in memory.
    /// </summary>
    private static List<MetadataReference> GatherMetadataReferences()
    {
        var references = new List<MetadataReference>();

        // Strategy 1: File-based — works for dotnet run and non-single-file publishes
        // IL3000: We intentionally check Assembly.Location and handle the empty case in Strategy 2.
#pragma warning disable IL3000
        var asmLocation = typeof(object).Assembly.Location;
#pragma warning restore IL3000
        if (!string.IsNullOrEmpty(asmLocation))
        {
            var runtimeDir = Path.GetDirectoryName(asmLocation)!;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Core references
            foreach (var name in new[]
            {
                "System.Runtime.dll",
                "System.Collections.dll",
                "System.Linq.dll",
                "System.Console.dll",
                "System.Text.RegularExpressions.dll",
                "System.ComponentModel.Primitives.dll",
                "System.Private.CoreLib.dll",
                "System.Private.Uri.dll",
                "netstandard.dll",
            })
            {
                var path = Path.Combine(runtimeDir, name);
                if (File.Exists(path) && seen.Add(path))
                    references.Add(MetadataReference.CreateFromFile(path));
            }

            // Add all System.* assemblies for broad compatibility
            foreach (var dllPath in Directory.GetFiles(runtimeDir, "System.*.dll"))
            {
                if (!seen.Add(dllPath)) continue;
                try
                {
                    AssemblyName.GetAssemblyName(dllPath);
                    references.Add(MetadataReference.CreateFromFile(dllPath));
                }
                catch { /* skip native DLLs */ }
            }

            return references;
        }

        // Strategy 2: In-memory — works for self-contained single-file publishes
        // Assembly.Location is empty, DLLs are bundled inside the exe.
        // Use TryGetRawMetadata to read metadata directly from loaded assemblies.
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.IsDynamic) continue;
            // Skip ScriptMCP assemblies to avoid namespace conflict with the ScriptMCP helper class
            var asmName = asm.GetName().Name;
            if (asmName != null && asmName.StartsWith("ScriptMCP", StringComparison.Ordinal)) continue;

            try
            {
                unsafe
                {
                    if (asm.TryGetRawMetadata(out byte* blob, out int length))
                    {
                        var moduleMetadata = ModuleMetadata.CreateFromMetadata((IntPtr)blob, length);
                        var assemblyMetadata = AssemblyMetadata.Create(moduleMetadata);
                        references.Add(assemblyMetadata.GetReference(display: asm.FullName));
                    }
                }
            }
            catch { /* skip assemblies that can't provide metadata */ }
        }

        return references;
    }

    // ── ScriptMCP helper assembly (compiled once, loaded into each ALC) ──────

    private const string HelperSourceCode = """
        using System;
        using System.Diagnostics;

        public static class ScriptMCP
        {
            private static ProcessStartInfo CreateStartInfo(string functionName, string arguments)
            {
                var exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath))
                    throw new InvalidOperationException("Unable to resolve the current executable path.");
                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add("--exec");
                psi.ArgumentList.Add(functionName);
                psi.ArgumentList.Add(arguments);
                return psi;
            }

            public static string Call(string functionName, string arguments = "{}")
            {
                var psi = CreateStartInfo(functionName, arguments);
                var proc = Process.Start(psi)!;
                proc.StandardInput.Close();
                var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                var stderrTask = proc.StandardError.ReadToEndAsync();
                if (!proc.WaitForExit(120_000))
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    throw new TimeoutException($"ScriptMCP.Call(\"{functionName}\") timed out after 120 seconds.");
                }
                var stdout = stdoutTask.GetAwaiter().GetResult();
                var stderr = stderrTask.GetAwaiter().GetResult();
                if (proc.ExitCode != 0)
                    throw new Exception($"ScriptMCP.Call(\"{functionName}\") failed (exit code {proc.ExitCode}):\n{stderr}\n{stdout}".Trim());
                return stdout;
            }

            public static Process Proc(string functionName, string arguments = "{}")
            {
                var psi = CreateStartInfo(functionName, arguments);
                var proc = Process.Start(psi)!;
                proc.StandardInput.Close();
                return proc;
            }
        }
        """;

    private static (byte[] bytes, MetadataReference reference) CompileHelperAssembly()
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(HelperSourceCode);
        var references = GatherMetadataReferences();

        var compilation = CSharpCompilation.Create(
            assemblyName: "ScriptMCP.Helpers",
            syntaxTrees: new[] { syntaxTree },
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithOptimizationLevel(OptimizationLevel.Release));

        using var peStream = new MemoryStream();
        var emitResult = compilation.Emit(peStream);

        if (!emitResult.Success)
        {
            var errors = emitResult.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString());
            throw new InvalidOperationException(
                $"Failed to compile ScriptMCP helper assembly:\n{string.Join("\n", errors)}");
        }

        var bytes = peStream.ToArray();
        var metadataRef = MetadataReference.CreateFromImage(bytes);
        return (bytes, metadataRef);
    }

    // ── Execution ─────────────────────────────────────────────────────────────

    private static string ExecuteCompiledCode(string funcName, byte[] assemblyBytes, List<DynParam> dynParams, string arguments)
    {
        AssemblyLoadContext? alc = null;
        try
        {
            JsonElement argsElem = ParseArguments(arguments);

            // Build args dictionary
            var args = new Dictionary<string, string>();
            foreach (var param in dynParams)
            {
                if (argsElem.TryGetProperty(param.Name, out var val))
                    args[param.Name] = val.ValueKind == JsonValueKind.String
                        ? val.GetString() ?? ""
                        : val.GetRawText();
                else
                    args[param.Name] = "";
            }

            // Load into collectible ALC
            alc = new AssemblyLoadContext(funcName, isCollectible: true);
            alc.LoadFromStream(new MemoryStream(_helperAssembly.Value.bytes));
            var assembly = alc.LoadFromStream(new MemoryStream(assemblyBytes));

            var scriptType = assembly.GetType("DynamicScript")
                ?? throw new InvalidOperationException("Compiled assembly missing DynamicScript type.");
            var runMethod = scriptType.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException("Compiled assembly missing Run method.");

            var result = (string?)runMethod.Invoke(null, new object[] { args });

            return result ?? "(no output)";
        }
        catch (TargetInvocationException ex)
        {
            var inner = ex.InnerException ?? ex;
            return $"Script execution failed: {inner.Message}";
        }
        catch (Exception ex)
        {
            return $"Script execution failed: {ex.Message}";
        }
        finally
        {
            alc?.Unload();
        }
    }

    private static string ExecuteInstructions(string body, List<DynParam> dynParams, string arguments)
    {
        try
        {
            JsonElement argsElem = ParseArguments(arguments);

            var text = body;
            foreach (var param in dynParams)
            {
                if (argsElem.TryGetProperty(param.Name, out var val))
                    text = text.Replace("{" + param.Name + "}", val.GetString() ?? "");
            }

            return text;
        }
        catch (Exception ex)
        {
            return $"Instructions execution failed: {ex.Message}";
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool IsInstructions(DynamicFunction f) =>
        string.Equals(f.FunctionType, "instructions", StringComparison.OrdinalIgnoreCase);

    private static JsonElement ParseArguments(string arguments)
    {
        try
        {
            return string.IsNullOrWhiteSpace(arguments)
                ? JsonDocument.Parse("{}").RootElement
                : JsonDocument.Parse(arguments).RootElement;
        }
        catch
        {
            return JsonDocument.Parse("{}").RootElement;
        }
    }

    private static void InsertFunction(SqliteConnection conn, DynamicFunction func, byte[]? assemblyBytes)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT OR REPLACE INTO functions (name, description, parameters, function_type, body, compiled_assembly, output_instructions)
            VALUES (@name, @description, @parameters, @function_type, @body, @compiled_assembly, @output_instructions)";
        cmd.Parameters.AddWithValue("@name", func.Name);
        cmd.Parameters.AddWithValue("@description", func.Description);
        cmd.Parameters.AddWithValue("@parameters", JsonSerializer.Serialize(func.Parameters));
        cmd.Parameters.AddWithValue("@function_type", func.FunctionType ?? "code");
        cmd.Parameters.AddWithValue("@body", func.Body);
        cmd.Parameters.AddWithValue("@compiled_assembly", (object?)assemblyBytes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@output_instructions", (object?)func.OutputInstructions ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
}
