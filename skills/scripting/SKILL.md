---
name: ScriptMCP Dynamic Scripting
description: This skill should be used when the user asks to "create a function", "register a function", "write a dynamic function", "make a script", "automate a task with ScriptMCP", "build a C# function", "create an instructions function", or needs guidance on using ScriptMCP tools for dynamic function creation, management, and execution. Provides best practices for writing robust dynamic functions.
version: 1.0.0
---

# ScriptMCP Dynamic Scripting

## Purpose

Provide guidance for creating, managing, and executing dynamic functions through the ScriptMCP MCP server. ScriptMCP enables creating C# compiled functions and plain-English instruction functions on the fly, persisted in SQLite for reuse across sessions.

## Tool Overview

ScriptMCP exposes these MCP tools:

| Tool | Purpose |
|------|---------|
| `list_dynamic_functions` | Discover all registered functions |
| `register_dynamic_function` | Create a new function |
| `update_dynamic_function` | Modify a single field on an existing function |
| `inspect_dynamic_function` | View metadata, parameters, and optionally source |
| `call_dynamic_function` | Execute a function in-process |
| `call_dynamic_process` | Execute a function out-of-process (isolated, parallel) |
| `compile_dynamic_function` | Recompile a code function |
| `delete_dynamic_function` | Remove a function |
| `read_shared_memory` | Read exec output entries from exec_output.jsonl |
| `create_scheduled_task` | Create a scheduled task (Windows Task Scheduler or cron) for a dynamic function |

## Function Types

### Code Functions (`functionType: "code"`)

Compiled C# method bodies targeting .NET 9 / C# 13. The code is placed inside:

```csharp
public static string Run(Dictionary<string, string> args)
```

Write only the method body — no class or method signature. Must return a string.

**Auto-included namespaces:** System, System.Collections.Generic, System.Globalization, System.IO, System.Linq, System.Net, System.Net.Http, System.Text, System.Text.RegularExpressions, System.Threading.Tasks.

**Available libraries:** All `System.*` assemblies from .NET 9 runtime. Use `System.Text.Json` for JSON, `System.Net.Http.HttpClient` for HTTP, `System.Diagnostics.Process` for shell commands. NuGet packages are NOT available.

**The method is NOT async** — use `.Result` or `.GetAwaiter().GetResult()` for async calls.

### Instructions Functions (`functionType: "instructions"`)

Plain English instructions with `{paramName}` placeholder substitution. When called, the instructions are returned for Claude to read and follow — the raw text is not shown to the user.

Use instructions functions for:
- Guided workflows and checklists
- Response formatting templates
- Multi-step procedures that combine multiple tools

## Writing Robust Code Functions

### Parameter Handling

Define parameters as a JSON array:

```json
[{"name": "url", "type": "string", "description": "The URL to fetch"}]
```

Supported types: `string` (default), `int`, `long`, `double`, `float`, `bool`. Parameters are auto-parsed and available as local variables.

### Error Handling

Wrap risky operations in try-catch and return meaningful error messages:

```csharp
try {
    var client = new HttpClient();
    var response = client.GetStringAsync(url).Result;
    return response;
} catch (Exception ex) {
    return $"Error: {ex.Message}";
}
```

### Common Patterns

**HTTP requests:**
```csharp
var client = new HttpClient();
client.DefaultRequestHeaders.Add("User-Agent", "ScriptMCP");
var json = client.GetStringAsync(url).Result;
return json;
```

**File operations:**
```csharp
var content = File.ReadAllText(path);
// process content
return result;
```

**JSON processing:**
```csharp
var doc = System.Text.Json.JsonDocument.Parse(jsonString);
var root = doc.RootElement;
// extract fields
return output;
```

**Process execution:**
```csharp
var psi = new System.Diagnostics.ProcessStartInfo("cmd", "/c dir") {
    RedirectStandardOutput = true,
    UseShellExecute = false
};
var proc = System.Diagnostics.Process.Start(psi);
var output = proc.StandardOutput.ReadToEnd();
proc.WaitForExit();
return output;
```

### Inter-Function Calls

Code functions can invoke other dynamic functions:

- `ScriptMCP.Call(name, argsJson)` — synchronous, returns output string
- `ScriptMCP.Proc(name, argsJson)` — launches subprocess, returns `Process` for parallel work

## Handling Function Output

**This is critical.** After calling `call_dynamic_function` or `call_dynamic_process`, respect the function's output exactly.

### Functions WITHOUT Output Instructions

Return the function output verbatim. Do NOT:
- Add commentary, labels, or explanations around it
- Summarize, paraphrase, or reword it
- Wrap it in code blocks or markdown formatting
- Prefix it with "Here's the result:" or similar
- Remove, truncate, or reorder any part of it

The function author designed the output for a reason. Deliver it as-is.

### Functions WITH Output Instructions

Some function results include a trailing `[Output Instructions]: ...` section. When present:

1. **Read the instructions carefully** — they specify exactly how to present the output
2. **Follow them precisely** — if they say "render as a table", render as a table; if they say "return exactly", return exactly
3. **Never show the `[Output Instructions]` line itself** — strip it from what the user sees
4. **Apply instructions only to the output above the marker** — the instructions describe how to format/present the content, not additional content to add

If output instructions say to return the output exactly, return it with zero modifications — no wrapping, no commentary, no formatting changes.

### Output Instructions on Registration

Attach `outputInstructions` when registering a function to control presentation:

- `"present as a markdown table"` — formats tabular data
- `"summarize in 3 bullet points"` — condenses output
- `"return exactly as-is"` — preserves raw output

## Scheduling & Shared Output

### Scheduled Tasks

Use `create_scheduled_task` to run a dynamic function on a recurring schedule:

- **function_name** (required): The dynamic function to run
- **function_args** (default `"{}"`): JSON arguments for the function
- **interval_minutes** (required): How often to run, in minutes

On **Windows**, uses Task Scheduler (`schtasks`) with a hidden PowerShell window so no console window flashes.
On **Linux/macOS**, uses cron. Each entry is tagged with `# ScriptMCP:<function_name>` for easy identification and removal.

The task uses `--exec_out` mode, which runs the function and appends the result as a JSONL line to `exec_output.jsonl` in the ScriptMCP data directory.

After creation, the task is immediately run once. The tool returns the task name and management commands (run, disable, delete).

### Reading Exec Output

Use `read_shared_memory` to read results written by `--exec_out` (from scheduled tasks or manual CLI invocations):

- **No arguments**: Returns all JSONL entries with a file size header
- **func parameter**: Searches backwards from the latest entry and returns only the `out` field of the most recent match

Each JSONL entry contains: `{"func": "name", "ts": "ISO8601", "out": "result"}`.

The file is capped at 1 MB — when exceeded, the oldest half of entries is discarded.

### Native Tools vs Dynamic Functions

`read_shared_memory` and `create_scheduled_task` are **native MCP tools** — they do not appear in `list_dynamic_functions` and do not need inspection before use. Call them directly.

## Best Practices

1. **Always list functions first** — call `list_dynamic_functions` at conversation start to discover available tools
2. **Inspect before calling** — use `inspect_dynamic_function` to verify parameters and purpose before execution
3. **Prefer existing functions** — check if a suitable function already exists before registering a new one
4. **One field at a time** — use `update_dynamic_function` for targeted edits, not wholesale rewrites
5. **Handle compilation errors** — if registration fails, fix the C# errors and re-register
6. **Use out-of-process for safety** — use `call_dynamic_process` for untrusted or long-running operations
7. **Keep functions focused** — each function should do one thing well
8. **Descriptive naming** — use clear, descriptive function names (e.g., `fetch_weather`, `parse_csv`)

## Persistence

Functions are auto-saved to SQLite at `%LOCALAPPDATA%\ScriptMCP\tools.db`. No manual save needed. Functions persist across server restarts and sessions.

## Additional Resources

For detailed C# scripting patterns and advanced use cases, consult:
- **`references/csharp-patterns.md`** — Common C# code patterns for dynamic functions
