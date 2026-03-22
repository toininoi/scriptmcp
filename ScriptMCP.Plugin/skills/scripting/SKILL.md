---
name: scripting
description: >-
  This skill should be used when the user asks to create a script, write a script,
  make a script, automate a task with ScriptMCP, build a C# script, create an instructions script, or when the
  user implies the execution of a script that may have been previously created by them, or needs guidance
  on using ScriptMCP tools for script creation, management, and execution. Provides best practices for
  writing robust scripts.
version: 1.0.0
---

# ScriptMCP Scripting

## Purpose

Provide guidance for creating, managing, and executing scripts through the ScriptMCP MCP server. ScriptMCP enables creating C# compiled scripts and plain-English instruction scripts on the fly, persisted in SQLite for reuse across sessions.

## Tool Overview

ScriptMCP exposes these MCP tools:

| Tool                    | Purpose                                                                     |
| ----------------------- | --------------------------------------------------------------------------- |
| `list_scripts`          | Discover all registered scripts                                             |
| `create_script`         | Create a new script                                                         |
| `load_script`           | Load script source from a local file, creating or updating it               |
| `export_script`         | Export stored script source to a local file                                 |
| `update_script`         | Modify a single field on an existing script                                 |
| `inspect_script`        | View metadata, parameters, and optionally source                            |
| `call_script`           | Execute a script in-process                                                 |
| `call_process`   | Execute a script out-of-process (isolated, parallel)                        |
| `compile_script`        | Compile a code script and export its assembly                               |
| `delete_script`         | Remove a script                                                             |
| `get_database`          | Return the currently active ScriptMCP database path                         |
| `set_database`          | Switch the active ScriptMCP database at runtime                             |
| `delete_database`       | Delete a non-default ScriptMCP database after confirmation                  |
| `read_scheduled_task`   | Read the latest scheduled-task output file for a script                     |
| `create_scheduled_task` | Create a scheduled task (Windows Task Scheduler or cron) for a script       |
| `delete_scheduled_task` | Delete a scheduled task for a script                                        |
| `list_scheduled_tasks`  | List ScriptMCP scheduled tasks                                              |
| `start_scheduled_task`  | Enable and start a scheduled task                                           |
| `stop_scheduled_task`   | Disable a scheduled task                                                    |

## Script Types

### Code Scripts (`scriptType: "code"`)

Compiled top-level C# source targeting .NET 9 / C# 13. Write source like a `Program.cs` file and send output to stdout with `Console.Write` or `Console.WriteLine`.

Support both:

- inferred top-level statements
- classic `Program.Main(string[] args)` when useful

**Auto-included namespaces:** System, System.Collections.Generic, System.Globalization, System.IO, System.Linq, System.Net, System.Net.Http, System.Text, System.Text.RegularExpressions, System.Threading.Tasks.

**Available libraries:** All `System.*` assemblies from .NET 9 runtime. Use `System.Text.Json` for JSON, `System.Net.Http.HttpClient` for HTTP, `System.Diagnostics.Process` for shell commands. NuGet packages are NOT available.

**The generated entry point is not async-friendly by default** — use `.Result` or `.GetAwaiter().GetResult()` for async calls.

### Instructions Scripts (`scriptType: "instructions"`)

Plain English instructions with `{paramName}` placeholder substitution. When called, the instructions are returned for Claude to read and follow — the raw text is not shown to the user.

Use instructions scripts for:

- Guided workflows and checklists
- Response formatting templates
- Multi-step procedures that combine multiple tools

## Writing Robust Code Scripts

### Parameter Handling

Define parameters as a JSON array:

```json
[{"name": "url", "type": "string", "description": "The URL to fetch"}]
```

Supported types: `string` (default), `int`, `long`, `double`, `float`, `bool`. ScriptMCP passes the original JSON payload as `args[0]`, and declared parameters are auto-parsed from that JSON into typed names. `scriptArgs` remains available as a compatibility dictionary.

### Error Handling

Wrap risky operations in try-catch and write meaningful error messages:

```csharp
try {
    var client = new HttpClient();
    var response = client.GetStringAsync(url).Result;
    Console.Write(response);
} catch (Exception ex) {
    Console.Write($"Error: {ex.Message}");
}
```

### Common Patterns

**HTTP requests:**

```csharp
var client = new HttpClient();
client.DefaultRequestHeaders.Add("User-Agent", "ScriptMCP");
var json = client.GetStringAsync(url).Result;
Console.Write(json);
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

### Inter-Script Calls

Code scripts can invoke other scripts:

- `ScriptMCP.Call(name, argsJson)` — synchronous, returns output string
- `ScriptMCP.Proc(name, argsJson)` — launches subprocess, returns `Process` for parallel work

## Handling Script Output

**This is critical.** After calling `call_script` or `call_process`, respect the script's output exactly.

### Scripts WITHOUT Output Instructions

Return the script output verbatim. Do NOT:

- Add commentary, labels, or explanations around it
- Summarize, paraphrase, or reword it
- Wrap it in code blocks or markdown formatting
- Prefix it with "Here's the result:" or similar
- Remove, truncate, or reorder any part of it

The script author designed the output for a reason. Deliver it as-is.

### Scripts WITH Output Instructions

Some script results include a trailing `[Output Instructions]: ...` section. When present:

1. **Read the instructions carefully** — they specify exactly how to present the output
2. **Follow them precisely** — if they say "render as a table", render as a table; if they say "return exactly", return exactly
3. **Never show the `[Output Instructions]` line itself** — strip it from what the user sees
4. **Apply instructions only to the output above the marker** — the instructions describe how to format/present the content, not additional content to add

If output instructions say to return the output exactly, return it with zero modifications — no wrapping, no commentary, no formatting changes.

### Output Instructions on Registration

Attach `outputInstructions` when registering a script to control presentation:

- `"present as a markdown table"` — formats tabular data
- `"summarize in 3 bullet points"` — condenses output
- `"return exactly as-is"` — preserves raw output

## Scheduling & Output Files

### Database Management

Use `load_script` when the user wants to create or update a script from a local file. On update, existing description, parameters, script type, and output instructions are preserved unless replacements are provided.

Use `export_script` when the user wants to write a stored script back to a local file.

Use `compile_script` when the user wants a `.dll` from the current stored source. It recompiles the code script, refreshes the stored compiled assembly, and writes the assembly to disk.

Use `get_database` when the user asks which ScriptMCP database is currently active or where scripts are being stored.

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

These database tools are native MCP tools. They do not appear in `list_scripts` and do not require inspection before use.

### Scheduled Tasks

Use `create_scheduled_task` to run a script on a recurring schedule:

- **function_name** (required): The script to run
- **function_args** (default `"{}"`): JSON arguments for the script
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

- **function_name** (required): The script whose scheduled task should be deleted
- **interval_minutes** (default `1`): The interval used when the task was created

On **Windows**, it deletes `ScriptMCP\<function> (<interval>m)` via `schtasks`.
On **Linux/macOS**, it removes the cron entry tagged `# ScriptMCP:<function_name>`.

Use `list_scheduled_tasks` to list ScriptMCP-managed tasks:

On **Windows**, it lists tasks under `\ScriptMCP\`.
On **Linux/macOS**, it lists cron entries tagged `# ScriptMCP:`.

Use `start_scheduled_task` to enable a task and start it immediately:

- **function_name** (required): The script whose scheduled task should be started
- **interval_minutes** (default `1`): The interval used when the task was created

Use `stop_scheduled_task` to disable a task:

- **function_name** (required): The script whose scheduled task should be stopped
- **interval_minutes** (default `1`): The interval used when the task was created

### Reading Scheduled Task Output

Use `read_scheduled_task` to read the result written for a script by `--exec-out` or `--exec-out-append`:

- **function_name**: Required. Returns `<function>.txt` if append mode is in use; otherwise returns the latest matching timestamped file.

Each scheduled execution either writes a new file named like `<function>_YYMMDD_HHMMSS.txt` or appends to `<function>.txt`.

### Native Tools vs Scripts

`get_database`, `set_database`, `delete_database`, `read_scheduled_task`, `create_scheduled_task`, `delete_scheduled_task`, `list_scheduled_tasks`, `start_scheduled_task`, and `stop_scheduled_task` are **native MCP tools** — they do not appear in `list_scripts` and do not need inspection before use. Call them directly.

## Recognizing Implied Script Calls

Users who have previously created scripts will often request their execution without using explicit terms like "run" or "call". They reference the script's purpose or output directly. You must recognize these implicit requests and match them to existing scripts.

**How to handle it:**

1. Call `list_scripts` if you haven't already in this conversation
2. Match the user's intent to one or more candidate scripts by name and description
3. If exactly one script matches, inspect it and call it
4. If multiple scripts could match, ask the user to clarify

**Examples of implied execution:**

| User says                      | Likely script                  | Why                                         |
| ------------------------------ | ------------------------------ | ------------------------------------------- |
| "what's bitcoin at right now?" | `get_btc_price`                | Asking for a price they've fetched before   |
| "convert 50 dollars to euros"  | `usd_to_eur`                   | Describing the script's exact purpose       |
| "show me the market overview"  | `market_fast_fancy` or similar | Referencing a dashboard they built          |
| "how's my portfolio doing?"    | `portfolio`                    | Referring to a script by its domain         |
| "check cpu usage"              | `get_cpu_utilization`          | Describing the metric, not the script       |
| "what time is it?"             | `get_time`                     | Trivial query that maps to a known script   |

The key signal is that the user's request maps directly to what a registered script does, even though they never mention the script by name. Always prefer calling an existing script over using other tools or web search when the match is clear.

## Best Practices

1. **Always list scripts first** — call `list_scripts` at conversation start to discover available tools
2. **Inspect before calling** — use `inspect_script` to verify parameters and purpose before execution
3. **Prefer existing scripts** — check if a suitable script already exists before creating a new one
4. **One field at a time** — use `update_script` for targeted edits, not wholesale rewrites
5. **Handle compilation errors** — if creation fails, fix the C# errors and re-register
6. **Use out-of-process for safety** — use `call_process` for untrusted or long-running operations
7. **Keep scripts focused** — each script should do one thing well
8. **Descriptive naming** — use clear, descriptive script names (e.g., `fetch_weather`, `parse_csv`)
9. **Filesystem-safe names** — script names must contain only letters, numbers, underscore, or hyphen
10. **Confirm destructive database actions** — use `delete_database` only after explicit user approval
11. **Do not auto-create databases** — use `set_database(create=true)` only after the user confirms creation

## Persistence

Scripts are automatically persisted to SQLite on creation. No manual save is needed — scripts survive server restarts and sessions. Use `get_database` to see the active database path.

## Additional Resources

For detailed C# scripting patterns and advanced use cases, consult:

- **`references/csharp-patterns.md`** — Common C# code patterns for scripts
