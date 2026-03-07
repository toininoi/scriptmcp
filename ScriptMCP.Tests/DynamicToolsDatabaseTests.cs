using ScriptMCP.Library;

namespace ScriptMCP.Tests;

[Collection("DynamicTools tests")]
public sealed class DynamicToolsDatabaseTests
{
    private readonly TestDatabaseFixture _fixture;
    private readonly DynamicTools _tools;

    public DynamicToolsDatabaseTests(TestDatabaseFixture fixture)
    {
        _fixture = fixture;
        _tools = new DynamicTools();
    }

    [Fact]
    public void RegistersAndExecutesFunctionUsingDedicatedTestDatabaseFile()
    {
        var name = UniqueName("test_add_two_numbers");
        var registerResult = _tools.RegisterDynamicFunction(
            name: name,
            description: "Adds two integers.",
            parameters: """[{"name":"x","type":"int","description":"first"},{"name":"y","type":"int","description":"second"}]""",
            body: "return (x + y).ToString();",
            functionType: "code",
            outputInstructions: "");

        Assert.Contains("registered successfully", registerResult, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(_fixture.DatabasePath));

        var listResult = _tools.ListDynamicFunctions();
        Assert.Contains(name, listResult, StringComparison.Ordinal);

        var callResult = _tools.CallDynamicFunction(name, """{"x":2,"y":3}""");
        Assert.Equal("5", callResult);
    }

    [Fact]
    public void InspectSupportsBasicAndFullInspectionModes()
    {
        var name = UniqueName("test_inspect");
        _tools.RegisterDynamicFunction(
            name: name,
            description: "Inspect me.",
            parameters: """[{"name":"x","type":"int","description":"value"}]""",
            body: "return x.ToString();",
            functionType: "code",
            outputInstructions: "return exactly");

        var basic = _tools.InspectDynamicFunction(name);
        Assert.Contains($"Function: {name}", basic, StringComparison.Ordinal);
        Assert.DoesNotContain("Compiled:", basic, StringComparison.Ordinal);
        Assert.DoesNotContain("Source (C# Code):", basic, StringComparison.Ordinal);
        Assert.Contains("Output Instructions: return exactly", basic, StringComparison.Ordinal);

        var full = _tools.InspectDynamicFunction(name, fullInspection: true);
        Assert.Contains("Compiled:    Yes", full, StringComparison.Ordinal);
        Assert.Contains("Source (C# Code):", full, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateBodyRecompilesAndChangesBehavior()
    {
        var name = UniqueName("test_update");
        _tools.RegisterDynamicFunction(
            name: name,
            description: "Math function.",
            parameters: """[{"name":"x","type":"int","description":"first"},{"name":"y","type":"int","description":"second"}]""",
            body: "return (x + y).ToString();",
            functionType: "code",
            outputInstructions: "");

        var before = _tools.CallDynamicFunction(name, """{"x":2,"y":3}""");
        Assert.Equal("5", before);

        var update = _tools.UpdateDynamicFunction(name, "body", "return (x * y).ToString();");
        Assert.Contains("updated successfully: body", update, StringComparison.OrdinalIgnoreCase);

        var after = _tools.CallDynamicFunction(name, """{"x":2,"y":3}""");
        Assert.Equal("6", after);
    }

    [Fact]
    public void RegisterRejectsDirectCircularDependency()
    {
        var first = UniqueName("test_cycle_first");
        var second = UniqueName("test_cycle_second");

        var firstRegister = _tools.RegisterDynamicFunction(
            name: first,
            description: "Calls the second function.",
            parameters: "[]",
            body: $$"""return ScriptMCP.Call("{{second}}");""",
            functionType: "code",
            outputInstructions: "");
        Assert.Contains("registered successfully", firstRegister, StringComparison.OrdinalIgnoreCase);

        var secondRegister = _tools.RegisterDynamicFunction(
            name: second,
            description: "Calls the first function.",
            parameters: "[]",
            body: $$"""return ScriptMCP.Call("{{first}}");""",
            functionType: "code",
            outputInstructions: "");

        Assert.Contains("Registration failed: direct circular dependency detected", secondRegister, StringComparison.Ordinal);
        Assert.Contains($"{second} <-> {first}", secondRegister, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateBodyRejectsDirectCircularDependency()
    {
        var first = UniqueName("test_cycle_update_first");
        var second = UniqueName("test_cycle_update_second");

        var firstRegister = _tools.RegisterDynamicFunction(
            name: first,
            description: "Calls the second function.",
            parameters: "[]",
            body: $$"""return ScriptMCP.Call("{{second}}");""",
            functionType: "code",
            outputInstructions: "");
        Assert.Contains("registered successfully", firstRegister, StringComparison.OrdinalIgnoreCase);

        var secondRegister = _tools.RegisterDynamicFunction(
            name: second,
            description: "Does not call anything.",
            parameters: "[]",
            body: """return "ok";""",
            functionType: "code",
            outputInstructions: "");
        Assert.Contains("registered successfully", secondRegister, StringComparison.OrdinalIgnoreCase);

        var update = _tools.UpdateDynamicFunction(
            second,
            "body",
            $$"""return ScriptMCP.Call("{{first}}");""");

        Assert.Contains("Update failed: direct circular dependency detected", update, StringComparison.Ordinal);
        Assert.Contains($"{second} <-> {first}", update, StringComparison.Ordinal);

        var inspect = _tools.InspectDynamicFunction(second);
        Assert.Contains("Depends on:  (none)", inspect, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateWithInvalidFieldReturnsError()
    {
        var name = UniqueName("test_update_error");
        _tools.RegisterDynamicFunction(
            name: name,
            description: "No-op",
            parameters: "[]",
            body: "return \"ok\";",
            functionType: "code",
            outputInstructions: "");

        var result = _tools.UpdateDynamicFunction(name, "not_a_field", "x");
        Assert.Contains("Update failed:", result, StringComparison.Ordinal);
    }

    [Fact]
    public void CallDynamicFunctionAppendsOutputInstructionsSuffix()
    {
        var name = UniqueName("test_output_instructions");
        _tools.RegisterDynamicFunction(
            name: name,
            description: "Has output instructions.",
            parameters: "[]",
            body: "return \"payload\";",
            functionType: "code",
            outputInstructions: "render as markdown table");

        var output = _tools.CallDynamicFunction(name, "{}");
        Assert.Contains("payload", output, StringComparison.Ordinal);
        Assert.Contains("[Output Instructions]: render as markdown table", output, StringComparison.Ordinal);
    }

    [Fact]
    public void CallDynamicProcessRejectsUnknownOutputMode()
    {
        var result = _tools.CallDynamicProcess("anything", "{}", "BogusMode");
        Assert.Equal("Error: invalid output_mode. Supported values: Default, WriteNew, WriteAppend.", result);
    }

    [Fact]
    public void ReadScheduledTaskReturnsMostRecentOutputPreferringAppendWhenNewer()
    {
        ResetOutputDirectory();
        var name = UniqueName("test_read_scheduled");

        var timestampPath = DynamicTools.GetScheduledTaskOutputPath(name, new DateTime(2026, 01, 01, 0, 0, 0, DateTimeKind.Utc));
        File.WriteAllText(timestampPath, "timestamp-result");
        File.SetLastWriteTimeUtc(timestampPath, new DateTime(2026, 01, 01, 0, 0, 0, DateTimeKind.Utc));

        var appendPath = DynamicTools.GetScheduledTaskAppendOutputPath(name);
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
        Assert.Equal(DynamicTools.SavePath, _tools.GetDatabase());
    }

    [Fact]
    public void SetDatabaseRejectsCreatingMissingDatabaseWithoutConfirmation()
    {
        var originalPath = DynamicTools.SavePath;
        var databaseName = UniqueName("missing-db");
        var expectedPath = GetDefaultDatabasePath(databaseName);

        try
        {
            if (File.Exists(expectedPath))
                File.Delete(expectedPath);

            var result = _tools.SetDatabase(databaseName);

            Assert.Contains("Database does not exist:", result, StringComparison.Ordinal);
            Assert.Contains(expectedPath, result, StringComparison.Ordinal);
            Assert.Equal(originalPath, DynamicTools.SavePath);
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
        var originalPath = DynamicTools.SavePath;
        var databaseName = UniqueName("created-db");
        var expectedPath = GetDefaultDatabasePath(databaseName);

        try
        {
            if (File.Exists(expectedPath))
                File.Delete(expectedPath);

            var result = _tools.SetDatabase(databaseName, create: true);

            Assert.Contains("Switched database from:", result, StringComparison.Ordinal);
            Assert.Contains(expectedPath, result, StringComparison.Ordinal);
            Assert.Equal(expectedPath, DynamicTools.SavePath);
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
        var originalPath = DynamicTools.SavePath;
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
            Assert.Equal(databasePath, DynamicTools.SavePath);
        }
        finally
        {
            RestoreAndDeleteDatabase(originalPath, databasePath);
        }
    }

    [Fact]
    public void DeleteDatabaseDeletesActiveDatabaseAndSwitchesToDefault()
    {
        var originalPath = DynamicTools.SavePath;
        var databaseName = UniqueName("active-delete-db");
        var databasePath = GetDefaultDatabasePath(databaseName);
        var defaultPath = GetDefaultDatabasePath(McpConstants.DefaultDatabaseFileName);

        try
        {
            if (File.Exists(databasePath))
                File.Delete(databasePath);

            Assert.Contains("Switched database from:", _tools.SetDatabase(databaseName, create: true), StringComparison.Ordinal);
            Assert.Equal(databasePath, DynamicTools.SavePath);

            var result = _tools.DeleteDatabase(databaseName, confirm: true);

            Assert.Contains($"Deleted database: {databasePath}", result, StringComparison.Ordinal);
            Assert.Contains($"Active database: {defaultPath}", result, StringComparison.Ordinal);
            Assert.False(File.Exists(databasePath));
            Assert.Equal(defaultPath, DynamicTools.SavePath);
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
        var originalPath = DynamicTools.SavePath;

        var result = _tools.DeleteDatabase(defaultPath);

        Assert.Equal("Error: the default database cannot be deleted.", result);
        Assert.Equal(originalPath, DynamicTools.SavePath);
    }

    [Fact]
    public void DeleteDatabaseChecksExistenceBeforePromptingForConfirmation()
    {
        var originalPath = DynamicTools.SavePath;
        var databaseName = UniqueName("missing-delete-db");
        var databasePath = GetDefaultDatabasePath(databaseName);

        try
        {
            if (File.Exists(databasePath))
                File.Delete(databasePath);

            var result = _tools.DeleteDatabase(databaseName);

            Assert.Equal($"Error: database not found: {databasePath}", result);
            Assert.DoesNotContain("Say yes or no.", result, StringComparison.Ordinal);
            Assert.Equal(originalPath, DynamicTools.SavePath);
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

    private void RestoreAndDeleteDatabase(string originalPath, string databasePath)
    {
        if (!string.Equals(DynamicTools.SavePath, originalPath, StringComparison.OrdinalIgnoreCase))
            _tools.SetDatabase(originalPath);

        DynamicTools.SavePath = originalPath;

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
