using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Reflection.Metadata;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
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
    [JsonPropertyName("Dependencies")]        public string?       Dependencies        { get; set; } = "";
}

// ── DynamicTools ──────────────────────────────────────────────────────────────

public class DynamicTools
{
    private enum DynamicProcessOutputMode
    {
        Default,
        WriteNew,
        WriteAppend
    }

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
        "scriptmcp.db");

    private static string ConnectionString => $"Data Source={SavePath}";

    private static void AppendDatabaseArgumentFromCurrentProcess(System.Diagnostics.ProcessStartInfo psi)
    {
        psi.ArgumentList.Add(McpConstants.DatabaseArgumentName);
        psi.ArgumentList.Add(SavePath);
    }

    private static string BuildDatabaseArgumentForShell()
    {
        return $" {McpConstants.DatabaseArgumentName} \"{SavePath.Replace("\"", "\\\"")}\"";
    }

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

        // Migrate: add dependencies column if missing (existing DBs)
        bool hasDependencies = false;
        using var pragma2 = conn.CreateCommand();
        pragma2.CommandText = "PRAGMA table_info(functions)";
        using (var reader2 = pragma2.ExecuteReader())
        {
            while (reader2.Read())
            {
                if (string.Equals(reader2.GetString(1), "dependencies", StringComparison.OrdinalIgnoreCase))
                { hasDependencies = true; break; }
            }
        }
        if (!hasDependencies)
        {
            using var alter2 = conn.CreateCommand();
            alter2.CommandText = "ALTER TABLE functions ADD COLUMN dependencies TEXT";
            alter2.ExecuteNonQuery();
        }

        // Backfill: scan existing functions that have never been scanned (dependencies IS NULL)
        BackfillDependencies(conn);
    }

    private static void BackfillDependencies(SqliteConnection conn)
    {
        var knownNames = GetFunctionNames(conn);

        using var scanCmd = conn.CreateCommand();
        scanCmd.CommandText = "SELECT name, parameters, function_type, body FROM functions WHERE dependencies IS NULL";

        var toUpdate = new List<(string name, string deps)>();
        using (var scanReader = scanCmd.ExecuteReader())
        {
            while (scanReader.Read())
            {
                var func = new DynamicFunction
                {
                    Name = scanReader.GetString(0),
                    Parameters = JsonSerializer.Deserialize<List<DynParam>>(scanReader.GetString(1), ReadOptions) ?? new List<DynParam>(),
                    FunctionType = scanReader.GetString(2),
                    Body = scanReader.GetString(3),
                };
                var deps = ExtractDependencies(func, knownNames);
                toUpdate.Add((func.Name, DependenciesToCsv(deps)));
            }
        }

        foreach (var (name, deps) in toUpdate)
        {
            using var upd = conn.CreateCommand();
            upd.CommandText = "UPDATE functions SET dependencies = @deps WHERE name = @name";
            upd.Parameters.AddWithValue("@deps", deps);
            upd.Parameters.AddWithValue("@name", name);
            upd.ExecuteNonQuery();
        }

        if (toUpdate.Count > 0)
            Console.Error.WriteLine($"Backfilled dependencies for {toUpdate.Count} function(s).");
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

            var migrationNames = funcs.Select(f => f.Name).ToList();

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

                var deps = ExtractDependencies(func, migrationNames);
                func.Dependencies = DependenciesToCsv(deps);
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
    [Description("Lists all registered dynamic function names as a comma-delimited string")]
    public string ListDynamicFunctions()
    {
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM functions ORDER BY name";

        using var reader = cmd.ExecuteReader();
        var names = new List<string>();

        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return string.Join(", ", names);
    }

    // ── Deletion ──────────────────────────────────────────────────────────────

    [McpServerTool(Name = "delete_dynamic_function")]
    [Description("Deletes a registered dynamic function from the database by name")]
    public string DeleteDynamicFunction(
        [Description("The name of the dynamic function to delete")] string name,
        [Description("Set to true to force deletion when other functions depend on this one")] bool forced = false)
    {
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();

        var dependents = FindDependentsOf(conn, name);

        if (dependents.Count > 0 && !forced)
        {
            return $"Cannot delete '{name}' because these functions depend on it: {string.Join(", ", dependents)}.\n" +
                   "User confirmation is required before forced deletion.";
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM functions WHERE name = @name";
        cmd.Parameters.AddWithValue("@name", name);

        var rows = cmd.ExecuteNonQuery();
        if (rows == 0)
            return $"Function '{name}' not found.";

        var msg = $"Function '{name}' deleted successfully.";
        if (dependents.Count > 0)
            msg += $" Note: the following function(s) depended on it and may break: {string.Join(", ", dependents)}.";
        return msg;
    }

    // ── Inspection ─────────────────────────────────────────────────────────────

    [McpServerTool(Name = "inspect_dynamic_function")]
    [Description("Inspects a registered dynamic function and returns metadata and parameters. Set fullInspection=true to also include source code and compiled status.")]
    public string InspectDynamicFunction(
        [Description("The name of the dynamic function to inspect")] string name,
        [Description("When true, include source code and compiled status. When false or omitted, omit those details.")] bool fullInspection = false)
    {
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name, description, parameters, function_type, body, compiled_assembly, output_instructions, dependencies FROM functions WHERE name = @name";
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
        var dependencies        = reader.IsDBNull(7) ? null : reader.GetString(7);

        var dynParams = JsonSerializer.Deserialize<List<DynParam>>(parametersJson, ReadOptions)
                        ?? new List<DynParam>();

        var isInstr = string.Equals(functionType, "instructions", StringComparison.OrdinalIgnoreCase);

        var sb = new StringBuilder();
        sb.AppendLine($"Function: {funcName}");
        sb.AppendLine($"Type:        {functionType}");
        sb.AppendLine($"Description: {description}");

        if (dynParams.Count == 0)
        {
            sb.AppendLine();
            sb.AppendLine("Parameters: (none)");
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine("Parameters:");
            foreach (var p in dynParams)
                sb.AppendLine($"  - {p.Name} ({p.Type}): {p.Description}");
        }

        sb.AppendLine();
        sb.AppendLine($"Depends on:  {(string.IsNullOrWhiteSpace(dependencies) ? "(none)" : dependencies)}");

        if (fullInspection)
        {
            sb.AppendLine();
            sb.AppendLine($"Compiled:    {(isInstr ? "N/A (instructions)" : hasAssembly ? "Yes" : "No (missing assembly)")}");
            sb.AppendLine();
            sb.AppendLine($"Source ({(isInstr ? "Instructions" : "C# Code")}):");

            var lines = body.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                var lineNum = (i + 1).ToString().PadLeft(3);
                sb.AppendLine($"  {lineNum} | {lines[i].TrimEnd('\r')}");
            }
        }

        sb.AppendLine();
        sb.AppendLine($"Output Instructions: {(string.IsNullOrWhiteSpace(outputInstructions) ? "(none)" : outputInstructions)}");

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
                OutputInstructions  = string.IsNullOrWhiteSpace(outputInstructions)
                    ? null
                    : outputInstructions,
            };

            ValidateFunctionName(func.Name);

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

            var knownNames = GetFunctionNames(conn);
            var deps = ExtractDependencies(func, knownNames);
            var mutualDeps = FindDirectMutualDependencies(conn, func.Name, deps);
            if (mutualDeps.Count > 0)
            {
                return $"Registration failed: direct circular dependency detected for '{func.Name}': " +
                       $"{string.Join(", ", mutualDeps.Select(d => $"{func.Name} <-> {d}"))}.";
            }
            func.Dependencies = DependenciesToCsv(deps);

            InsertFunction(conn, func, assemblyBytes);

            return $"{(IsInstructions(func) ? "Instructions" : "Code")} function '{func.Name}' registered successfully " +
                   $"with {func.Parameters.Count} parameter(s).";
        }
        catch (Exception ex)
        {
            return $"Registration failed: {ex.Message}";
        }
    }

    [McpServerTool(Name = "update_dynamic_function")]
    [Description("Updates a single field on an existing dynamic function entry. " +
                 "Supported fields: name, description, parameters, function_type, body, output_instructions, dependencies. " +
                 "When the update affects execution, the function is recompiled automatically.")]
    public string UpdateDynamicFunction(
        [Description("The existing function name to update")] string name,
        [Description("The field/column to update: name, description, parameters, function_type, body, output_instructions, or dependencies")] string field,
        [Description("The new value for that field")] string value)
    {
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();

        using var readCmd = conn.CreateCommand();
        readCmd.CommandText = @"
            SELECT name, description, parameters, function_type, body, output_instructions, dependencies
            FROM functions
            WHERE name = @name";
        readCmd.Parameters.AddWithValue("@name", name);

        using var reader = readCmd.ExecuteReader();
        if (!reader.Read())
            return $"Function '{name}' not found.";

        var func = new DynamicFunction
        {
            Name = reader.GetString(0),
            Description = reader.GetString(1),
            Parameters = JsonSerializer.Deserialize<List<DynParam>>(reader.GetString(2), ReadOptions) ?? new List<DynParam>(),
            FunctionType = reader.GetString(3),
            Body = reader.GetString(4),
            OutputInstructions = reader.IsDBNull(5) ? null : reader.GetString(5),
            Dependencies = reader.IsDBNull(6) ? "" : reader.GetString(6),
        };

        reader.Close();

        string normalizedField;
        try
        {
            normalizedField = NormalizeUpdatableField(field);
            ApplyFieldUpdate(func, normalizedField, value);
        }
        catch (Exception ex)
        {
            return $"Update failed: {ex.Message}";
        }

        byte[]? assemblyBytes = null;
        if (!IsInstructions(func))
        {
            var (bytes, errors) = CompileFunction(func);
            if (bytes == null)
                return $"Update failed: compilation failed after changing '{normalizedField}':\n{errors}";

            assemblyBytes = bytes;
        }

        // Auto-compute dependencies unless the user is explicitly setting them
        if (!string.Equals(normalizedField, "dependencies", StringComparison.OrdinalIgnoreCase))
        {
            var knownNames = GetFunctionNames(conn);
            var deps = ExtractDependencies(func, knownNames);
            if (string.Equals(normalizedField, "body", StringComparison.OrdinalIgnoreCase))
            {
                var mutualDeps = FindDirectMutualDependencies(conn, func.Name, deps);
                if (mutualDeps.Count > 0)
                {
                    return $"Update failed: direct circular dependency detected for '{func.Name}': " +
                           $"{string.Join(", ", mutualDeps.Select(d => $"{func.Name} <-> {d}"))}.";
                }
            }
            func.Dependencies = DependenciesToCsv(deps);
        }

        using var tx = conn.BeginTransaction();
        using var updateCmd = conn.CreateCommand();
        updateCmd.Transaction = tx;
        updateCmd.CommandText = @"
            UPDATE functions
            SET name = @new_name,
                description = @description,
                parameters = @parameters,
                function_type = @function_type,
                body = @body,
                compiled_assembly = @compiled_assembly,
                output_instructions = @output_instructions,
                dependencies = @dependencies
            WHERE name = @original_name";
        updateCmd.Parameters.AddWithValue("@new_name", func.Name);
        updateCmd.Parameters.AddWithValue("@description", func.Description);
        updateCmd.Parameters.AddWithValue("@parameters", JsonSerializer.Serialize(func.Parameters));
        updateCmd.Parameters.AddWithValue("@function_type", func.FunctionType ?? "code");
        updateCmd.Parameters.AddWithValue("@body", func.Body);
        updateCmd.Parameters.AddWithValue("@compiled_assembly", (object?)assemblyBytes ?? DBNull.Value);
        updateCmd.Parameters.AddWithValue("@output_instructions", (object?)func.OutputInstructions ?? DBNull.Value);
        updateCmd.Parameters.AddWithValue("@dependencies", (object?)func.Dependencies ?? DBNull.Value);
        updateCmd.Parameters.AddWithValue("@original_name", name);

        try
        {
            var rows = updateCmd.ExecuteNonQuery();
            if (rows == 0)
            {
                tx.Rollback();
                return $"Function '{name}' not found.";
            }

            // On rename: auto-patch callers that reference the old name
            var patchedCallers = new List<string>();
            bool isRename = string.Equals(normalizedField, "name", StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(name, func.Name, StringComparison.OrdinalIgnoreCase);
            if (isRename)
            {
                var dependents = FindDependentsOf(conn, name);
                foreach (var depName in dependents)
                {
                    // Skip the function being renamed (already updated above)
                    if (string.Equals(depName, name, StringComparison.OrdinalIgnoreCase))
                        continue;

                    using var readDep = conn.CreateCommand();
                    readDep.Transaction = tx;
                    readDep.CommandText = "SELECT name, description, parameters, function_type, body, output_instructions FROM functions WHERE name = @n";
                    readDep.Parameters.AddWithValue("@n", depName);

                    DynamicFunction? depFunc = null;
                    using (var depReader = readDep.ExecuteReader())
                    {
                        if (!depReader.Read()) continue;
                        depFunc = new DynamicFunction
                        {
                            Name = depReader.GetString(0),
                            Description = depReader.GetString(1),
                            Parameters = JsonSerializer.Deserialize<List<DynParam>>(depReader.GetString(2), ReadOptions) ?? new List<DynParam>(),
                            FunctionType = depReader.GetString(3),
                            Body = depReader.GetString(4),
                            OutputInstructions = depReader.IsDBNull(5) ? null : depReader.GetString(5),
                        };
                    }

                    // Replace old name with new name in Call/Proc patterns
                    var patternOld = new Regex(
                        @"(ScriptMCP\.\s*(?:Call|Proc)\s*\(\s*"")" + Regex.Escape(name) + @"("")",
                        RegexOptions.IgnoreCase);
                    var newBody = patternOld.Replace(depFunc.Body, "${1}" + func.Name + "${2}");
                    if (newBody == depFunc.Body) continue;

                    depFunc.Body = newBody;

                    // Recompile the patched caller
                    byte[]? depAsm = null;
                    if (!IsInstructions(depFunc))
                    {
                        var (bytes2, errors2) = CompileFunction(depFunc);
                        if (bytes2 == null)
                        {
                            // Can't patch this caller — skip but don't fail the rename
                            Console.Error.WriteLine($"Rename auto-patch: failed to recompile '{depName}': {errors2}");
                            continue;
                        }
                        depAsm = bytes2;
                    }

                    var depKnown = GetFunctionNames(conn);
                    var depDeps = ExtractDependencies(depFunc, depKnown);
                    depFunc.Dependencies = DependenciesToCsv(depDeps);

                    using var patchCmd = conn.CreateCommand();
                    patchCmd.Transaction = tx;
                    patchCmd.CommandText = @"
                        UPDATE functions
                        SET body = @body,
                            compiled_assembly = @asm,
                            dependencies = @deps
                        WHERE name = @n";
                    patchCmd.Parameters.AddWithValue("@body", depFunc.Body);
                    patchCmd.Parameters.AddWithValue("@asm", (object?)depAsm ?? DBNull.Value);
                    patchCmd.Parameters.AddWithValue("@deps", (object?)depFunc.Dependencies ?? DBNull.Value);
                    patchCmd.Parameters.AddWithValue("@n", depName);
                    patchCmd.ExecuteNonQuery();

                    patchedCallers.Add(depName);
                }
            }

            tx.Commit();

            var msg = $"Function '{name}' updated successfully: {normalizedField}.";
            if (patchedCallers.Count > 0)
                msg += $" Auto-patched caller(s): {string.Join(", ", patchedCallers)}.";
            return msg;
        }
        catch (SqliteException) when (string.Equals(normalizedField, "name", StringComparison.OrdinalIgnoreCase))
        {
            tx.Rollback();
            return $"Update failed: a function named '{func.Name}' already exists.";
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
        [Description("JSON object of argument values, e.g. {\"x\": 5}")] string arguments = "{}",
        [Description("Output mode: Default (uses --exec, no persisted output file), WriteNew (uses --exec-out, writes a new file per execution), WriteAppend (uses --exec-out-append, appends to one stable file)")] string output_mode = "Default")
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
            return "Error: unable to resolve the current executable path.";

        if (!Enum.TryParse<DynamicProcessOutputMode>(output_mode, ignoreCase: true, out var outputMode))
            return "Error: invalid output_mode. Supported values: Default, WriteNew, WriteAppend.";

        var execFlag = outputMode switch
        {
            DynamicProcessOutputMode.WriteNew => "--exec-out",
            DynamicProcessOutputMode.WriteAppend => "--exec-out-append",
            _ => "--exec"
        };

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
                CreateNoWindow = true,
            };
            AppendDatabaseArgumentFromCurrentProcess(psi);
            psi.ArgumentList.Add(execFlag);
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

    // ── Scheduled Task Output ────────────────────────────────────────────────

    [McpServerTool(Name = "read_scheduled_task")]
    [Description("Reads the most recent scheduled-task output file for the specified dynamic function.")]
    public string ReadScheduledTask(
        [Description("Dynamic function name whose latest scheduled-task output should be returned")] string function_name)
    {
        var outputDirs = new[]
        {
            GetScheduledTaskOutputDirectory()
        }
        .Where(Directory.Exists)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

        if (outputDirs.Length == 0)
            return "(empty)";

        var prefix = GetScheduledTaskFilePrefix(function_name);
        var appendFile = outputDirs
            .Select(dir => Path.Combine(dir, $"{prefix}.txt"))
            .Where(File.Exists)
            .Select(path => new FileInfo(path))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();

        var pattern = $"^{Regex.Escape(prefix)}_(\\d{{6}}_\\d{{6}})\\.txt$";
        var latestFile = outputDirs
            .SelectMany(dir => Directory.EnumerateFiles(dir, $"{prefix}_*.txt"))
            .Select(path => new FileInfo(path))
            .Select(file => new
            {
                File = file,
                Match = Regex.Match(file.Name, pattern, RegexOptions.CultureInvariant)
            })
            .Where(x => x.Match.Success)
            .OrderByDescending(x => x.Match.Groups[1].Value, StringComparer.Ordinal)
            .Select(x => x.File)
            .FirstOrDefault();

        var chosenFile = latestFile;
        if (appendFile != null && appendFile.Exists &&
            (chosenFile == null || appendFile.LastWriteTimeUtc >= chosenFile.LastWriteTimeUtc))
        {
            chosenFile = appendFile;
        }

        if (chosenFile == null || !chosenFile.Exists)
            return $"No scheduled-task output found for '{function_name}'";

        var content = File.ReadAllText(chosenFile.FullName);
        return string.IsNullOrEmpty(content) ? "(empty)" : content;
    }

    public static string GetScheduledTaskOutputDirectory() =>
        Path.Combine(Path.GetDirectoryName(SavePath) ?? ".", "output");

    public static string GetScheduledTaskFilePrefix(string functionName)
    {
        if (string.IsNullOrWhiteSpace(functionName))
            return "unnamed";

        var sanitized = new string(functionName
            .Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)
            .ToArray())
            .Trim();

        return string.IsNullOrEmpty(sanitized) ? "unnamed" : sanitized;
    }

    public static string GetScheduledTaskOutputPath(string functionName, DateTime? utcNow = null)
    {
        var outputDir = GetScheduledTaskOutputDirectory();
        Directory.CreateDirectory(outputDir);

        var prefix = GetScheduledTaskFilePrefix(functionName);
        var timestamp = (utcNow ?? DateTime.UtcNow).ToString("yyMMdd_HHmmss", CultureInfo.InvariantCulture);
        return Path.Combine(outputDir, $"{prefix}_{timestamp}.txt");
    }

    public static string GetScheduledTaskAppendOutputPath(string functionName)
    {
        var outputDir = GetScheduledTaskOutputDirectory();
        Directory.CreateDirectory(outputDir);

        var prefix = GetScheduledTaskFilePrefix(functionName);
        return Path.Combine(outputDir, $"{prefix}.txt");
    }

    // ── Scheduled Tasks ────────────────────────────────────────────────────────

    [McpServerTool(Name = "create_scheduled_task")]
    [Description("Creates a scheduled task (Windows Task Scheduler or cron on Linux/macOS) that runs a ScriptMCP dynamic function at a given interval in minutes")]
    public string CreateScheduledTask(
        [Description("Name of the ScriptMCP dynamic function to run")] string function_name,
        [Description("JSON arguments for the function (default: {})")] string function_args = "{}",
        [Description("How often to run the task, in minutes")] int interval_minutes = 1,
        [Description("When true, append each result to a stable <function>.txt file instead of creating a new timestamped file")] bool append = false)
    {
        string exePath = Environment.ProcessPath ?? "";
        if (string.IsNullOrEmpty(exePath))
            return "Error: Unable to resolve the current executable path.";

        if (OperatingSystem.IsWindows())
            return CreateScheduledTaskWindows(exePath, function_name, function_args, interval_minutes, append);
        else
            return CreateScheduledTaskCron(exePath, function_name, function_args, interval_minutes, append);
    }

    [McpServerTool(Name = "delete_scheduled_task")]
    [Description("Deletes a scheduled task (Windows Task Scheduler or cron on Linux/macOS) for a ScriptMCP dynamic function.")]
    public string DeleteScheduledTask(
        [Description("Name of the ScriptMCP dynamic function whose scheduled task should be deleted")] string function_name,
        [Description("Interval in minutes used when the task was created")] int interval_minutes = 1)
    {
        if (OperatingSystem.IsWindows())
            return DeleteScheduledTaskWindows(function_name, interval_minutes);
        else
            return DeleteScheduledTaskCron(function_name);
    }

    [McpServerTool(Name = "list_scheduled_tasks")]
    [Description("Lists ScriptMCP scheduled tasks from Windows Task Scheduler or cron.")]
    public string ListScheduledTasks()
    {
        if (OperatingSystem.IsWindows())
            return ListScheduledTasksWindows();
        else
            return ListScheduledTasksCron();
    }

    [McpServerTool(Name = "start_scheduled_task")]
    [Description("Starts or enables a scheduled task for a ScriptMCP dynamic function.")]
    public string StartScheduledTask(
        [Description("Name of the ScriptMCP dynamic function whose scheduled task should be started")] string function_name,
        [Description("Interval in minutes used when the task was created")] int interval_minutes = 1)
    {
        if (OperatingSystem.IsWindows())
            return StartScheduledTaskWindows(function_name, interval_minutes);
        else
            return StartScheduledTaskCron(function_name);
    }

    [McpServerTool(Name = "stop_scheduled_task")]
    [Description("Stops or disables a scheduled task for a ScriptMCP dynamic function.")]
    public string StopScheduledTask(
        [Description("Name of the ScriptMCP dynamic function whose scheduled task should be stopped")] string function_name,
        [Description("Interval in minutes used when the task was created")] int interval_minutes = 1)
    {
        if (OperatingSystem.IsWindows())
            return StopScheduledTaskWindows(function_name, interval_minutes);
        else
            return StopScheduledTaskCron(function_name);
    }

    private static string GetScheduledTaskName(string function_name, int interval_minutes) =>
        $"ScriptMCP\\{function_name} ({interval_minutes}m)";

    private string ListScheduledTasksWindows()
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "schtasks",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("/Query");
        psi.ArgumentList.Add("/FO");
        psi.ArgumentList.Add("LIST");

        var proc = System.Diagnostics.Process.Start(psi)!;
        string output = proc.StandardOutput.ReadToEnd();
        string error = proc.StandardError.ReadToEnd().Trim();
        proc.WaitForExit();

        if (proc.ExitCode != 0)
        {
            var err = new StringBuilder();
            err.AppendLine($"Failed to list scheduled tasks. Exit code: {proc.ExitCode}");
            if (!string.IsNullOrEmpty(error)) err.AppendLine(error);
            return err.ToString().Trim();
        }

        var blocks = Regex.Split(output.Trim(), @"\r?\n\r?\n")
            .Where(block => Regex.IsMatch(block, @"TaskName:\s*\\ScriptMCP\\", RegexOptions.IgnoreCase))
            .ToList();

        if (blocks.Count == 0)
            return "(empty)";

        var sb = new StringBuilder();
        foreach (var block in blocks)
        {
            string? taskName = null;
            string? status = null;
            string? nextRun = null;

            foreach (var rawLine in block.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                int idx = rawLine.IndexOf(':');
                if (idx < 0) continue;

                var key = rawLine[..idx].Trim();
                var value = rawLine[(idx + 1)..].Trim();

                if (key.Equals("TaskName", StringComparison.OrdinalIgnoreCase))
                    taskName = value;
                else if (key.Equals("Status", StringComparison.OrdinalIgnoreCase))
                    status = value;
                else if (key.Equals("Next Run Time", StringComparison.OrdinalIgnoreCase))
                    nextRun = value;
            }

            if (taskName == null)
                continue;

            sb.AppendLine(taskName);
            if (!string.IsNullOrEmpty(status))
                sb.AppendLine($"  Status: {status}");
            if (!string.IsNullOrEmpty(nextRun))
                sb.AppendLine($"  Next:   {nextRun}");
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    private string ListScheduledTasksCron()
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "crontab",
            Arguments = "-l",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var proc = System.Diagnostics.Process.Start(psi)!;
        string output = proc.StandardOutput.ReadToEnd();
        string error = proc.StandardError.ReadToEnd().Trim();
        proc.WaitForExit();

        if (proc.ExitCode != 0 && string.IsNullOrWhiteSpace(output))
        {
            var err = new StringBuilder();
            err.AppendLine($"Failed to list scheduled tasks. Exit code: {proc.ExitCode}");
            if (!string.IsNullOrEmpty(error)) err.AppendLine(error);
            return err.ToString().Trim();
        }

        var lines = output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Contains("# ScriptMCP:", StringComparison.Ordinal))
            .ToList();

        if (lines.Count == 0)
            return "(empty)";

        return string.Join(Environment.NewLine, lines);
    }

    private string CreateScheduledTaskWindows(string exePath, string function_name, string function_args, int interval_minutes, bool append)
    {
        string tn = GetScheduledTaskName(function_name, interval_minutes);

        // Quote the JSON argument payload for the target process because schtasks
        // stores the executable path separately from the argument string.
        string escapedArgs = function_args.Replace("\"", "\\\"");

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "schtasks",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // Use ArgumentList for proper quoting — schtasks gets each arg correctly escaped
        psi.ArgumentList.Add("/Create");
        psi.ArgumentList.Add("/TN");
        psi.ArgumentList.Add(tn);
        psi.ArgumentList.Add("/TR");
        var dbArg = BuildDatabaseArgumentForShell();
        var taskCommand = new StringBuilder();
        taskCommand.Append($"\"{exePath}\"{dbArg} {(append ? "--exec-out-append" : "--exec-out")} {function_name} \"{escapedArgs}\"");
        psi.ArgumentList.Add(taskCommand.ToString());
        psi.ArgumentList.Add("/SC");
        psi.ArgumentList.Add("MINUTE");
        psi.ArgumentList.Add("/MO");
        psi.ArgumentList.Add(interval_minutes.ToString());
        psi.ArgumentList.Add("/F");

        // Create the task
        var proc = System.Diagnostics.Process.Start(psi)!;
        string output = proc.StandardOutput.ReadToEnd().Trim();
        string error = proc.StandardError.ReadToEnd().Trim();
        proc.WaitForExit();

        if (proc.ExitCode != 0)
        {
            var err = new StringBuilder();
            err.AppendLine($"Failed to create task. Exit code: {proc.ExitCode}");
            if (!string.IsNullOrEmpty(output)) err.AppendLine(output);
            if (!string.IsNullOrEmpty(error)) err.AppendLine(error);
            return err.ToString().Trim();
        }

        // Run it immediately
        var runPsi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "schtasks",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        runPsi.ArgumentList.Add("/Run");
        runPsi.ArgumentList.Add("/TN");
        runPsi.ArgumentList.Add(tn);
        var runProc = System.Diagnostics.Process.Start(runPsi)!;
        runProc.StandardOutput.ReadToEnd();
        runProc.StandardError.ReadToEnd();
        runProc.WaitForExit();

        var sb = new StringBuilder();
        sb.AppendLine($"Scheduled task created and started.");
        sb.AppendLine($"  Name:     {tn}");
        sb.AppendLine($"  Function: {function_name}({function_args})");
        sb.AppendLine($"  Exe:      {exePath}");
        sb.AppendLine($"  Interval: Every {interval_minutes} minute(s)");
        sb.AppendLine($"  Output:   {(append ? "Append to <function>.txt" : "New timestamped file per run")}");
        sb.AppendLine();
        sb.AppendLine("Manage with:");
        sb.AppendLine($"  Run now:  schtasks /Run /TN \"{tn}\"");
        sb.AppendLine($"  Disable:  schtasks /Change /TN \"{tn}\" /Disable");
        sb.AppendLine($"  Delete:   schtasks /Delete /TN \"{tn}\" /F");

        return sb.ToString().Trim();
    }

    private string DeleteScheduledTaskWindows(string function_name, int interval_minutes)
    {
        string tn = GetScheduledTaskName(function_name, interval_minutes);

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "schtasks",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("/Delete");
        psi.ArgumentList.Add("/TN");
        psi.ArgumentList.Add(tn);
        psi.ArgumentList.Add("/F");

        var proc = System.Diagnostics.Process.Start(psi)!;
        string output = proc.StandardOutput.ReadToEnd().Trim();
        string error = proc.StandardError.ReadToEnd().Trim();
        proc.WaitForExit();

        if (proc.ExitCode != 0)
        {
            var err = new StringBuilder();
            err.AppendLine($"Failed to delete task. Exit code: {proc.ExitCode}");
            if (!string.IsNullOrEmpty(output)) err.AppendLine(output);
            if (!string.IsNullOrEmpty(error)) err.AppendLine(error);
            return err.ToString().Trim();
        }

        var sb = new StringBuilder();
        sb.AppendLine("Scheduled task deleted.");
        sb.AppendLine($"  Name:     {tn}");
        sb.AppendLine($"  Function: {function_name}");
        sb.AppendLine($"  Interval: Every {interval_minutes} minute(s)");
        return sb.ToString().Trim();
    }

    private string StartScheduledTaskWindows(string function_name, int interval_minutes)
    {
        string tn = GetScheduledTaskName(function_name, interval_minutes);

        var enablePsi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "schtasks",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        enablePsi.ArgumentList.Add("/Change");
        enablePsi.ArgumentList.Add("/TN");
        enablePsi.ArgumentList.Add(tn);
        enablePsi.ArgumentList.Add("/ENABLE");

        var enableProc = System.Diagnostics.Process.Start(enablePsi)!;
        string enableOutput = enableProc.StandardOutput.ReadToEnd().Trim();
        string enableError = enableProc.StandardError.ReadToEnd().Trim();
        enableProc.WaitForExit();

        if (enableProc.ExitCode != 0)
        {
            var err = new StringBuilder();
            err.AppendLine($"Failed to enable task. Exit code: {enableProc.ExitCode}");
            if (!string.IsNullOrEmpty(enableOutput)) err.AppendLine(enableOutput);
            if (!string.IsNullOrEmpty(enableError)) err.AppendLine(enableError);
            return err.ToString().Trim();
        }

        var runPsi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "schtasks",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        runPsi.ArgumentList.Add("/Run");
        runPsi.ArgumentList.Add("/TN");
        runPsi.ArgumentList.Add(tn);

        var runProc = System.Diagnostics.Process.Start(runPsi)!;
        runProc.StandardOutput.ReadToEnd();
        runProc.StandardError.ReadToEnd();
        runProc.WaitForExit();

        var sb = new StringBuilder();
        sb.AppendLine("Scheduled task enabled and started.");
        sb.AppendLine($"  Name:     {tn}");
        sb.AppendLine($"  Function: {function_name}");
        sb.AppendLine($"  Interval: Every {interval_minutes} minute(s)");
        return sb.ToString().Trim();
    }

    private string StopScheduledTaskWindows(string function_name, int interval_minutes)
    {
        string tn = GetScheduledTaskName(function_name, interval_minutes);

        var disablePsi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "schtasks",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        disablePsi.ArgumentList.Add("/Change");
        disablePsi.ArgumentList.Add("/TN");
        disablePsi.ArgumentList.Add(tn);
        disablePsi.ArgumentList.Add("/DISABLE");

        var disableProc = System.Diagnostics.Process.Start(disablePsi)!;
        string disableOutput = disableProc.StandardOutput.ReadToEnd().Trim();
        string disableError = disableProc.StandardError.ReadToEnd().Trim();
        disableProc.WaitForExit();

        if (disableProc.ExitCode != 0)
        {
            var err = new StringBuilder();
            err.AppendLine($"Failed to disable task. Exit code: {disableProc.ExitCode}");
            if (!string.IsNullOrEmpty(disableOutput)) err.AppendLine(disableOutput);
            if (!string.IsNullOrEmpty(disableError)) err.AppendLine(disableError);
            return err.ToString().Trim();
        }

        var sb = new StringBuilder();
        sb.AppendLine("Scheduled task disabled.");
        sb.AppendLine($"  Name:     {tn}");
        sb.AppendLine($"  Function: {function_name}");
        sb.AppendLine($"  Interval: Every {interval_minutes} minute(s)");
        return sb.ToString().Trim();
    }

    private string CreateScheduledTaskCron(string exePath, string function_name, string function_args, int interval_minutes, bool append)
    {
        // Build the cron command line
        string escapedArgs = function_args.Replace("'", "'\\''");
        var dbArg = BuildDatabaseArgumentForShell();
        string command = $"'{exePath}'{dbArg} {(append ? "--exec-out-append" : "--exec-out")} {function_name} '{escapedArgs}'";

        // Build the cron schedule expression
        string schedule = interval_minutes switch
        {
            < 60 => $"*/{interval_minutes} * * * *",
            60 => "0 * * * *",
            _ when interval_minutes % 60 == 0 => $"0 */{interval_minutes / 60} * * *",
            _ => $"*/{interval_minutes} * * * *",
        };

        string cronLine = $"{schedule} {command} # ScriptMCP:{function_name}";

        // Read existing crontab, remove any previous entry for this function, append new one
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "crontab",
            Arguments = "-l",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var proc = System.Diagnostics.Process.Start(psi)!;
        string existing = proc.StandardOutput.ReadToEnd();
        proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        // Filter out previous ScriptMCP entries for this function
        string tag = $"# ScriptMCP:{function_name}";
        var lines = existing.Split('\n')
            .Where(l => !l.Contains(tag))
            .ToList();

        // Remove trailing empty lines, then append new entry
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
            lines.RemoveAt(lines.Count - 1);
        lines.Add(cronLine);
        lines.Add(""); // trailing newline

        string newCrontab = string.Join("\n", lines);

        // Install the new crontab via stdin
        var installPsi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "crontab",
            Arguments = "-",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var installProc = System.Diagnostics.Process.Start(installPsi)!;
        installProc.StandardInput.Write(newCrontab);
        installProc.StandardInput.Close();
        string installOutput = installProc.StandardOutput.ReadToEnd().Trim();
        string installError = installProc.StandardError.ReadToEnd().Trim();
        installProc.WaitForExit();

        if (installProc.ExitCode != 0)
        {
            var err = new StringBuilder();
            err.AppendLine($"Failed to install crontab. Exit code: {installProc.ExitCode}");
            if (!string.IsNullOrEmpty(installOutput)) err.AppendLine(installOutput);
            if (!string.IsNullOrEmpty(installError)) err.AppendLine(installError);
            return err.ToString().Trim();
        }

        // Run it immediately
        var runPsi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = exePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        AppendDatabaseArgumentFromCurrentProcess(runPsi);
        runPsi.ArgumentList.Add(append ? "--exec-out-append" : "--exec-out");
        runPsi.ArgumentList.Add(function_name);
        runPsi.ArgumentList.Add(function_args);
        var runProc = System.Diagnostics.Process.Start(runPsi)!;
        runProc.StandardOutput.ReadToEnd();
        runProc.StandardError.ReadToEnd();
        runProc.WaitForExit();

        var sb = new StringBuilder();
        sb.AppendLine($"Cron job created and run once.");
        sb.AppendLine($"  Function: {function_name}({function_args})");
        sb.AppendLine($"  Exe:      {exePath}");
        sb.AppendLine($"  Schedule: {schedule}");
        sb.AppendLine($"  Tag:      {tag}");
        sb.AppendLine($"  Output:   {(append ? "Append to <function>.txt" : "New timestamped file per run")}");
        sb.AppendLine();
        sb.AppendLine("Manage with:");
        sb.AppendLine($"  List:     crontab -l");
        sb.AppendLine($"  Remove:   crontab -l | grep -v '{tag}' | crontab -");

        return sb.ToString().Trim();
    }

    private string DeleteScheduledTaskCron(string function_name)
    {
        string tag = $"# ScriptMCP:{function_name}";

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "crontab",
            Arguments = "-l",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var proc = System.Diagnostics.Process.Start(psi)!;
        string existing = proc.StandardOutput.ReadToEnd();
        proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        var lines = existing.Split('\n').ToList();
        var filtered = lines.Where(l => !l.Contains(tag)).ToList();
        bool removed = filtered.Count != lines.Count;

        if (!removed)
            return $"Scheduled task not found for '{function_name}'.";

        while (filtered.Count > 0 && string.IsNullOrWhiteSpace(filtered[^1]))
            filtered.RemoveAt(filtered.Count - 1);
        filtered.Add("");

        string newCrontab = string.Join("\n", filtered);

        var installPsi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "crontab",
            Arguments = "-",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var installProc = System.Diagnostics.Process.Start(installPsi)!;
        installProc.StandardInput.Write(newCrontab);
        installProc.StandardInput.Close();
        string installOutput = installProc.StandardOutput.ReadToEnd().Trim();
        string installError = installProc.StandardError.ReadToEnd().Trim();
        installProc.WaitForExit();

        if (installProc.ExitCode != 0)
        {
            var err = new StringBuilder();
            err.AppendLine($"Failed to install crontab. Exit code: {installProc.ExitCode}");
            if (!string.IsNullOrEmpty(installOutput)) err.AppendLine(installOutput);
            if (!string.IsNullOrEmpty(installError)) err.AppendLine(installError);
            return err.ToString().Trim();
        }

        var sb = new StringBuilder();
        sb.AppendLine("Cron job deleted.");
        sb.AppendLine($"  Function: {function_name}");
        sb.AppendLine($"  Tag:      {tag}");
        sb.AppendLine();
        sb.AppendLine("Manage with:");
        sb.AppendLine("  List:     crontab -l");
        return sb.ToString().Trim();
    }

    private string StartScheduledTaskCron(string function_name)
    {
        string tag = $"# ScriptMCP:{function_name}";
        return $"Cron jobs cannot be paused/resumed individually. Matching entries remain active if present.\n  Tag:      {tag}";
    }

    private string StopScheduledTaskCron(string function_name)
    {
        string tag = $"# ScriptMCP:{function_name}";
        return $"Cron jobs cannot be paused/resumed individually. Remove the entry with delete_scheduled_task to stop it.\n  Tag:      {tag}";
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
            private static string? GetDbPath()
            {
                return Environment.GetEnvironmentVariable("SCRIPTMCP_DB");
            }

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
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding = System.Text.Encoding.UTF8,
                    CreateNoWindow = true,
                };
                var dbPath = GetDbPath();
                if (!string.IsNullOrWhiteSpace(dbPath))
                {
                    psi.ArgumentList.Add("--db");
                    psi.ArgumentList.Add(dbPath);
                }
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

            Environment.SetEnvironmentVariable("SCRIPTMCP_DB", SavePath);
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

    private static void ValidateFunctionName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("name cannot be empty.");

        if (!Regex.IsMatch(name.Trim(), "^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant))
        {
            throw new ArgumentException(
                "name must contain only letters, numbers, underscore, or hyphen.");
        }
    }

    private static string NormalizeUpdatableField(string field)
    {
        var normalized = (field ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "name" => "name",
            "description" => "description",
            "parameters" => "parameters",
            "function_type" => "function_type",
            "functiontype" => "function_type",
            "body" => "body",
            "output_instructions" => "output_instructions",
            "outputinstructions" => "output_instructions",
            "dependencies" => "dependencies",
            _ => throw new ArgumentException(
                "field must be one of: name, description, parameters, function_type, body, output_instructions, dependencies."),
        };
    }

    private static void ApplyFieldUpdate(DynamicFunction func, string field, string value)
    {
        switch (field)
        {
            case "name":
                ValidateFunctionName(value);
                func.Name = value.Trim();
                break;

            case "description":
                func.Description = value ?? "";
                break;

            case "parameters":
                func.Parameters = JsonSerializer.Deserialize<List<DynParam>>(value ?? "[]", ReadOptions)
                    ?? new List<DynParam>();
                break;

            case "function_type":
                var functionType = string.IsNullOrWhiteSpace(value) ? "" : value.Trim();
                if (!string.Equals(functionType, "code", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(functionType, "instructions", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException("function_type must be 'code' or 'instructions'.");
                }
                func.FunctionType = functionType;
                break;

            case "body":
                func.Body = value ?? "";
                break;

            case "output_instructions":
                func.OutputInstructions = string.IsNullOrWhiteSpace(value) ? null : value;
                break;

            case "dependencies":
                func.Dependencies = string.IsNullOrWhiteSpace(value) ? "" : value.Trim();
                break;

            default:
                throw new ArgumentException(
                    "field must be one of: name, description, parameters, function_type, body, output_instructions, dependencies.");
        }
    }

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

    // ── Dependency tracking ────────────────────────────────────────────────────

    private static List<string> GetFunctionNames(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM functions ORDER BY LENGTH(name) DESC";
        var names = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            names.Add(reader.GetString(0));
        return names;
    }

    private static List<string> ExtractDependencies(DynamicFunction func, IReadOnlyList<string>? knownFunctions = null)
    {
        if (string.IsNullOrWhiteSpace(func.Body))
            return new List<string>();

        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Scan body for known function names (works for both code and instructions)
        if (knownFunctions != null)
        {
            var body = func.Body;
            foreach (var name in knownFunctions)
            {
                // Skip self-reference
                if (string.Equals(name, func.Name, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Fast contains check before expensive regex
                if (body.Contains(name, StringComparison.OrdinalIgnoreCase))
                {
                    // Word boundary check to avoid partial matches (e.g. "get_time" in "get_time_string")
                    if (Regex.IsMatch(body, @"(?<![A-Za-z0-9_-])" + Regex.Escape(name) + @"(?![A-Za-z0-9_-])", RegexOptions.IgnoreCase))
                        found.Add(name);
                }
            }
        }

        return found
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string DependenciesToCsv(List<string> deps)
        => deps.Count == 0 ? "" : string.Join(",", deps);

    private static List<string> FindDirectMutualDependencies(
        SqliteConnection conn,
        string functionName,
        IReadOnlyCollection<string> dependencies)
    {
        if (string.IsNullOrWhiteSpace(functionName) || dependencies.Count == 0)
            return new List<string>();

        return dependencies
            .Where(dep => FunctionBodyReferencesName(conn, dep, functionName))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool FunctionBodyReferencesName(SqliteConnection conn, string sourceFunctionName, string referencedFunctionName)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT body FROM functions WHERE name = @name";
        cmd.Parameters.AddWithValue("@name", sourceFunctionName);

        var body = cmd.ExecuteScalar() as string;
        if (string.IsNullOrWhiteSpace(body) || string.IsNullOrWhiteSpace(referencedFunctionName))
            return false;

        if (!body.Contains(referencedFunctionName, StringComparison.OrdinalIgnoreCase))
            return false;

        return Regex.IsMatch(
            body,
            @"(?<![A-Za-z0-9_-])" + Regex.Escape(referencedFunctionName) + @"(?![A-Za-z0-9_-])",
            RegexOptions.IgnoreCase);
    }

    private static List<string> FindDependentsOf(SqliteConnection conn, string functionName)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT name FROM functions
            WHERE dependencies = @exact
               OR dependencies LIKE @start
               OR dependencies LIKE @mid
               OR dependencies LIKE @end";
        cmd.Parameters.AddWithValue("@exact", functionName);
        cmd.Parameters.AddWithValue("@start", functionName + ",%");
        cmd.Parameters.AddWithValue("@mid", "%," + functionName + ",%");
        cmd.Parameters.AddWithValue("@end", "%," + functionName);

        var dependents = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            dependents.Add(reader.GetString(0));
        return dependents;
    }

    private static void InsertFunction(SqliteConnection conn, DynamicFunction func, byte[]? assemblyBytes)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT OR REPLACE INTO functions (name, description, parameters, function_type, body, compiled_assembly, output_instructions, dependencies)
            VALUES (@name, @description, @parameters, @function_type, @body, @compiled_assembly, @output_instructions, @dependencies)";
        cmd.Parameters.AddWithValue("@name", func.Name);
        cmd.Parameters.AddWithValue("@description", func.Description);
        cmd.Parameters.AddWithValue("@parameters", JsonSerializer.Serialize(func.Parameters));
        cmd.Parameters.AddWithValue("@function_type", func.FunctionType ?? "code");
        cmd.Parameters.AddWithValue("@body", func.Body);
        cmd.Parameters.AddWithValue("@compiled_assembly", (object?)assemblyBytes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@output_instructions", (object?)func.OutputInstructions ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@dependencies", (object?)func.Dependencies ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    // ── Database Switching ────────────────────────────────────────────────────

    private static readonly string DefaultDatabasePath = Path.Combine(
        McpConstants.GetDefaultDatabaseDirectory(),
        McpConstants.DefaultDatabaseFileName);

    [McpServerTool(Name = "get_database")]
    [Description("Returns the path of the currently active ScriptMCP database.")]
    public string GetDatabase()
    {
        return SavePath;
    }

    [McpServerTool(Name = "set_database")]
    [Description("Sets the active ScriptMCP database at runtime. Similar to the --db CLI argument but can be used during a session. If no path is provided, switches to the default database. If only a name is provided (no directory separators), it is resolved relative to the default database directory. If the database does not exist, the user must confirm creation by setting create=true.")]
    public string SetDatabase(
        [Description("Path to the SQLite database file, a database name (resolved relative to the default directory), or omit to switch to the default database")] string path = "",
        [Description("Set to true to confirm creating a new database when the file does not exist")] bool create = false)
    {
        string resolvedPath;

        if (string.IsNullOrWhiteSpace(path))
        {
            resolvedPath = DefaultDatabasePath;
        }
        else
        {
            var trimmed = path.Trim();

            // If it's just a name with no directory separators, resolve relative to the default directory
            if (!trimmed.Contains(Path.DirectorySeparatorChar) &&
                !trimmed.Contains(Path.AltDirectorySeparatorChar))
            {
                // Append .db extension if not already present
                if (!trimmed.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
                    trimmed += ".db";

                resolvedPath = Path.Combine(McpConstants.GetDefaultDatabaseDirectory(), trimmed);
            }
            else
            {
                resolvedPath = Path.GetFullPath(trimmed);
            }
        }

        var previousPath = SavePath;

        if (string.Equals(resolvedPath, previousPath, StringComparison.OrdinalIgnoreCase))
            return $"Already using database: {resolvedPath}";

        if (!File.Exists(resolvedPath) && !create)
            return $"Database does not exist:\n  {resolvedPath}\nAsk the user if they want to create it. If yes, call set_database again with create=true.";

        SavePath = resolvedPath;
        EnsureDatabase();

        return $"Switched database from:\n  {previousPath}\nto:\n  {resolvedPath}";
    }

    [McpServerTool(Name = "delete_database")]
    [Description("Deletes a ScriptMCP database file. Call with confirm=false first to validate the path and get a yes-or-no confirmation prompt. The default database cannot be deleted. If the target database is currently active, it will be switched to the default database first.")]
    public string DeleteDatabase(
        [Description("Path to the SQLite database file, or a database name (resolved relative to the default directory)")] string path,
        [Description("Must be set to true to confirm deletion. If false, returns a confirmation prompt instead.")] bool confirm = false)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "Error: path cannot be empty.";

        var trimmed = path.Trim();

        string resolvedPath;
        if (!trimmed.Contains(Path.DirectorySeparatorChar) &&
            !trimmed.Contains(Path.AltDirectorySeparatorChar))
        {
            if (!trimmed.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
                trimmed += ".db";
            resolvedPath = Path.Combine(McpConstants.GetDefaultDatabaseDirectory(), trimmed);
        }
        else
        {
            resolvedPath = Path.GetFullPath(trimmed);
        }

        if (string.Equals(resolvedPath, DefaultDatabasePath, StringComparison.OrdinalIgnoreCase))
            return "Error: the default database cannot be deleted.";

        if (!File.Exists(resolvedPath))
            return $"Error: database not found: {resolvedPath}";

        if (!confirm)
            return $"Delete this database?\n  {resolvedPath}\nSay yes or no.";

        // If deleting the currently active database, switch to default first
        if (string.Equals(resolvedPath, SavePath, StringComparison.OrdinalIgnoreCase))
        {
            SavePath = DefaultDatabasePath;
            EnsureDatabase();
        }

        SqliteConnection.ClearAllPools();
        DeleteDatabaseFileWithRetry(resolvedPath);
        return $"Deleted database: {resolvedPath}\nActive database: {SavePath}";
    }

    private static void DeleteDatabaseFileWithRetry(string path)
    {
        const int maxAttempts = 5;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                File.Delete(path);
                return;
            }
            catch (IOException) when (attempt < maxAttempts - 1)
            {
                Thread.Sleep(50);
                SqliteConnection.ClearAllPools();
            }
        }
    }
}
