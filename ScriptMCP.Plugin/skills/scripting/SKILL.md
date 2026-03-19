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
| `get_database` | Return the currently active ScriptMCP database path |
| `set_database` | Switch the active ScriptMCP database at runtime |
| `delete_database` | Delete a non-default ScriptMCP database after confirmation |
| `read_scheduled_task` | Read the latest scheduled-task output file for a function |
| `create_scheduled_task` | Create a scheduled task (Windows Task Scheduler or cron) for a dynamic function |
| `delete_scheduled_task` | Delete a scheduled task for a dynamic function |
| `list_scheduled_tasks` | List ScriptMCP scheduled tasks |
| `start_scheduled_task` | Enable and start a scheduled task |
| `stop_scheduled_task` | Disable a scheduled task |

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

## Scheduling & Output Files

### Database Management

Use `get_database` when the user asks which ScriptMCP database is currently active or where functions are being stored.

Use `set_database` to switch databases during a live session:

- **path** (optional): Absolute path, relative path, or bare database name
- **create** (default `false`): Must be set to `true` to create a missing database after user confirmation

Rules:
- If `path` is omitted, ScriptMCP switches to the default database
- If `path` is only a file name like `sandbox.db`, it resolves under the default ScriptMCP data directory
- If the target database does not exist, do not create it silently; ask the user and then call `set_database` again with `create=true` if they approve

Use `delete_database` to remove a database file:

- **path** (required): Absolute path or bare database name
- **confirm** (default `false`): Must be set to `true` after explicit user confirmation

Rules:
- Never call `delete_database` without explicit confirmation from the user
- The default ScriptMCP database cannot be deleted
- If the target database is currently active, ScriptMCP switches to the default database before deletion

These database tools are native MCP tools. They do not appear in `list_dynamic_functions` and do not require inspection before use.

### Scheduled Tasks

Use `create_scheduled_task` to run a dynamic function on a recurring schedule:

- **function_name** (required): The dynamic function to run
- **function_args** (default `"{}"`): JSON arguments for the function
- **interval_minutes** (required): How often to run, in minutes
- **append** (default `false`): When true, append to `<function>.txt` instead of creating a new timestamped file each run

Ask the user whether they want:
- a unique output file per run
- or a single output file reused across runs

Set `append=true` only for the single-file behavior.

On **Windows**, uses Task Scheduler (`schtasks`) and runs `scriptmcp.exe` directly.
On **Linux/macOS**, uses cron. Each entry is tagged with `# ScriptMCP:<function_name>` for easy identification and removal.

The task uses `--exec-out` mode. By default it writes the result to a timestamped file in `output` beside the ScriptMCP database. With `append=true`, it uses `--exec-out-append` and appends to `<function>.txt`.

After creation, the task is immediately run once. The tool returns the task name and management commands (run, disable, delete).

Use `delete_scheduled_task` to remove a scheduled task:

- **function_name** (required): The dynamic function whose scheduled task should be deleted
- **interval_minutes** (default `1`): The interval used when the task was created

On **Windows**, it deletes `ScriptMCP\<function> (<interval>m)` via `schtasks`.
On **Linux/macOS**, it removes the cron entry tagged `# ScriptMCP:<function_name>`.

Use `list_scheduled_tasks` to list ScriptMCP-managed tasks:

On **Windows**, it lists tasks under `\ScriptMCP\`.
On **Linux/macOS**, it lists cron entries tagged `# ScriptMCP:`.

Use `start_scheduled_task` to enable a task and start it immediately:

- **function_name** (required): The dynamic function whose scheduled task should be started
- **interval_minutes** (default `1`): The interval used when the task was created

Use `stop_scheduled_task` to disable a task:

- **function_name** (required): The dynamic function whose scheduled task should be stopped
- **interval_minutes** (default `1`): The interval used when the task was created

### Reading Scheduled Task Output

Use `read_scheduled_task` to read the result written for a function by `--exec-out` or `--exec-out-append`:

- **function_name**: Required. Returns `<function>.txt` if append mode is in use; otherwise returns the latest matching timestamped file.

Each scheduled execution either writes a new file named like `<function>_YYMMDD_HHMMSS.txt` or appends to `<function>.txt`.

### Native Tools vs Dynamic Functions

`get_database`, `set_database`, `delete_database`, `read_scheduled_task`, `create_scheduled_task`, `delete_scheduled_task`, `list_scheduled_tasks`, `start_scheduled_task`, and `stop_scheduled_task` are **native MCP tools** — they do not appear in `list_dynamic_functions` and do not need inspection before use. Call them directly.

## Best Practices

1. **Always list functions first** — call `list_dynamic_functions` at conversation start to discover available tools
2. **Inspect before calling** — use `inspect_dynamic_function` to verify parameters and purpose before execution
3. **Prefer existing functions** — check if a suitable function already exists before registering a new one
4. **One field at a time** — use `update_dynamic_function` for targeted edits, not wholesale rewrites
5. **Handle compilation errors** — if registration fails, fix the C# errors and re-register
6. **Use out-of-process for safety** — use `call_dynamic_process` for untrusted or long-running operations
7. **Keep functions focused** — each function should do one thing well
8. **Descriptive naming** — use clear, descriptive function names (e.g., `fetch_weather`, `parse_csv`)
9. **Filesystem-safe names** — function names must contain only letters, numbers, underscore, or hyphen
10. **Confirm destructive database actions** — use `delete_database` only after explicit user approval
11. **Do not auto-create databases** — use `set_database(create=true)` only after the user confirms creation

## Persistence

Functions are automatically persisted to SQLite on registration. No manual save is needed — functions survive server restarts and sessions. Use `get_database` to see the active database path.

## Additional Resources

For detailed C# scripting patterns and advanced use cases, consult:
- **`references/csharp-patterns.md`** — Common C# code patterns for dynamic functions
