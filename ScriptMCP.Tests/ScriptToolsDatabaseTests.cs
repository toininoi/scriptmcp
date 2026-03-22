using ScriptMCP.Library;

namespace ScriptMCP.Tests;

[Collection("ScriptTools tests")]
public sealed class ScriptToolsDatabaseTests
{
    private readonly TestDatabaseFixture _fixture;
    private readonly ScriptTools _tools;

    public ScriptToolsDatabaseTests(TestDatabaseFixture fixture)
    {
        _fixture = fixture;
        _tools = new ScriptTools();
    }

    [Fact]
    public void RegistersAndExecutesFunctionUsingDedicatedTestDatabaseFile()
    {
        var name = UniqueName("test_add_two_numbers");
        var registerResult = _tools.CreateScript(
            name: name,
            description: "Adds two integers.",
            parameters: """[{"name":"x","type":"int","description":"first"},{"name":"y","type":"int","description":"second"}]""",
            body: "Console.Write((x + y).ToString());",
            functionType: "code",
            outputInstructions: "");

        Assert.Contains("created successfully", registerResult, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(_fixture.DatabasePath));

        var listResult = _tools.ListScripts();
        Assert.Contains(name, listResult, StringComparison.Ordinal);

        var callResult = _tools.CallScript(name, """{"x":2,"y":3}""");
        Assert.Equal("5", callResult);
    }

    [Fact]
    public void InspectSupportsBasicAndFullInspectionModes()
    {
        var name = UniqueName("test_inspect");
        _tools.CreateScript(
            name: name,
            description: "Inspect me.",
            parameters: """[{"name":"x","type":"int","description":"value"}]""",
            body: "Console.Write(x.ToString());",
            functionType: "code",
            outputInstructions: "return exactly");

        var basic = _tools.InspectScript(name);
        Assert.Contains($"Script: {name}", basic, StringComparison.Ordinal);
        Assert.DoesNotContain("Compiled:", basic, StringComparison.Ordinal);
        Assert.DoesNotContain("Source (C# Code):", basic, StringComparison.Ordinal);
        Assert.Contains("Output Instructions: return exactly", basic, StringComparison.Ordinal);

        var full = _tools.InspectScript(name, fullInspection: true);
        Assert.Contains("Compiled:    Yes", full, StringComparison.Ordinal);
        Assert.Contains("Source (C# Code):", full, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateBodyRecompilesAndChangesBehavior()
    {
        var name = UniqueName("test_update");
        _tools.CreateScript(
            name: name,
            description: "Math function.",
            parameters: """[{"name":"x","type":"int","description":"first"},{"name":"y","type":"int","description":"second"}]""",
            body: "Console.Write((x + y).ToString());",
            functionType: "code",
            outputInstructions: "");

        var before = _tools.CallScript(name, """{"x":2,"y":3}""");
        Assert.Equal("5", before);

        var update = _tools.UpdateScript(name, "body", "Console.Write((x * y).ToString());");
        Assert.Contains("updated successfully: body", update, StringComparison.OrdinalIgnoreCase);

        var after = _tools.CallScript(name, """{"x":2,"y":3}""");
        Assert.Equal("6", after);
    }

    [Fact]
    public void RegistersAndExecutesTopLevelConsoleScriptUsingStdout()
    {
        var name = UniqueName("test_top_level");
        var registerResult = _tools.CreateScript(
            name: name,
            description: "Writes a greeting from top-level code.",
            parameters: """[{"name":"name","type":"string","description":"person"}]""",
            body: """
using System;

string Format(string value) => $"Hello, {value}!";

Console.Write(Format(name));
""",
            functionType: "code",
            outputInstructions: "");

        Assert.Contains("created successfully", registerResult, StringComparison.OrdinalIgnoreCase);

        var callResult = _tools.CallScript(name, """{"name":"Bill"}""");
        Assert.Equal("Hello, Bill!", callResult);
    }

    [Fact]
    public void TopLevelConsoleScriptReceivesRawJsonArgumentAndTypedProperties()
    {
        var name = UniqueName("test_top_level_raw_json");
        var registerResult = _tools.CreateScript(
            name: name,
            description: "Uses raw json args and typed imports.",
            parameters: """[{"name":"city","type":"string","description":"city"}]""",
            body: """
using System;

Console.Write(args[0] + "|" + city + "|" + scriptArgs["city"]);
""",
            functionType: "code",
            outputInstructions: "");

        Assert.Contains("created successfully", registerResult, StringComparison.OrdinalIgnoreCase);

        var callResult = _tools.CallScript(name, """{"city":"Athens"}""");
        Assert.Equal("""{"city":"Athens"}|Athens|Athens""", callResult);
    }

    [Fact]
    public void TopLevelConsoleScriptReceivesJsonPayloadAtArgsZero()
    {
        var name = UniqueName("test_top_level_args_zero");
        var registerResult = _tools.CreateScript(
            name: name,
            description: "Uses raw json payload at args zero.",
            parameters: "[]",
            body: """
using System;

Console.Write(args.Length + "|" + args[0]);
""",
            functionType: "code",
            outputInstructions: "");

        Assert.Contains("created successfully", registerResult, StringComparison.OrdinalIgnoreCase);

        var callResult = _tools.CallScript(name, """{"city":"Athens","units":"metric"}""");
        Assert.Equal("""1|{"city":"Athens","units":"metric"}""", callResult);
    }

    [Fact]
    public void CreateScriptRejectsLegacyMethodBodySyntax()
    {
        var name = UniqueName("test_legacy_rejected");
        var registerResult = _tools.CreateScript(
            name: name,
            description: "Legacy body should fail.",
            parameters: """[{"name":"x","type":"int","description":"value"}]""",
            body: "return x.ToString();",
            functionType: "code",
            outputInstructions: "");

        Assert.Contains("Compilation failed:", registerResult, StringComparison.Ordinal);
    }

    [Fact]
    public void StartupMigratesExistingLegacyCodeScriptToTopLevel()
    {
        var name = UniqueName("test_startup_migration");

        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_fixture.DatabasePath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO scripts (name, description, parameters, script_type, body, compiled_assembly, output_instructions, dependencies, code_format)
                VALUES (@name, @description, @parameters, 'code', @body, NULL, NULL, '', 'legacy_method_body')";
            cmd.Parameters.AddWithValue("@name", name);
            cmd.Parameters.AddWithValue("@description", "Legacy body awaiting migration.");
            cmd.Parameters.AddWithValue("@parameters", """[{"name":"x","type":"int","description":"value"}]""");
            cmd.Parameters.AddWithValue("@body", "return x.ToString();");
            cmd.ExecuteNonQuery();
        }

        ResetScriptToolsInitialization();
        var migratedTools = new ScriptTools();

        var callResult = migratedTools.CallScript(name, """{"x":7}""");
        Assert.Equal("7", callResult);

        var inspection = migratedTools.InspectScript(name, fullInspection: true);
        Assert.Contains("Code Format: top_level", inspection, StringComparison.Ordinal);
        Assert.Contains("Console.Write(__scriptmcpResult);", inspection, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadScriptCreatesNewScriptFromFile()
    {
        var name = UniqueName("test_load_create");
        var sourcePath = Path.Combine(_fixture.TestDataDirectory, $"{name}.cs");
        File.WriteAllText(sourcePath, "Console.Write(\"loaded-create\");");

        var result = _tools.LoadScript(sourcePath, name: name);
        Assert.Contains("loaded from", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("created", result, StringComparison.OrdinalIgnoreCase);

        var callResult = _tools.CallScript(name, "{}");
        Assert.Equal("loaded-create", callResult);
    }

    [Fact]
    public void LoadScriptUpdatesExistingScriptFromFileAndPreservesMetadata()
    {
        var name = UniqueName("test_load_update");
        var sourcePath = Path.Combine(_fixture.TestDataDirectory, $"{name}.cs");

        Assert.Contains("created successfully", _tools.CreateScript(
            name: name,
            description: "Original description",
            parameters: """[{"name":"city","type":"string","description":"City name"}]""",
            body: "Console.Write(city);",
            functionType: "code",
            outputInstructions: "return exactly"), StringComparison.OrdinalIgnoreCase);

        File.WriteAllText(sourcePath, "Console.Write(city.ToUpperInvariant());");

        var result = _tools.LoadScript(sourcePath, name: name);
        Assert.Contains("loaded from", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("updated", result, StringComparison.OrdinalIgnoreCase);

        var callResult = _tools.CallScript(name, """{"city":"athens"}""");
        Assert.Contains("ATHENS", callResult, StringComparison.Ordinal);

        var inspection = _tools.InspectScript(name);
        Assert.Contains("Original description", inspection, StringComparison.Ordinal);
        Assert.Contains("city (string): City name", inspection, StringComparison.Ordinal);
        Assert.Contains("Output Instructions: return exactly", inspection, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportScriptWritesCodeScriptToCsFile()
    {
        var name = UniqueName("test_export_code");
        var exportPath = Path.Combine(_fixture.TestDataDirectory, $"{name}.cs");

        Assert.Contains("created successfully", _tools.CreateScript(
            name: name,
            description: "Export me.",
            parameters: "[]",
            body: "Console.Write(\"exported\");",
            functionType: "code",
            outputInstructions: ""), StringComparison.OrdinalIgnoreCase);

        var result = _tools.ExportScript(name, exportPath);
        Assert.Contains("exported to", result, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(exportPath));
        Assert.Equal("Console.Write(\"exported\");", File.ReadAllText(exportPath));
    }

    [Fact]
    public void ExportScriptUsesDefaultExtensionForInstructionsScript()
    {
        var name = UniqueName("test_export_instructions");
        var originalDirectory = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_fixture.TestDataDirectory);

        try
        {
            Assert.Contains("created successfully", _tools.CreateScript(
                name: name,
                description: "Instruction export.",
                parameters: "[]",
                body: "Do the thing.",
                functionType: "instructions",
                outputInstructions: ""), StringComparison.OrdinalIgnoreCase);

            var result = _tools.ExportScript(name);
            var exportPath = Path.Combine(_fixture.TestDataDirectory, $"{name}.txt");

            Assert.Contains("exported to", result, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(exportPath));
            Assert.Equal("Do the thing.", File.ReadAllText(exportPath));
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDirectory);
        }
    }

    [Fact]
    public void CompileScriptExportsAssemblyToDllFile()
    {
        var name = UniqueName("test_compile_export");
        var dllPath = Path.Combine(_fixture.TestDataDirectory, $"{name}.dll");

        Assert.Contains("created successfully", _tools.CreateScript(
            name: name,
            description: "Compile me.",
            parameters: "[]",
            body: "Console.Write(\"compiled\");",
            functionType: "code",
            outputInstructions: ""), StringComparison.OrdinalIgnoreCase);

        var result = _tools.CompileScript(name, dllPath);
        Assert.Contains("compiled and exported to", result, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(dllPath));
        Assert.True(new FileInfo(dllPath).Length > 0);
    }

    [Fact]
    public void CompileScriptRejectsInstructionsScript()
    {
        var name = UniqueName("test_compile_instructions");

        Assert.Contains("created successfully", _tools.CreateScript(
            name: name,
            description: "Instructions only.",
            parameters: "[]",
            body: "Follow the process.",
            functionType: "instructions",
            outputInstructions: ""), StringComparison.OrdinalIgnoreCase);

        var result = _tools.CompileScript(name);
        Assert.Contains("is an instructions script", result, StringComparison.Ordinal);
    }

    [Fact]
    public void RegisterRejectsDirectCircularDependency()
    {
        var first = UniqueName("test_cycle_first");
        var second = UniqueName("test_cycle_second");

        var firstRegister = _tools.CreateScript(
            name: first,
            description: "Calls the second function.",
            parameters: "[]",
            body: $$"""Console.Write(ScriptMCP.Call("{{second}}"));""",
            functionType: "code",
            outputInstructions: "");
        Assert.Contains("created successfully", firstRegister, StringComparison.OrdinalIgnoreCase);

        var secondRegister = _tools.CreateScript(
            name: second,
            description: "Calls the first function.",
            parameters: "[]",
            body: $$"""Console.Write(ScriptMCP.Call("{{first}}"));""",
            functionType: "code",
            outputInstructions: "");

        Assert.Contains("Creation failed: direct circular dependency detected", secondRegister, StringComparison.Ordinal);
        Assert.Contains($"{second} <-> {first}", secondRegister, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateBodyRejectsDirectCircularDependency()
    {
        var first = UniqueName("test_cycle_update_first");
        var second = UniqueName("test_cycle_update_second");

        var firstRegister = _tools.CreateScript(
            name: first,
            description: "Calls the second function.",
            parameters: "[]",
            body: $$"""Console.Write(ScriptMCP.Call("{{second}}"));""",
            functionType: "code",
            outputInstructions: "");
        Assert.Contains("created successfully", firstRegister, StringComparison.OrdinalIgnoreCase);

        var secondRegister = _tools.CreateScript(
            name: second,
            description: "Does not call anything.",
            parameters: "[]",
            body: """Console.Write("ok");""",
            functionType: "code",
            outputInstructions: "");
        Assert.Contains("created successfully", secondRegister, StringComparison.OrdinalIgnoreCase);

        var update = _tools.UpdateScript(
            second,
            "body",
            $$"""Console.Write(ScriptMCP.Call("{{first}}"));""");

        Assert.Contains("Update failed: direct circular dependency detected", update, StringComparison.Ordinal);
        Assert.Contains($"{second} <-> {first}", update, StringComparison.Ordinal);

        var inspect = _tools.InspectScript(second);
        Assert.Contains("Depends on:  (none)", inspect, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateWithInvalidFieldReturnsError()
    {
        var name = UniqueName("test_update_error");
        _tools.CreateScript(
            name: name,
            description: "No-op",
            parameters: "[]",
            body: "Console.Write(\"ok\");",
            functionType: "code",
            outputInstructions: "");

        var result = _tools.UpdateScript(name, "not_a_field", "x");
        Assert.Contains("Update failed:", result, StringComparison.Ordinal);
    }

    [Fact]
    public void CallScriptAppendsOutputInstructionsSuffix()
    {
        var name = UniqueName("test_output_instructions");
        _tools.CreateScript(
            name: name,
            description: "Has output instructions.",
            parameters: "[]",
            body: "Console.Write(\"payload\");",
            functionType: "code",
            outputInstructions: "render as markdown table");

        var output = _tools.CallScript(name, "{}");
        Assert.Contains("payload", output, StringComparison.Ordinal);
        Assert.Contains("[Output Instructions]: render as markdown table", output, StringComparison.Ordinal);
    }

    [Fact]
    public void CallProcessRejectsUnknownOutputMode()
    {
        var result = _tools.CallProcess("anything", "{}", "BogusMode");
        Assert.Equal("Error: invalid output_mode. Supported values: Default, WriteNew, WriteAppend.", result);
    }

    [Fact]
    public void ReadScheduledTaskReturnsMostRecentOutputPreferringAppendWhenNewer()
    {
        ResetOutputDirectory();
        var name = UniqueName("test_read_scheduled");

        var timestampPath = ScriptTools.GetScheduledTaskOutputPath(name, new DateTime(2026, 01, 01, 0, 0, 0, DateTimeKind.Utc));
        File.WriteAllText(timestampPath, "timestamp-result");
        File.SetLastWriteTimeUtc(timestampPath, new DateTime(2026, 01, 01, 0, 0, 0, DateTimeKind.Utc));

        var appendPath = ScriptTools.GetScheduledTaskAppendOutputPath(name);
        File.WriteAllText(appendPath, "append-result");
        File.SetLastWriteTimeUtc(appendPath, new DateTime(2026, 01, 02, 0, 0, 0, DateTimeKind.Utc));

        var result = _tools.ReadScheduledTask(name);
        Assert.Equal("append-result", result);
    }

    [Fact]
    public void ReadScheduledTaskReturnsNotFoundMessageWhenNoFilesExist()
    {
        ResetOutputDirectory();
        var name = UniqueName("test_read_missing");

        var result = _tools.ReadScheduledTask(name);
        Assert.Equal("(empty)", result);
    }

    [Fact]
    public void GetDatabaseReturnsCurrentSavePath()
    {
        Assert.Equal(ScriptTools.SavePath, _tools.GetDatabase());
    }

    [Fact]
    public void SetDatabaseRejectsCreatingMissingDatabaseWithoutConfirmation()
    {
        var originalPath = ScriptTools.SavePath;
        var databaseName = UniqueName("missing-db");
        var expectedPath = GetDefaultDatabasePath(databaseName);

        try
        {
            if (File.Exists(expectedPath))
                File.Delete(expectedPath);

            var result = _tools.SetDatabase(databaseName);

            Assert.Contains("Database does not exist:", result, StringComparison.Ordinal);
            Assert.Contains(expectedPath, result, StringComparison.Ordinal);
            Assert.Equal(originalPath, ScriptTools.SavePath);
            Assert.False(File.Exists(expectedPath));
        }
        finally
        {
            RestoreAndDeleteDatabase(originalPath, expectedPath);
        }
    }

    [Fact]
    public void SetDatabaseCreatesAndSwitchesToNamedDatabaseWhenConfirmed()
    {
        var originalPath = ScriptTools.SavePath;
        var databaseName = UniqueName("created-db");
        var expectedPath = GetDefaultDatabasePath(databaseName);

        try
        {
            if (File.Exists(expectedPath))
                File.Delete(expectedPath);

            var result = _tools.SetDatabase(databaseName, create: true);

            Assert.Contains("Switched database from:", result, StringComparison.Ordinal);
            Assert.Contains(expectedPath, result, StringComparison.Ordinal);
            Assert.Equal(expectedPath, ScriptTools.SavePath);
            Assert.True(File.Exists(expectedPath));
            Assert.Equal(expectedPath, _tools.GetDatabase());
        }
        finally
        {
            RestoreAndDeleteDatabase(originalPath, expectedPath);
        }
    }

    [Fact]
    public void DeleteDatabaseRequiresConfirmationBeforeDeleting()
    {
        var originalPath = ScriptTools.SavePath;
        var databaseName = UniqueName("delete-db");
        var databasePath = GetDefaultDatabasePath(databaseName);

        try
        {
            if (File.Exists(databasePath))
                File.Delete(databasePath);

            Assert.Contains("Switched database from:", _tools.SetDatabase(databaseName, create: true), StringComparison.Ordinal);
            Assert.True(File.Exists(databasePath));

            var result = _tools.DeleteDatabase(databaseName);

            Assert.Contains("Delete this database?", result, StringComparison.Ordinal);
            Assert.Contains(databasePath, result, StringComparison.Ordinal);
            Assert.Contains("Say yes or no.", result, StringComparison.Ordinal);
            Assert.True(File.Exists(databasePath));
            Assert.Equal(databasePath, ScriptTools.SavePath);
        }
        finally
        {
            RestoreAndDeleteDatabase(originalPath, databasePath);
        }
    }

    [Fact]
    public void DeleteDatabaseDeletesActiveDatabaseAndSwitchesToDefault()
    {
        var originalPath = ScriptTools.SavePath;
        var databaseName = UniqueName("active-delete-db");
        var databasePath = GetDefaultDatabasePath(databaseName);
        var defaultPath = GetDefaultDatabasePath(McpConstants.DefaultDatabaseFileName);

        try
        {
            if (File.Exists(databasePath))
                File.Delete(databasePath);

            Assert.Contains("Switched database from:", _tools.SetDatabase(databaseName, create: true), StringComparison.Ordinal);
            Assert.Equal(databasePath, ScriptTools.SavePath);

            var result = _tools.DeleteDatabase(databaseName, confirm: true);

            Assert.Contains($"Deleted database: {databasePath}", result, StringComparison.Ordinal);
            Assert.Contains($"Active database: {defaultPath}", result, StringComparison.Ordinal);
            Assert.False(File.Exists(databasePath));
            Assert.Equal(defaultPath, ScriptTools.SavePath);
            Assert.True(File.Exists(defaultPath));
        }
        finally
        {
            RestoreAndDeleteDatabase(originalPath, databasePath);
        }
    }

    [Fact]
    public void DeleteDatabaseRejectsDeletingDefaultDatabase()
    {
        var defaultPath = GetDefaultDatabasePath(McpConstants.DefaultDatabaseFileName);
        var originalPath = ScriptTools.SavePath;

        var result = _tools.DeleteDatabase(defaultPath);

        Assert.Equal("Error: the default database cannot be deleted.", result);
        Assert.Equal(originalPath, ScriptTools.SavePath);
    }

    [Fact]
    public void DeleteDatabaseChecksExistenceBeforePromptingForConfirmation()
    {
        var originalPath = ScriptTools.SavePath;
        var databaseName = UniqueName("missing-delete-db");
        var databasePath = GetDefaultDatabasePath(databaseName);

        try
        {
            if (File.Exists(databasePath))
                File.Delete(databasePath);

            var result = _tools.DeleteDatabase(databaseName);

            Assert.Equal($"Error: database not found: {databasePath}", result);
            Assert.DoesNotContain("Say yes or no.", result, StringComparison.Ordinal);
            Assert.Equal(originalPath, ScriptTools.SavePath);
        }
        finally
        {
            RestoreAndDeleteDatabase(originalPath, databasePath);
        }
    }

    private static string UniqueName(string prefix) => $"{prefix}_{Guid.NewGuid():N}";

    private static string GetDefaultDatabasePath(string pathOrName)
    {
        var trimmed = pathOrName.Trim();
        if (!trimmed.Contains(Path.DirectorySeparatorChar) &&
            !trimmed.Contains(Path.AltDirectorySeparatorChar))
        {
            if (!trimmed.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
                trimmed += ".db";
            return Path.GetFullPath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ScriptMCP",
                trimmed));
        }

        return Path.GetFullPath(trimmed);
    }

    private static void ResetScriptToolsInitialization()
    {
        var initializedField = typeof(ScriptTools).GetField("_initialized", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        initializedField!.SetValue(null, false);
    }

    private void RestoreAndDeleteDatabase(string originalPath, string databasePath)
    {
        if (!string.Equals(ScriptTools.SavePath, originalPath, StringComparison.OrdinalIgnoreCase))
            _tools.SetDatabase(originalPath);

        ScriptTools.SavePath = originalPath;

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        if (!File.Exists(databasePath))
            return;

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                File.Delete(databasePath);
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(50);
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            }
        }
    }

    private void ResetOutputDirectory()
    {
        if (Directory.Exists(_fixture.OutputDirectory))
            Directory.Delete(_fixture.OutputDirectory, recursive: true);
    }
}
