using System.Diagnostics;
using ScriptMCP.Library;

namespace ScriptMCP.Tests;

[Collection("ScriptTools tests")]
public sealed class CliOutputModeTests
{
    private readonly TestDatabaseFixture _fixture;

    public CliOutputModeTests(TestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void ExecOutCreatesTimestampedOutputFile()
    {
        var tools = new ScriptTools();
        var name = UniqueName("test_cli_write_new");
        tools.CreateScript(
            name: name,
            description: "CLI write new",
            parameters: "[]",
            body: "Console.Write(\"new-file-output\");",
            functionType: "code",
            outputInstructions: "");

        ResetFunctionOutputFiles(name);

        RunConsole("--exec-out", name, "{}");

        var outputDir = ScriptTools.GetScheduledTaskOutputDirectory();
        var prefix = ScriptTools.GetScheduledTaskFilePrefix(name);
        var files = Directory.EnumerateFiles(outputDir, $"{prefix}_*.txt").ToList();
        Assert.Single(files);

        var text = File.ReadAllText(files[0]).Trim();
        Assert.Equal("new-file-output", text);
    }

    [Fact]
    public void ExecOutAppendAppendsToStableOutputFile()
    {
        var tools = new ScriptTools();
        var name = UniqueName("test_cli_write_append");
        tools.CreateScript(
            name: name,
            description: "CLI append",
            parameters: "[]",
            body: "Console.Write(\"append-output\");",
            functionType: "code",
            outputInstructions: "");

        ResetFunctionOutputFiles(name);

        RunConsole("--exec-out-append", name, "{}");
        RunConsole("--exec-out-append", name, "{}");

        var appendPath = ScriptTools.GetScheduledTaskAppendOutputPath(name);
        Assert.True(File.Exists(appendPath));

        var lines = File.ReadAllLines(appendPath)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();
        Assert.Equal(2, lines.Count);
        Assert.All(lines, line => Assert.Equal("append-output", line.Trim()));
    }

    [Fact]
    public void ExecWithDbArgumentPropagatesToScriptMcpCallSubprocess()
    {
        var tools = new ScriptTools();
        var innerName = UniqueName("test_cli_inner");
        var outerName = UniqueName("test_cli_outer");

        tools.CreateScript(
            name: innerName,
            description: "Inner function",
            parameters: "[]",
            body: "Console.Write(\"inner-ok\");",
            functionType: "code",
            outputInstructions: "");

        tools.CreateScript(
            name: outerName,
            description: "Outer function",
            parameters: "[]",
            body: $"Console.Write(ScriptMCP.Call(\"{innerName}\", \"{{}}\").Trim());",
            functionType: "code",
            outputInstructions: "");

        var stdout = RunConsole("--exec", outerName, "{}");
        Assert.Equal("inner-ok", stdout.Trim());
    }

    private string RunConsole(string mode, string functionName, string argsJson)
    {
        var exePath = FindConsoleExecutablePath();
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = Path.GetDirectoryName(exePath)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--db");
        psi.ArgumentList.Add(_fixture.DatabasePath);
        psi.ArgumentList.Add(mode);
        psi.ArgumentList.Add(functionName);
        psi.ArgumentList.Add(argsJson);

        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        var exited = proc.WaitForExit(120_000);

        Assert.True(exited, "ScriptMCP.Console process timed out.");
        Assert.True(proc.ExitCode == 0, $"Console command failed.\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
        return stdout;
    }

    private void ResetFunctionOutputFiles(string functionName)
    {
        var outputDir = ScriptTools.GetScheduledTaskOutputDirectory();
        if (!Directory.Exists(outputDir))
            return;

        var prefix = ScriptTools.GetScheduledTaskFilePrefix(functionName);
        foreach (var path in Directory.EnumerateFiles(outputDir, $"{prefix}*.txt"))
            File.Delete(path);
    }

    private static string FindConsoleExecutablePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "ScriptMCP.Console", "bin", "Debug", "net9.0", "scriptmcp.exe");
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate ScriptMCP.Console executable at bin/Debug/net9.0/scriptmcp.exe.");
    }

    private static string UniqueName(string prefix) => $"{prefix}_{Guid.NewGuid():N}";
}
