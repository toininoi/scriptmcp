using System.Text;
using System.Text.Json;
using ScriptMCP.Library;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

McpConstants.ResolveSavePath();
Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

const int MaxOutputFileSize = 1_048_576; // 1 MB cap

// ── Helper: strip [Output Instructions] suffix ──────────────────────────────
static string StripOutputInstructions(string output)
{
    var idx = output.IndexOf("[Output Instructions]:", StringComparison.Ordinal);
    return idx >= 0 ? output[..idx].TrimEnd() : output;
}

// ── Helper: append a result line to the shared output JSONL file ────────────
static void WriteToOutputFile(string functionName, string result)
{
    var outputPath = Path.Combine(
        Path.GetDirectoryName(DynamicTools.SavePath) ?? ".",
        "exec_output.jsonl");

    var jsonLine = JsonSerializer.Serialize(new
    {
        func = functionName,
        ts = DateTime.UtcNow.ToString("o"),
        @out = result
    }) + "\n";

    // If file exceeds cap, keep only the last half of lines
    if (File.Exists(outputPath))
    {
        var fileInfo = new FileInfo(outputPath);
        if (fileInfo.Length + jsonLine.Length > MaxOutputFileSize)
        {
            var lines = File.ReadAllLines(outputPath);
            var keep = lines.Skip(lines.Length / 2).ToArray();
            File.WriteAllLines(outputPath, keep);
        }
    }

    File.AppendAllText(outputPath, jsonLine);
}

// ── CLI mode: --exec <functionName> [argsJson] ──────────────────────────────
// Executes a single dynamic function and exits without starting the MCP server.
var execIndex = Array.IndexOf(args, "--exec");
var execOutIndex = Array.IndexOf(args, "--exec_out");

if (execOutIndex >= 0 && execOutIndex + 1 < args.Length)
{
    // --exec_out: execute function, write to stdout AND shared memory
    var functionName = args[execOutIndex + 1];
    var argsJson = (execOutIndex + 2 < args.Length) ? args[execOutIndex + 2] : "{}";

    try
    {
        var tools = new DynamicTools();
        var result = tools.CallDynamicFunction(functionName, argsJson);
        Console.Write(result);

        var cleanResult = StripOutputInstructions(result);
        WriteToOutputFile(functionName, cleanResult);
    }
    catch (Exception ex)
    {
        Console.Error.Write(ex.ToString());
        Environment.ExitCode = 1;
    }
    return;
}

if (execIndex >= 0 && execIndex + 1 < args.Length)
{
    var functionName = args[execIndex + 1];
    var argsJson = (execIndex + 2 < args.Length) ? args[execIndex + 2] : "{}";

    try
    {
        var tools = new DynamicTools();
        var result = tools.CallDynamicFunction(functionName, argsJson);
        Console.Write(result);
    }
    catch (Exception ex)
    {
        Console.Error.Write(ex.ToString());
        Environment.ExitCode = 1;
    }
    return;
}

// ── MCP server mode (default) ────────────────────────────────────────────────

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Services.AddSingleton<DynamicTools>();
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new() { Name = "scriptmcp-console", Version = "1.0.0" };
        options.ServerInstructions = McpConstants.Instructions;
    })
    .WithStdioServerTransport()
    .WithTools<DynamicTools>()
    .WithResources<DynamicResources>();

await builder.Build().RunAsync();
