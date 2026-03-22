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

public class Script
{
    [JsonPropertyName("Name")]                public string        Name                { get; set; } = "";
    [JsonPropertyName("Description")]         public string        Description         { get; set; } = "";
    [JsonPropertyName("Parameters")]          public List<DynParam> Parameters         { get; set; } = new();
    [JsonPropertyName("FunctionType")]        public string        FunctionType        { get; set; } = "code";
    [JsonPropertyName("Body")]                public string        Body                { get; set; } = "";
    [JsonPropertyName("OutputInstructions")]  public string?       OutputInstructions  { get; set; }
    [JsonPropertyName("Dependencies")]        public string?       Dependencies        { get; set; } = "";
    [JsonPropertyName("CodeFormat")]          public string?       CodeFormat          { get; set; }
}

internal sealed class CompilationOutcome
{
    public byte[]? Bytes { get; init; }
    public string? Errors { get; init; }
}

// ── ScriptTools ──────────────────────────────────────────────────────────────

public class ScriptTools
{
    private const string TopLevelCodeFormat = "top_level";
    private const string UnmigratedCodeFormat = "legacy_method_body";

    private enum ScriptProcessOutputMode
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

    private static readonly object _consoleRedirectLock = new();

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

    public ScriptTools() => Initialize();

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

        // Migrate: rename old 'functions' table to 'scripts' if needed
        using var checkOld = conn.CreateCommand();
        checkOld.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='functions'";
        var hasOldTable = (long)checkOld.ExecuteScalar()! > 0;

        using var checkNew = conn.CreateCommand();
        checkNew.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='scripts'";
        var hasNewTable = (long)checkNew.ExecuteScalar()! > 0;

        if (hasOldTable && !hasNewTable)
        {
            using var rename = conn.CreateCommand();
            rename.CommandText = "ALTER TABLE functions RENAME TO scripts";
            rename.ExecuteNonQuery();

            // Rename column function_type -> script_type
            using var renameCol = conn.CreateCommand();
            renameCol.CommandText = "ALTER TABLE scripts RENAME COLUMN function_type TO script_type";
            renameCol.ExecuteNonQuery();

            Console.Error.WriteLine("Migrated table 'functions' -> 'scripts' and column 'function_type' -> 'script_type'.");
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS scripts (
                name                TEXT PRIMARY KEY COLLATE NOCASE,
                description         TEXT NOT NULL,
                parameters          TEXT NOT NULL,
                script_type         TEXT NOT NULL DEFAULT 'code',
                code_format         TEXT NOT NULL DEFAULT 'top_level',
                body                TEXT NOT NULL,
                compiled_assembly   BLOB,
                output_instructions TEXT
            );";
        cmd.ExecuteNonQuery();

        // Migrate: add output_instructions column if missing (existing DBs)
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA table_info(scripts)";
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
            alter.CommandText = "ALTER TABLE scripts ADD COLUMN output_instructions TEXT";
            alter.ExecuteNonQuery();
        }

        // Migrate: add dependencies column if missing (existing DBs)
        bool hasDependencies = false;
        using var pragma2 = conn.CreateCommand();
        pragma2.CommandText = "PRAGMA table_info(scripts)";
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
            alter2.CommandText = "ALTER TABLE scripts ADD COLUMN dependencies TEXT";
            alter2.ExecuteNonQuery();
        }

        bool hasCodeFormat = false;
        using var pragma3 = conn.CreateCommand();
        pragma3.CommandText = "PRAGMA table_info(scripts)";
        using (var reader3 = pragma3.ExecuteReader())
        {
            while (reader3.Read())
            {
                if (string.Equals(reader3.GetString(1), "code_format", StringComparison.OrdinalIgnoreCase))
                { hasCodeFormat = true; break; }
            }
        }
        if (!hasCodeFormat)
        {
            using var alter3 = conn.CreateCommand();
            alter3.CommandText = "ALTER TABLE scripts ADD COLUMN code_format TEXT";
            alter3.ExecuteNonQuery();
        }

        // Backfill: scan existing scripts that have never been scanned (dependencies IS NULL)
        BackfillDependencies(conn);
        MigrateLegacyCodeScripts(conn);
    }

    private static void BackfillDependencies(SqliteConnection conn)
    {
        var knownNames = GetScriptNames(conn);

        using var scanCmd = conn.CreateCommand();
        scanCmd.CommandText = "SELECT name, parameters, script_type, body FROM scripts WHERE dependencies IS NULL";

        var toUpdate = new List<(string name, string deps)>();
        using (var scanReader = scanCmd.ExecuteReader())
        {
            while (scanReader.Read())
            {
                var func = new Script
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
            upd.CommandText = "UPDATE scripts SET dependencies = @deps WHERE name = @name";
            upd.Parameters.AddWithValue("@deps", deps);
            upd.Parameters.AddWithValue("@name", name);
            upd.ExecuteNonQuery();
        }

        if (toUpdate.Count > 0)
            Console.Error.WriteLine($"Backfilled dependencies for {toUpdate.Count} script(s).");
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
            countCmd.CommandText = "SELECT COUNT(*) FROM scripts";
            var count = (long)countCmd.ExecuteScalar()!;
            if (count > 0) return;
        }

        try
        {
            var json = File.ReadAllText(jsonPath);
            var funcs = JsonSerializer.Deserialize<List<Script>>(json, ReadOptions);
            if (funcs == null || funcs.Count == 0) return;

            var migrationNames = funcs.Select(f => f.Name).ToList();

            int migrated = 0;
            foreach (var func in funcs)
            {
                byte[]? assemblyBytes = null;
                if (!IsInstructions(func))
                {
                    func.Body = ConvertLegacyMethodBodyToTopLevel(func);
                    func.CodeFormat = TopLevelCodeFormat;
                    var compiled = CompileFunction(func);
                    if (compiled.Bytes == null)
                    {
                        Console.Error.WriteLine($"Migration: failed to compile '{func.Name}': {compiled.Errors}");
                        // Store without compiled assembly — will fail at call time but data is preserved
                    }
                    assemblyBytes = compiled.Bytes;
                }

                var deps = ExtractDependencies(func, migrationNames);
                func.Dependencies = DependenciesToCsv(deps);
                InsertScript(conn, func, assemblyBytes);
                migrated++;
            }

            Console.Error.WriteLine($"Migrated {migrated} script(s) from {jsonPath} to SQLite.");

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

    [McpServerTool(Name = "list_scripts")]
    [Description("Lists all registered script names as a comma-delimited string")]
    public string ListScripts()
    {
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM scripts ORDER BY name";

        using var reader = cmd.ExecuteReader();
        var names = new List<string>();

        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return string.Join(", ", names);
    }

    // ── Deletion ──────────────────────────────────────────────────────────────

    [McpServerTool(Name = "delete_script")]
    [Description("Deletes a registered script from the database by name")]
    public string DeleteScript(
        [Description("The name of the script to delete")] string name,
        [Description("Set to true to force deletion when other scripts depend on this one")] bool forced = false)
    {
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();

        var dependents = FindDependentsOf(conn, name);

        if (dependents.Count > 0 && !forced)
        {
            return $"Cannot delete '{name}' because these scripts depend on it: {string.Join(", ", dependents)}.\n" +
                   "User confirmation is required before forced deletion.";
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM scripts WHERE name = @name";
        cmd.Parameters.AddWithValue("@name", name);

        var rows = cmd.ExecuteNonQuery();
        if (rows == 0)
            return $"Script '{name}' not found.";

        var msg = $"Script '{name}' deleted successfully.";
        if (dependents.Count > 0)
            msg += $" Note: the following script(s) depended on it and may break: {string.Join(", ", dependents)}.";
        return msg;
    }

    // ── Inspection ─────────────────────────────────────────────────────────────

    [McpServerTool(Name = "inspect_script")]
    [Description("Inspects a registered script and returns metadata and parameters. Set fullInspection=true to also include source code and compiled status.")]
    public string InspectScript(
        [Description("The name of the script to inspect")] string name,
        [Description("When true, include source code and compiled status. When false or omitted, omit those details.")] bool fullInspection = false)
    {
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name, description, parameters, script_type, code_format, body, compiled_assembly, output_instructions, dependencies FROM scripts WHERE name = @name";
        cmd.Parameters.AddWithValue("@name", name);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return $"Script '{name}' not found. Use list_scripts to see available scripts.";

        var funcName            = reader.GetString(0);
        var description         = reader.GetString(1);
        var parametersJson      = reader.GetString(2);
        var functionType        = reader.GetString(3);
        var codeFormat          = reader.IsDBNull(4) ? TopLevelCodeFormat : reader.GetString(4);
        var body                = reader.GetString(5);
        var hasAssembly         = !reader.IsDBNull(6);
        var outputInstructions  = reader.IsDBNull(7) ? null : reader.GetString(7);
        var dependencies        = reader.IsDBNull(8) ? null : reader.GetString(8);

        var dynParams = JsonSerializer.Deserialize<List<DynParam>>(parametersJson, ReadOptions)
                        ?? new List<DynParam>();

        var isInstr = string.Equals(functionType, "instructions", StringComparison.OrdinalIgnoreCase);

        var sb = new StringBuilder();
        sb.AppendLine($"Script: {funcName}");
        sb.AppendLine($"Type:        {functionType}");
        if (!isInstr)
            sb.AppendLine($"Code Format: {codeFormat}");
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

    [McpServerTool(Name = "create_script")]
    [Description("Creates a new script that can be called later. Use scriptType 'instructions' " +
                 "for plain English instructions (supports {paramName} substitution). " +
                 "Use scriptType 'code' for top-level C# source, like a Program.cs file, compiled and executed at runtime via Roslyn.")]
    public string CreateScript(
        [Description("Script name")] string name,
        [Description("Description of what the script does")] string description,
        [Description("JSON array of parameters, e.g. [{\"name\":\"x\",\"type\":\"int\",\"description\":\"The number\"}]")]
            string parameters,
        [Description("Plain English instructions (supports {paramName} substitution) or top-level C# source depending on scriptType")]
            string body,
        [Description("Script type: 'instructions' for plain English (recommended), or 'code' for C# source (compiled at runtime)")]
            string functionType = "instructions",
        [Description("Optional instructions for how to present/format the output after execution (e.g. 'present as a markdown table', 'summarize in bullet points')")]
            string outputInstructions = "")
    {
        try
        {
            var dynParams = JsonSerializer.Deserialize<List<DynParam>>(parameters, ReadOptions)
                            ?? new List<DynParam>();
            var resolvedFunctionType = string.IsNullOrWhiteSpace(functionType) ? "instructions" : functionType;

            var func = new Script
            {
                Name                = name,
                Description         = description,
                Parameters          = dynParams,
                FunctionType        = resolvedFunctionType,
                Body                = body,
                OutputInstructions  = string.IsNullOrWhiteSpace(outputInstructions)
                    ? null
                    : outputInstructions,
                CodeFormat          = string.Equals(resolvedFunctionType, "instructions", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : TopLevelCodeFormat,
            };

            ValidateScriptName(func.Name);

            byte[]? assemblyBytes = null;

            if (!IsInstructions(func))
            {
                var compiled = CompileFunction(func);
                if (compiled.Bytes == null)
                    return $"Compilation failed:\n{compiled.Errors}";
                assemblyBytes = compiled.Bytes;
            }

            using var conn = new SqliteConnection(ConnectionString);
            conn.Open();

            var knownNames = GetScriptNames(conn);
            var deps = ExtractDependencies(func, knownNames);
            var mutualDeps = FindDirectMutualDependencies(conn, func.Name, deps);
            if (mutualDeps.Count > 0)
            {
                return $"Creation failed: direct circular dependency detected for '{func.Name}': " +
                       $"{string.Join(", ", mutualDeps.Select(d => $"{func.Name} <-> {d}"))}.";
            }
            func.Dependencies = DependenciesToCsv(deps);

            InsertScript(conn, func, assemblyBytes);

            return $"{(IsInstructions(func) ? "Instructions" : "Code")} script '{func.Name}' created successfully " +
                   $"with {func.Parameters.Count} parameter(s).";
        }
        catch (Exception ex)
        {
            return $"Creation failed: {ex.Message}";
        }
    }

    [McpServerTool(Name = "load_script")]
    [Description("Loads a script from a file. If the script does not exist, it is created. If it already exists, it is updated from the file contents. " +
                 "By default, updates preserve the existing description, parameters, script_type, and output_instructions unless new values are provided.")]
    public string LoadScript(
        [Description("Path to the local file containing the script source or instructions")] string path,
        [Description("Optional script name. Defaults to the file name without extension.")] string name = "",
        [Description("Optional description. On update, omit to preserve the existing description.")] string description = "",
        [Description("Optional JSON array of parameters. On update, omit to preserve the existing parameters.")] string parameters = "",
        [Description("Optional script type: 'code' or 'instructions'. On update, omit to preserve the existing type. New scripts default to 'code'.")] string scriptType = "",
        [Description("Optional output instructions. On update, omit to preserve existing output instructions.")] string outputInstructions = "")
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
                return "Load failed: path is required.";

            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
                return $"Load failed: file not found: {fullPath}";

            var body = File.ReadAllText(fullPath);
            var resolvedName = string.IsNullOrWhiteSpace(name)
                ? Path.GetFileNameWithoutExtension(fullPath)
                : name.Trim();

            ValidateScriptName(resolvedName);

            using var conn = new SqliteConnection(ConnectionString);
            conn.Open();

            using var readCmd = conn.CreateCommand();
            readCmd.CommandText = @"
                SELECT description, parameters, script_type, output_instructions
                FROM scripts
                WHERE name = @name";
            readCmd.Parameters.AddWithValue("@name", resolvedName);

            using var reader = readCmd.ExecuteReader();
            var exists = reader.Read();

            var resolvedDescription = !string.IsNullOrWhiteSpace(description)
                ? description
                : exists
                    ? reader.GetString(0)
                    : $"Loaded from file: {fullPath}";

            var resolvedParameters = !string.IsNullOrWhiteSpace(parameters)
                ? parameters
                : exists
                    ? reader.GetString(1)
                    : "[]";

            var resolvedScriptType = !string.IsNullOrWhiteSpace(scriptType)
                ? scriptType
                : exists
                    ? reader.GetString(2)
                    : "code";

            var resolvedOutputInstructions = !string.IsNullOrWhiteSpace(outputInstructions)
                ? outputInstructions
                : exists && !reader.IsDBNull(3)
                    ? reader.GetString(3)
                    : "";

            reader.Close();

            var result = CreateScript(
                name: resolvedName,
                description: resolvedDescription,
                parameters: resolvedParameters,
                body: body,
                functionType: resolvedScriptType,
                outputInstructions: resolvedOutputInstructions);

            if (result.StartsWith("Compilation failed:", StringComparison.OrdinalIgnoreCase) ||
                result.StartsWith("Creation failed:", StringComparison.OrdinalIgnoreCase))
            {
                return result;
            }

            return exists
                ? $"Script '{resolvedName}' loaded from '{fullPath}' and updated."
                : $"Script '{resolvedName}' loaded from '{fullPath}' and created.";
        }
        catch (Exception ex)
        {
            return $"Load failed: {ex.Message}";
        }
    }

    [McpServerTool(Name = "export_script")]
    [Description("Exports a stored script to a local file. By default it writes to <name>.cs for code scripts and <name>.txt for instructions scripts.")]
    public string ExportScript(
        [Description("The name of the script to export")] string name,
        [Description("Optional destination path. Defaults to <name>.cs for code scripts or <name>.txt for instructions scripts in the current working directory.")] string path = "")
    {
        try
        {
            if (string.IsNullOrWhiteSpace(name))
                return "Export failed: name is required.";

            using var conn = new SqliteConnection(ConnectionString);
            conn.Open();

            using var readCmd = conn.CreateCommand();
            readCmd.CommandText = @"
                SELECT script_type, body
                FROM scripts
                WHERE name = @name";
            readCmd.Parameters.AddWithValue("@name", name);

            using var reader = readCmd.ExecuteReader();
            if (!reader.Read())
                return $"Script '{name}' not found.";

            var scriptType = reader.GetString(0);
            var body = reader.GetString(1);
            reader.Close();

            var extension = string.Equals(scriptType, "instructions", StringComparison.OrdinalIgnoreCase)
                ? ".txt"
                : ".cs";

            var resolvedPath = string.IsNullOrWhiteSpace(path)
                ? Path.Combine(Directory.GetCurrentDirectory(), name + extension)
                : Path.GetFullPath(path);

            var resolvedDirectory = Path.GetDirectoryName(resolvedPath);
            if (!string.IsNullOrWhiteSpace(resolvedDirectory))
                Directory.CreateDirectory(resolvedDirectory);

            File.WriteAllText(resolvedPath, body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return $"Script '{name}' exported to '{resolvedPath}'.";
        }
        catch (Exception ex)
        {
            return $"Export failed: {ex.Message}";
        }
    }

    [McpServerTool(Name = "update_script")]
    [Description("Updates a single field on an existing script entry. " +
                 "Supported fields: name, description, parameters, script_type, body, output_instructions, dependencies. " +
                 "When the update affects execution, the script is recompiled automatically.")]
    public string UpdateScript(
        [Description("The existing script name to update")] string name,
        [Description("The field/column to update: name, description, parameters, script_type, body, output_instructions, or dependencies")] string field,
        [Description("The new value for that field")] string value)
    {
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();

        using var readCmd = conn.CreateCommand();
        readCmd.CommandText = @"
            SELECT name, description, parameters, script_type, body, output_instructions, dependencies
                 , code_format
            FROM scripts
            WHERE name = @name";
        readCmd.Parameters.AddWithValue("@name", name);

        using var reader = readCmd.ExecuteReader();
        if (!reader.Read())
            return $"Script '{name}' not found.";

        var func = new Script
        {
            Name = reader.GetString(0),
            Description = reader.GetString(1),
            Parameters = JsonSerializer.Deserialize<List<DynParam>>(reader.GetString(2), ReadOptions) ?? new List<DynParam>(),
            FunctionType = reader.GetString(3),
            Body = reader.GetString(4),
            OutputInstructions = reader.IsDBNull(5) ? null : reader.GetString(5),
            Dependencies = reader.IsDBNull(6) ? "" : reader.GetString(6),
            CodeFormat = reader.IsDBNull(7) ? TopLevelCodeFormat : reader.GetString(7),
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
            var compiled = CompileFunction(func);
            if (compiled.Bytes == null)
                return $"Update failed: compilation failed after changing '{normalizedField}':\n{compiled.Errors}";

            assemblyBytes = compiled.Bytes;
        }

        // Auto-compute dependencies unless the user is explicitly setting them
        if (!string.Equals(normalizedField, "dependencies", StringComparison.OrdinalIgnoreCase))
        {
            var knownNames = GetScriptNames(conn);
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
            UPDATE scripts
            SET name = @new_name,
                description = @description,
                parameters = @parameters,
                script_type = @script_type,
                body = @body,
                compiled_assembly = @compiled_assembly,
                output_instructions = @output_instructions,
                dependencies = @dependencies,
                code_format = @code_format
            WHERE name = @original_name";
        updateCmd.Parameters.AddWithValue("@new_name", func.Name);
        updateCmd.Parameters.AddWithValue("@description", func.Description);
        updateCmd.Parameters.AddWithValue("@parameters", JsonSerializer.Serialize(func.Parameters));
        updateCmd.Parameters.AddWithValue("@script_type", func.FunctionType ?? "code");
        updateCmd.Parameters.AddWithValue("@body", func.Body);
        updateCmd.Parameters.AddWithValue("@compiled_assembly", (object?)assemblyBytes ?? DBNull.Value);
        updateCmd.Parameters.AddWithValue("@output_instructions", (object?)func.OutputInstructions ?? DBNull.Value);
        updateCmd.Parameters.AddWithValue("@dependencies", (object?)func.Dependencies ?? DBNull.Value);
        updateCmd.Parameters.AddWithValue("@code_format", (object?)func.CodeFormat ?? DBNull.Value);
        updateCmd.Parameters.AddWithValue("@original_name", name);

        try
        {
            var rows = updateCmd.ExecuteNonQuery();
            if (rows == 0)
            {
                tx.Rollback();
                return $"Script '{name}' not found.";
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
                    readDep.CommandText = "SELECT name, description, parameters, script_type, body, output_instructions, code_format FROM scripts WHERE name = @n";
                    readDep.Parameters.AddWithValue("@n", depName);

                    Script? depFunc = null;
                    using (var depReader = readDep.ExecuteReader())
                    {
                        if (!depReader.Read()) continue;
                        depFunc = new Script
                        {
                            Name = depReader.GetString(0),
                            Description = depReader.GetString(1),
                            Parameters = JsonSerializer.Deserialize<List<DynParam>>(depReader.GetString(2), ReadOptions) ?? new List<DynParam>(),
                            FunctionType = depReader.GetString(3),
                            Body = depReader.GetString(4),
                            OutputInstructions = depReader.IsDBNull(5) ? null : depReader.GetString(5),
                            CodeFormat = depReader.IsDBNull(6) ? TopLevelCodeFormat : depReader.GetString(6),
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
                        var compiledDep = CompileFunction(depFunc);
                        if (compiledDep.Bytes == null)
                        {
                            // Can't patch this caller — skip but don't fail the rename
                            Console.Error.WriteLine($"Rename auto-patch: failed to recompile '{depName}': {compiledDep.Errors}");
                            continue;
                        }
                        depAsm = compiledDep.Bytes;
                    }

                    var depKnown = GetScriptNames(conn);
                    var depDeps = ExtractDependencies(depFunc, depKnown);
                    depFunc.Dependencies = DependenciesToCsv(depDeps);

                    using var patchCmd = conn.CreateCommand();
                    patchCmd.Transaction = tx;
                    patchCmd.CommandText = @"
                        UPDATE scripts
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

            var msg = $"Script '{name}' updated successfully: {normalizedField}.";
            if (patchedCallers.Count > 0)
                msg += $" Auto-patched caller(s): {string.Join(", ", patchedCallers)}.";
            return msg;
        }
        catch (SqliteException) when (string.Equals(normalizedField, "name", StringComparison.OrdinalIgnoreCase))
        {
            tx.Rollback();
            return $"Update failed: a script named '{func.Name}' already exists.";
        }
    }

    // ── Compilation ──────────────────────────────────────────────────────────

    [McpServerTool(Name = "compile_script")]
    [Description("Compiles a registered code script from its stored source, refreshes the stored compiled assembly, and exports the assembly to a local file.")]
    public string CompileScript(
        [Description("The name of the script to compile")] string name,
        [Description("Optional destination path for the compiled assembly. Defaults to <name>.dll in the current working directory.")] string path = "")
    {
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();

        using var readCmd = conn.CreateCommand();
        readCmd.CommandText = "SELECT parameters, script_type, body FROM scripts WHERE name = @name";
        readCmd.Parameters.AddWithValue("@name", name);

        using var reader = readCmd.ExecuteReader();
        if (!reader.Read())
            return $"Script '{name}' not found.";

        var parametersJson = reader.GetString(0);
        var functionType   = reader.GetString(1);
        var body           = reader.GetString(2);
        reader.Close();

        if (string.Equals(functionType, "instructions", StringComparison.OrdinalIgnoreCase))
            return $"Script '{name}' is an instructions script — nothing to compile.";

        var dynParams = JsonSerializer.Deserialize<List<DynParam>>(parametersJson, ReadOptions)
                        ?? new List<DynParam>();

        var func = new Script
        {
            Name         = name,
            FunctionType = functionType,
            Body         = body,
            Parameters   = dynParams,
            CodeFormat   = TopLevelCodeFormat,
        };

        var compiled = CompileFunction(func);
        if (compiled.Bytes == null)
            return $"Recompilation failed:\n{compiled.Errors}";

        var resolvedPath = string.IsNullOrWhiteSpace(path)
            ? Path.Combine(Directory.GetCurrentDirectory(), $"{name}.dll")
            : Path.GetFullPath(path);
        var resolvedDirectory = Path.GetDirectoryName(resolvedPath);
        if (!string.IsNullOrWhiteSpace(resolvedDirectory))
            Directory.CreateDirectory(resolvedDirectory);

        using var updateCmd = conn.CreateCommand();
        updateCmd.CommandText = "UPDATE scripts SET compiled_assembly = @asm, code_format = @code_format WHERE name = @name";
        updateCmd.Parameters.AddWithValue("@name", name);
        updateCmd.Parameters.AddWithValue("@asm", compiled.Bytes);
        updateCmd.Parameters.AddWithValue("@code_format", TopLevelCodeFormat);
        updateCmd.ExecuteNonQuery();

        File.WriteAllBytes(resolvedPath, compiled.Bytes);
        return $"Script '{name}' compiled and exported to '{resolvedPath}'.";
    }

    // ── Invocation ────────────────────────────────────────────────────────────

    [McpServerTool(Name = "call_script")]
    [Description("Calls a previously registered script with the given arguments")]
    public string CallScript(
        [Description("The name of the script to call")] string name,
        [Description("JSON object of argument values, e.g. {\"x\": 5}")] string arguments = "{}")
    {
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name, description, parameters, script_type, body, compiled_assembly, output_instructions FROM scripts WHERE name = @name";
        cmd.Parameters.AddWithValue("@name", name);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return $"Script '{name}' not found. " +
                   "Use list_scripts to see available scripts.";

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
                return $"Script '{name}' has no compiled assembly. Re-register it to compile.";

            var assemblyBytes = (byte[])reader[5];
            result = ExecuteCompiledCode(name, assemblyBytes, dynParams, arguments);
        }

        if (!string.IsNullOrWhiteSpace(outputInstructions))
            result += $"\n\n[Output Instructions]: {outputInstructions}";

        return result;
    }

    // ── Out-of-process invocation ──────────────────────────────────────────────

    [McpServerTool(Name = "call_process")]
    [Description("Calls a script in a separate process (out-of-process execution). " +
                 "Useful for parallel execution or isolating side effects.")]
    public string CallProcess(
        [Description("The name of the script to call")] string name,
        [Description("JSON object of argument values, e.g. {\"x\": 5}")] string arguments = "{}",
        [Description("Output mode: Default (uses --exec, no persisted output file), WriteNew (uses --exec-out, writes a new file per execution), WriteAppend (uses --exec-out-append, appends to one stable file)")] string output_mode = "Default")
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
            return "Error: unable to resolve the current executable path.";

        if (!Enum.TryParse<ScriptProcessOutputMode>(output_mode, ignoreCase: true, out var outputMode))
            return "Error: invalid output_mode. Supported values: Default, WriteNew, WriteAppend.";

        var execFlag = outputMode switch
        {
            ScriptProcessOutputMode.WriteNew => "--exec-out",
            ScriptProcessOutputMode.WriteAppend => "--exec-out-append",
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
    [Description("Reads the most recent scheduled-task output file for the specified script.")]
    public string ReadScheduledTask(
        [Description("Script name whose latest scheduled-task output should be returned")] string function_name)
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
    [Description("Creates a scheduled task (Windows Task Scheduler or cron on Linux/macOS) that runs a ScriptMCP script at a given interval in minutes")]
    public string CreateScheduledTask(
        [Description("Name of the ScriptMCP script to run")] string function_name,
        [Description("JSON arguments for the script (default: {})")] string function_args = "{}",
        [Description("How often to run the task, in minutes")] int interval_minutes = 1,
        [Description("When true, append each result to a stable <script>.txt file instead of creating a new timestamped file")] bool append = false)
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
    [Description("Deletes a scheduled task (Windows Task Scheduler or cron on Linux/macOS) for a ScriptMCP script.")]
    public string DeleteScheduledTask(
        [Description("Name of the ScriptMCP script whose scheduled task should be deleted")] string function_name,
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
    [Description("Starts or enables a scheduled task for a ScriptMCP script.")]
    public string StartScheduledTask(
        [Description("Name of the ScriptMCP script whose scheduled task should be started")] string function_name,
        [Description("Interval in minutes used when the task was created")] int interval_minutes = 1)
    {
        if (OperatingSystem.IsWindows())
            return StartScheduledTaskWindows(function_name, interval_minutes);
        else
            return StartScheduledTaskCron(function_name);
    }

    [McpServerTool(Name = "stop_scheduled_task")]
    [Description("Stops or disables a scheduled task for a ScriptMCP script.")]
    public string StopScheduledTask(
        [Description("Name of the ScriptMCP script whose scheduled task should be stopped")] string function_name,
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
        sb.AppendLine($"  Script:   {function_name}({function_args})");
        sb.AppendLine($"  Exe:      {exePath}");
        sb.AppendLine($"  Interval: Every {interval_minutes} minute(s)");
        sb.AppendLine($"  Output:   {(append ? "Append to <script>.txt" : "New timestamped file per run")}");
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
        sb.AppendLine($"  Script:   {function_name}");
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
        sb.AppendLine($"  Script:   {function_name}");
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
        sb.AppendLine($"  Script:   {function_name}");
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
        sb.AppendLine($"  Script:   {function_name}({function_args})");
        sb.AppendLine($"  Exe:      {exePath}");
        sb.AppendLine($"  Schedule: {schedule}");
        sb.AppendLine($"  Tag:      {tag}");
        sb.AppendLine($"  Output:   {(append ? "Append to <script>.txt" : "New timestamped file per run")}");
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
        sb.AppendLine($"  Script:   {function_name}");
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

    private static CompilationOutcome CompileFunction(Script func)
    {
        var supportSource = BuildTopLevelSupportSource(func.Parameters);
        var userSource = func.Body ?? string.Empty;

        var syntaxTrees = new[]
        {
            CSharpSyntaxTree.ParseText(supportSource, path: "__ScriptMcpSupport.cs"),
            CSharpSyntaxTree.ParseText(userSource, path: $"{func.Name}.cs"),
        };

        var references = GatherMetadataReferences();
        references.Add(_helperAssembly.Value.reference);

        var compilation = CSharpCompilation.Create(
            assemblyName: $"Script_{func.Name}_{Guid.NewGuid():N}",
            syntaxTrees: syntaxTrees,
            references: references,
            options: new CSharpCompilationOptions(OutputKind.ConsoleApplication)
                .WithOptimizationLevel(OptimizationLevel.Release));

        using var peStream = new MemoryStream();
        var emitResult = compilation.Emit(peStream);

        if (!emitResult.Success)
        {
            var errors = emitResult.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString());
            return new CompilationOutcome { Errors = string.Join("\n", errors) };
        }

        return new CompilationOutcome { Bytes = peStream.ToArray() };
    }

    private static string BuildTopLevelSupportSource(List<DynParam> parameters)
    {
        var typedMembers = new StringBuilder();
        foreach (var param in parameters)
            typedMembers.AppendLine(BuildTopLevelProperty(param));

        return $$"""
            global using System;
            global using System.Collections.Generic;
            global using System.Globalization;
            global using System.IO;
            global using System.Linq;
            global using System.Net;
            global using System.Net.Http;
            global using System.Text;
            global using System.Text.RegularExpressions;
            global using System.Threading.Tasks;
            global using static __ScriptMcpGlobals;

            internal static class __ScriptMcpGlobals
            {
                public static Dictionary<string, string> scriptArgs => ScriptRuntime.GetArgs();
            {{typedMembers}}
            }
            """;
    }

    private static string BuildTopLevelProperty(DynParam param)
    {
        var csType = GetCSharpParameterType(param.Type);
        var defaultValue = GetDefaultLiteral(csType);
        var argName = EscapeStringLiteral(param.Name);

        var expression = csType switch
        {
            "int" => $$"""scriptArgs.TryGetValue("{{argName}}", out var raw) && int.TryParse(raw, out var parsed) ? parsed : {{defaultValue}}""",
            "long" => $$"""scriptArgs.TryGetValue("{{argName}}", out var raw) && long.TryParse(raw, out var parsed) ? parsed : {{defaultValue}}""",
            "double" => $$"""scriptArgs.TryGetValue("{{argName}}", out var raw) && double.TryParse(raw, out var parsed) ? parsed : {{defaultValue}}""",
            "float" => $$"""scriptArgs.TryGetValue("{{argName}}", out var raw) && float.TryParse(raw, out var parsed) ? parsed : {{defaultValue}}""",
            "bool" => $$"""scriptArgs.TryGetValue("{{argName}}", out var raw) && bool.TryParse(raw, out var parsed) ? parsed : {{defaultValue}}""",
            _ => $$"""scriptArgs.TryGetValue("{{argName}}", out var raw) ? raw : {{defaultValue}}""",
        };

        return $"    public static {csType} {param.Name} => {expression};";
    }

    private static string ConvertLegacyMethodBodyToTopLevel(Script func)
    {
        return $$"""
            var __scriptmcpArgs = scriptArgs;

            string __ScriptMcpLegacyMain()
            {
                var args = __scriptmcpArgs;
            {{IndentCode(func.Body, 4)}}
            }

            var __scriptmcpResult = __ScriptMcpLegacyMain();
            if (!string.IsNullOrEmpty(__scriptmcpResult))
                Console.Write(__scriptmcpResult);
            """;
    }

    private static string IndentCode(string code, int spaces)
    {
        var indent = new string(' ', spaces);
        var lines = (code ?? string.Empty).Replace("\r\n", "\n").Split('\n');
        return string.Join(Environment.NewLine, lines.Select(line => indent + line));
    }

    private static string GetCSharpParameterType(string? type) =>
        (type?.ToLowerInvariant()) switch
        {
            "int" => "int",
            "long" => "long",
            "double" => "double",
            "float" => "float",
            "bool" => "bool",
            _ => "string",
        };

    private static string GetDefaultLiteral(string csType) =>
        csType switch
        {
            "int" => "0",
            "long" => "0L",
            "double" => "0.0",
            "float" => "0f",
            "bool" => "false",
            _ => "\"\"",
        };

    private static string EscapeStringLiteral(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");

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
        using System.Collections.Generic;
        using System.Diagnostics;
        using System.Text.Json;
        using System.Threading;

        public static class ScriptRuntime
        {
            private static readonly AsyncLocal<string?> CurrentRawArguments = new();
            private static readonly AsyncLocal<Dictionary<string, string>?> CurrentArgs = new();

            public static void SetRawArguments(string arguments)
            {
                var raw = string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments;
                CurrentRawArguments.Value = raw;
                CurrentArgs.Value = ParseNamedArgs(raw);
            }

            public static void ClearArgs()
            {
                CurrentRawArguments.Value = null;
                CurrentArgs.Value = null;
            }

            public static string GetRawArguments()
            {
                return CurrentRawArguments.Value ?? "{}";
            }

            public static Dictionary<string, string> GetArgs()
            {
                return CurrentArgs.Value ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            private static Dictionary<string, string> ParseNamedArgs(string arguments)
            {
                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (string.IsNullOrWhiteSpace(arguments))
                    return values;

                try
                {
                    using var doc = JsonDocument.Parse(arguments);
                    if (doc.RootElement.ValueKind != JsonValueKind.Object)
                        return values;

                    foreach (var property in doc.RootElement.EnumerateObject())
                    {
                        values[property.Name] = property.Value.ValueKind == JsonValueKind.String
                            ? property.Value.GetString() ?? string.Empty
                            : property.Value.GetRawText();
                    }
                }
                catch
                {
                }

                return values;
            }
        }

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
            var rawArguments = NormalizeRawArguments(arguments);
            var commandLineArgs = BuildTopLevelCommandLineArgs(rawArguments);

            // Load into collectible ALC
            alc = new AssemblyLoadContext(funcName, isCollectible: true);
            var helperAssembly = alc.LoadFromStream(new MemoryStream(_helperAssembly.Value.bytes));
            var assembly = alc.LoadFromStream(new MemoryStream(assemblyBytes));

            Environment.SetEnvironmentVariable("SCRIPTMCP_DB", SavePath);
            SetScriptRuntimeArgs(helperAssembly, rawArguments);

            var entryPoint = assembly.EntryPoint;
            if (entryPoint == null)
                throw new InvalidOperationException("Compiled assembly missing entry point.");

            return ExecuteTopLevelAssembly(entryPoint, commandLineArgs);
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
            Environment.SetEnvironmentVariable("SCRIPTMCP_DB", null);
            alc?.Unload();
        }
    }

    private static void MigrateLegacyCodeScripts(SqliteConnection conn)
    {
        var pending = new List<Script>();

        using (var scanCmd = conn.CreateCommand())
        {
            scanCmd.CommandText = @"
                SELECT name, description, parameters, script_type, body, output_instructions, dependencies, code_format
                FROM scripts
                WHERE script_type = 'code' AND (code_format IS NULL OR code_format = '' OR code_format = @legacy)";
            scanCmd.Parameters.AddWithValue("@legacy", UnmigratedCodeFormat);

            using var reader = scanCmd.ExecuteReader();
            while (reader.Read())
            {
                pending.Add(new Script
                {
                    Name = reader.GetString(0),
                    Description = reader.GetString(1),
                    Parameters = JsonSerializer.Deserialize<List<DynParam>>(reader.GetString(2), ReadOptions) ?? new List<DynParam>(),
                    FunctionType = reader.GetString(3),
                    Body = reader.GetString(4),
                    OutputInstructions = reader.IsDBNull(5) ? null : reader.GetString(5),
                    Dependencies = reader.IsDBNull(6) ? "" : reader.GetString(6),
                    CodeFormat = reader.IsDBNull(7) ? null : reader.GetString(7),
                });
            }
        }

        foreach (var func in pending)
        {
            var migratedBody = ConvertLegacyMethodBodyToTopLevel(func);
            var migratedFunc = new Script
            {
                Name = func.Name,
                Description = func.Description,
                Parameters = func.Parameters,
                FunctionType = func.FunctionType,
                Body = migratedBody,
                OutputInstructions = func.OutputInstructions,
                Dependencies = func.Dependencies,
                CodeFormat = TopLevelCodeFormat,
            };

            var compiled = CompileFunction(migratedFunc);
            using var update = conn.CreateCommand();
            update.CommandText = @"
                UPDATE scripts
                SET body = @body,
                    code_format = @code_format,
                    compiled_assembly = @compiled_assembly
                WHERE name = @name";
            update.Parameters.AddWithValue("@name", migratedFunc.Name);
            update.Parameters.AddWithValue("@body", migratedBody);
            update.Parameters.AddWithValue("@code_format", TopLevelCodeFormat);
            update.Parameters.AddWithValue("@compiled_assembly", (object?)compiled.Bytes ?? DBNull.Value);
            update.ExecuteNonQuery();

            if (compiled.Bytes == null)
                Console.Error.WriteLine($"Top-level migration failed for '{migratedFunc.Name}': {compiled.Errors}");
        }

        if (pending.Count > 0)
            Console.Error.WriteLine($"Migrated {pending.Count} existing code script(s) to top-level source.");
    }

    private static void SetScriptRuntimeArgs(Assembly helperAssembly, string arguments)
    {
        var runtimeType = helperAssembly.GetType("ScriptRuntime")
            ?? throw new InvalidOperationException("Helper assembly missing ScriptRuntime type.");
        var setArgsMethod = runtimeType.GetMethod("SetRawArguments", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("Helper assembly missing ScriptRuntime.SetRawArguments method.");
        setArgsMethod.Invoke(null, new object[] { arguments });
    }

    private static string[] BuildTopLevelCommandLineArgs(string arguments)
    {
        return new[] { arguments };
    }

    private static string NormalizeRawArguments(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
            return "{}";

        try
        {
            return JsonDocument.Parse(arguments).RootElement.GetRawText();
        }
        catch
        {
            return "{}";
        }
    }

    private static string ExecuteTopLevelAssembly(MethodInfo entryPoint, string[] commandLineArgs)
    {
        lock (_consoleRedirectLock)
        {
            var originalOut = Console.Out;
            var originalError = Console.Error;
            using var stdout = new StringWriter(CultureInfo.InvariantCulture);
            using var stderr = new StringWriter(CultureInfo.InvariantCulture);

            try
            {
                Console.SetOut(stdout);
                Console.SetError(stderr);

                var parameters = entryPoint.GetParameters();
                object? invocationResult = parameters.Length == 0
                    ? entryPoint.Invoke(null, null)
                    : entryPoint.Invoke(null, new object?[] { commandLineArgs });

                if (invocationResult is System.Threading.Tasks.Task task)
                    task.GetAwaiter().GetResult();

                var stderrText = stderr.ToString();
                if (!string.IsNullOrEmpty(stderrText))
                    return stdout.ToString() + stderrText;

                return stdout.ToString();
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalError);
            }
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

    private static bool IsInstructions(Script f) =>
        string.Equals(f.FunctionType, "instructions", StringComparison.OrdinalIgnoreCase);

    private static void ValidateScriptName(string name)
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
            "script_type" => "script_type",
            "scripttype" => "script_type",
            "function_type" => "script_type",   // backward compat
            "functiontype" => "script_type",    // backward compat
            "body" => "body",
            "output_instructions" => "output_instructions",
            "outputinstructions" => "output_instructions",
            "dependencies" => "dependencies",
            _ => throw new ArgumentException(
                "field must be one of: name, description, parameters, script_type, body, output_instructions, dependencies."),
        };
    }

    private static void ApplyFieldUpdate(Script func, string field, string value)
    {
        switch (field)
        {
            case "name":
                ValidateScriptName(value);
                func.Name = value.Trim();
                break;

            case "description":
                func.Description = value ?? "";
                break;

            case "parameters":
                func.Parameters = JsonSerializer.Deserialize<List<DynParam>>(value ?? "[]", ReadOptions)
                    ?? new List<DynParam>();
                break;

            case "script_type":
                var scriptType = string.IsNullOrWhiteSpace(value) ? "" : value.Trim();
                if (!string.Equals(scriptType, "code", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(scriptType, "instructions", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException("script_type must be 'code' or 'instructions'.");
                }
                func.FunctionType = scriptType;
                func.CodeFormat = string.Equals(scriptType, "code", StringComparison.OrdinalIgnoreCase)
                    ? TopLevelCodeFormat
                    : null;
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
                    "field must be one of: name, description, parameters, script_type, body, output_instructions, dependencies.");
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

    private static List<string> GetScriptNames(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM scripts ORDER BY LENGTH(name) DESC";
        var names = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            names.Add(reader.GetString(0));
        return names;
    }

    private static List<string> ExtractDependencies(Script func, IReadOnlyList<string>? knownFunctions = null)
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
        cmd.CommandText = "SELECT body FROM scripts WHERE name = @name";
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
            SELECT name FROM scripts
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

    private static void InsertScript(SqliteConnection conn, Script func, byte[]? assemblyBytes)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT OR REPLACE INTO scripts (name, description, parameters, script_type, code_format, body, compiled_assembly, output_instructions, dependencies)
            VALUES (@name, @description, @parameters, @script_type, @code_format, @body, @compiled_assembly, @output_instructions, @dependencies)";
        cmd.Parameters.AddWithValue("@name", func.Name);
        cmd.Parameters.AddWithValue("@description", func.Description);
        cmd.Parameters.AddWithValue("@parameters", JsonSerializer.Serialize(func.Parameters));
        cmd.Parameters.AddWithValue("@script_type", func.FunctionType ?? "code");
        cmd.Parameters.AddWithValue("@code_format", (object?)func.CodeFormat ?? DBNull.Value);
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
