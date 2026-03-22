using System.Text;
using ScriptMCP.Library;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

var supportedOptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "--db",
    "--exec",
    "--exec-out",
    "--exec-out-append"
};

for (int i = 0; i < args.Length; i++)
{
    var arg = args[i];
    if (!arg.StartsWith("--", StringComparison.Ordinal))
        continue;

    var optionName = arg;
    if (arg.StartsWith("--db=", StringComparison.OrdinalIgnoreCase))
        optionName = "--db";
    else if (!supportedOptions.Contains(arg))
    {
        Console.Error.WriteLine($"Error: unsupported argument '{arg}'. Supported arguments: --db, --exec, --exec-out, --exec-out-append.");
        Environment.ExitCode = 1;
        return;
    }

    if (string.Equals(optionName, "--db", StringComparison.OrdinalIgnoreCase) &&
        !arg.StartsWith("--db=", StringComparison.OrdinalIgnoreCase))
    {
        if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
        {
            Console.Error.WriteLine("Error: --db requires a path value.");
            Environment.ExitCode = 1;
            return;
        }

        i++;
    }
}

McpConstants.ResolveSavePath(args);
Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
var fileEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

// ── Helper: strip [Output Instructions] suffix ──────────────────────────────
static string StripOutputInstructions(string output)
{
    var idx = output.IndexOf("[Output Instructions]:", StringComparison.Ordinal);
    return idx >= 0 ? output[..idx].TrimEnd() : output;
}

// ── Helper: write scheduled-task output to either a new file or an append file
void WriteScheduledTaskOutput(string functionName, string result, bool append)
{
    if (append)
    {
        var appendPath = ScriptTools.GetScheduledTaskAppendOutputPath(functionName);
        var text = result + Environment.NewLine;
        File.AppendAllText(appendPath, text, fileEncoding);
        return;
    }

    var timestampedPath = ScriptTools.GetScheduledTaskOutputPath(functionName);
    File.WriteAllText(timestampedPath, result, fileEncoding);
}

// ── CLI mode: --exec <functionName> [argsJson] ──────────────────────────────
// Executes a single script and exits without starting the MCP server.
var execIndex = Array.IndexOf(args, "--exec");
var execOutIndex = Array.IndexOf(args, "--exec-out");
var execOutAppendIndex = Array.IndexOf(args, "--exec-out-append");

if (execOutAppendIndex >= 0 && execOutAppendIndex + 1 < args.Length)
{
    // --exec-out-append: execute script, write to stdout and append output to <script>.txt
    var functionName = args[execOutAppendIndex + 1];
    var argsJson = (execOutAppendIndex + 2 < args.Length) ? args[execOutAppendIndex + 2] : "{}";

    try
    {
        var tools = new ScriptTools();
        var result = tools.CallScript(functionName, argsJson);
        Console.Write(result);

        var cleanResult = StripOutputInstructions(result);
        WriteScheduledTaskOutput(functionName, cleanResult, append: true);
    }
    catch (Exception ex)
    {
        Console.Error.Write(ex.ToString());
        Environment.ExitCode = 1;
    }
    return;
}

if (execOutIndex >= 0 && execOutIndex + 1 < args.Length)
{
    // --exec-out: execute script, write to stdout and persist scheduled-task output
    var functionName = args[execOutIndex + 1];
    var argsJson = (execOutIndex + 2 < args.Length) ? args[execOutIndex + 2] : "{}";

    try
    {
        var tools = new ScriptTools();
        var result = tools.CallScript(functionName, argsJson);
        Console.Write(result);

        var cleanResult = StripOutputInstructions(result);
        WriteScheduledTaskOutput(functionName, cleanResult, append: false);
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
        var tools = new ScriptTools();
        var result = tools.CallScript(functionName, argsJson);
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
builder.Services.AddSingleton<ScriptTools>();
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new() { Name = "scriptmcp-console", Version = "1.0.0" };
        options.ServerInstructions = McpConstants.Instructions;
    })
    .WithStdioServerTransport()
    .WithTools<ScriptTools>()
    .WithResources<ScriptResources>();

await builder.Build().RunAsync();
