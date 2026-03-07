# ScriptMCP

A dynamic function runtime for AI agents via the Model Context Protocol (MCP). ScriptMCP lets your AI agent create, compile, and execute C# functions on the fly — no restart required. Functions persist in a local SQLite database and can be invoked in-process or out-of-process for parallel execution.

![ScriptMCP in Claude Code](snapshot2.png)

## Overview

ScriptMCP exposes 18 MCP tools that together form a self-extending toolbox:

| Tool                        | Description                                                     |
| --------------------------- | --------------------------------------------------------------- |
| `register_dynamic_function` | Register a new function (C# code or plain English instructions) |
| `update_dynamic_function`   | Update one field on an existing function entry                  |
| `call_dynamic_function`     | Execute a function in-process                                   |
| `call_dynamic_process`      | Execute a function out-of-process (subprocess)                  |
| `list_dynamic_functions`    | List registered function names as a comma-delimited string      |
| `inspect_dynamic_function`  | View function metadata, with optional full source inspection    |
| `compile_dynamic_function`  | Compile a code function from its stored source                  |
| `delete_dynamic_function`   | Remove a function                                               |
| `save_dynamic_functions`    | Legacy no-op (functions auto-persist to SQLite)                 |
| `get_database`             | Show the currently active ScriptMCP database path               |
| `set_database`             | Switch to a different ScriptMCP database at runtime             |
| `delete_database`          | Delete a non-default ScriptMCP database                         |
| `create_scheduled_task`     | Schedule a function to run at a recurring interval              |
| `read_scheduled_task`       | Read the latest scheduled-task output for a function            |
| `delete_scheduled_task`     | Delete a scheduled task for a function                          |
| `list_scheduled_tasks`      | List ScriptMCP scheduled tasks                                  |
| `start_scheduled_task`      | Enable and start a scheduled task                               |
| `stop_scheduled_task`       | Disable a scheduled task                                        |

### How It Works

1. **Discover** — the AI agent discovers available functions via `list_dynamic_functions` at the start of each conversation
2. **Register** — the AI agent writes and registers C# functions or plain English instructions on your behalf (or you provide explicit code)
3. **Persist** — functions are **compiled via Roslyn** on registration and **stored in SQLite** — they survive server restarts
4. **Execute** — functions are invoked automatically by the AI via `call_dynamic_function` (in-process) or `call_dynamic_process` (out-of-process)
5. **Switch Databases** — the active SQLite database can be inspected or changed at runtime via `get_database` and `set_database`
6. **Schedule** — functions can be scheduled to run at recurring intervals via `create_scheduled_task`
7. **Update** — existing functions can be revised in place with `update_dynamic_function` when only one stored field needs to change
8. **Delete** — functions or non-default databases can be removed when no longer needed

### Function Types

- **`code`** — C# method bodies compiled at runtime. Has access to .NET 9 APIs including HTTP, JSON, regex, diagnostics, and more.
- **`instructions`** — Plain English instructions the AI reads and follows (e.g. multi-step workflows combining multiple tools and web search).

### In-Process Execution

`call_dynamic_function` runs a function directly inside the MCP server process. This is the default and fastest way to execute a function.

### Out-of-Process Execution

`call_dynamic_process` spawns `scriptmcp.exe --exec <functionName> [argsJson]` as a subprocess. This enables parallelization.

### Scheduled Tasks

`create_scheduled_task` sets up a recurring job that runs a dynamic function at a fixed interval. On Windows it uses Task Scheduler; on Linux/macOS it uses cron.

### Database Selection

ScriptMCP stores functions in a SQLite database and can switch databases during a live session:

- `get_database` returns the currently active database path
- `set_database` switches to another database path or database name
- `delete_database` first validates the target database and returns a yes-or-no confirmation prompt before deleting a non-default database

If `set_database` receives only a file name such as `work.db`, it resolves that name inside the default ScriptMCP data directory for the current OS. If the target database does not exist yet, the caller must confirm creation by passing `create=true`.

### Output Instructions

Functions can include optional **output instructions** that tell the AI how to format results. The AI reads the instructions and formats the output accordingly — e.g. render as a markdown table, display in an ASCII box, summarize in bullet points.

## Examples

### Let the AI create a function for you

Just describe what you need in natural language — the AI writes the C# code, registers it, and calls it:

```
You:    create a function that returns the current time, nothing else
Agent:  [registers get_time → return DateTime.Now.ToString("hh:mm:ss tt");]

You:    what time is it?
Agent:  10:07:39 pm
```

### HTTP JSON fetch

```
You:    create a function that fetches a JSON endpoint and returns the top-level keys
Agent:  [registers json_keys → fetches via HttpClient, parses with System.Text.Json]

You:    run json_keys on https://api.example.com/status
Agent:  status, version, uptime
```

### Instructions-type functions

Not everything needs code. You can register a function with plain English step-by-step instructions that become part of the function. When the function is called, the AI reads and follows those instructions:

```
You:    create a function called find_stock_symbol with these instructions:
        1) Take the user's description and search Yahoo Finance for a matching ticker
        2) Return the ticker symbol, company name, and exchange

You:    find the stock ticker for "that electric car company elon runs"
Agent:  [calls find_stock_symbol → reads stored instructions → searches Yahoo Finance]
        TSLA — Tesla, Inc. (NASDAQ)
```

### Function chaining

Code functions can call other functions and hand off to the AI via `[Output Instructions]`:

```
You:    create foo — calls get_time, then tells the AI to format it
Agent:  [registers foo as a code function]:

        var timeOutput = ScriptMCP.Call("get_time", "{}").Trim();
        return timeOutput + "\n[Output Instructions]: Extract the time "
             + "and return it as hours, mins, secs.";

You:    run foo
Agent:  11 hours, 12 mins, 23 secs
```

The code function handles what code does best (calling APIs, fetching data), then the AI handles what it does best (interpreting instructions and chaining tool calls).

### Scheduled tasks

```
You:    schedule get_stock_price to run every 5 minutes with {"symbol":"AAPL"}
Agent:  [calls create_scheduled_task → interval_minutes=5]
        Scheduled task created and started.

You:    what was the last stock price result?
Agent:  [calls read_scheduled_task → function_name="get_stock_price"]
        AAPL: $266.86 (+3.37, +1.28%)

You:    change it to run every 10 minutes instead
Agent:  [calls delete_scheduled_task → interval_minutes=5]
        [calls create_scheduled_task → interval_minutes=10]
        Rescheduled to every 10 minutes.

You:    delete the stock price task
Agent:  [calls delete_scheduled_task → function_name="get_stock_price"]
        Scheduled task deleted.
```

### Switching databases at runtime

```
You:    which ScriptMCP database is active?
Agent:  [calls get_database]
        C:\Users\you\AppData\Local\ScriptMCP\scriptmcp.db

You:    switch to sandbox.db
Agent:  [calls set_database → path="sandbox.db"]
        Database does not exist...

You:    yes, create it
Agent:  [calls set_database → path="sandbox.db", create=true]
        Switched database from:
          C:\Users\you\AppData\Local\ScriptMCP\scriptmcp.db
        to:
          C:\Users\you\AppData\Local\ScriptMCP\sandbox.db
```

## Install

Choose one of these installation modes first:

- `Source code` — download `Source code (zip)`, install the .NET 9 SDK, and run `ScriptMCP.Console` with `dotnet run`.
- `Framework-dependent` — download `scriptmcp-<rid>-framework-dependent.zip`, extract it, and use the included executable. This requires a compatible .NET 9 runtime on the target machine.
- `Self-contained` — download `scriptmcp-<rid>-self-contained.zip`, extract it, and use the included executable. No separate .NET install is required.

Supported release platforms:

- Windows x64
- Linux x64
- macOS arm64 (Apple Silicon)

#### macOS — Removing the Quarantine Flag

macOS Gatekeeper blocks unsigned executables downloaded from the internet. Before running ScriptMCP for the first time, remove the quarantine flag:

```bash
xattr -d com.apple.quarantine /opt/scriptmcp/scriptmcp
```

Replace the path with wherever you extracted the executable. This only needs to be done once.

#### Verifying Build Provenance

All release binaries include GitHub artifact attestations that cryptographically prove they were built by this repository's CI pipeline.

**Via the GitHub website** — visit the [Attestations page](https://github.com/sithiro/ScriptMCP/attestations) to see all signed build records. Each attestation links back to the exact workflow run and commit that produced it.

**Via the GitHub CLI** — if you have the [GitHub CLI](https://cli.github.com/) installed, you can verify a downloaded binary locally:

```bash
gh attestation verify scriptmcp-osx-arm64-self-contained.zip --repo sithiro/ScriptMCP
```

### Claude Code CLI

Use the `claude mcp add` command to register ScriptMCP as a user-level MCP server:

Source code:

```bash
claude mcp add -s user -t stdio scriptmcp -- dotnet run --project C:\path\to\ScriptMCP.Console\ScriptMCP.Console.csproj -c Release
```

Windows self-contained or framework dependent:

```bash
claude mcp add -s user -t stdio scriptmcp -- 'C:\Tools\ScriptMcp 1.1.1\scriptmcp.exe'
```

macOS/Linux self-contained or framework dependent:

```bash
claude mcp add -s user -t stdio scriptmcp -- /opt/scriptmcp/scriptmcp
```

The `-s user` flag makes ScriptMCP available across all your projects. To scope it to a single project, use `-s project` instead.

To remove it:

```bash
claude mcp remove -s user scriptmcp
```

### Claude Desktop

In Claude Desktop, go to **Settings → Developer** and click **Edit Config** to open `claude_desktop_config.json`. Add ScriptMCP to the `mcpServers` section:

![Claude Desktop MCP Settings](snapshot3.png)

Source code:

```json
{
  "mcpServers": {
    "scriptmcp": {
      "command": "dotnet",
      "args": ["run", "--project", "C:\\path\\to\\ScriptMCP.Console\\ScriptMCP.Console.csproj", "-c", "Release"]
    }
  }
}
```

Windows self-contained or framework dependent:

```json
{
  "mcpServers": {
    "scriptmcp": {
      "command": "C:\\Tools\\ScriptMcp 1.1.1\\scriptmcp.exe",
      "args": []
    }
  }
}
```

macOS/Linux self-contained or framework dependent:

```json
{
  "mcpServers": {
    "scriptmcp": {
      "command": "/opt/scriptmcp/scriptmcp",
      "args": []
    }
  }
}
```

### Codex CLI

Use `codex mcp add` to register ScriptMCP:

Source code:

```bash
codex mcp add scriptmcp -- dotnet run --project C:\path\to\ScriptMCP.Console\ScriptMCP.Console.csproj -c Release
```

Windows self-contained or framework dependent:

```bash
codex mcp add scriptmcp -- "C:\Tools\ScriptMcp 1.1.1\scriptmcp.exe"
```

macOS/Linux self-contained or framework dependent:

```bash
codex mcp add scriptmcp -- /opt/scriptmcp/scriptmcp
```

Then start Codex normally and use `/mcp` inside the TUI to verify the server is active.

To remove it:

```bash
codex mcp remove scriptmcp
```

### Arguments

`scriptmcp` supports these runtime arguments:

- `--db [FILEPATH|FILENAME]`: use a specific SQLite database path instead of the default ScriptMCP data directory
- `--exec <functionName> [argsJson]`: execute one dynamic function and write the result to stdout
- `--exec-out <functionName> [argsJson]`: execute one dynamic function, write the result to stdout, and persist the cleaned output to a new timestamped file
- `--exec-out-append <functionName> [argsJson]`: execute one dynamic function, write the result to stdout, and append the cleaned output to a stable `<function>.txt` file

Examples:

```bash
scriptmcp --exec get_time

scriptmcp --exec get_stock_price '{"symbol":"AAPL"}'
```

Use `--db` to select a custom database path for either MCP mode or CLI execution mode:

```bash
# absolute path
scriptmcp --db "D:\Data\scriptmcp.db" --exec get_time

# relative path (resolved under the default ScriptMCP data directory)
scriptmcp --db test.db --exec get_time
```

`--db` applies in both MCP server mode and CLI execution modes (`--exec`, `--exec-out`, `--exec-out-append`). If you pass a relative path (for example, `--db test.db`), it is resolved under the default ScriptMCP data directory for your OS.

At runtime, agents can inspect or change the active database without restarting the server by calling `get_database` and `set_database`. Deletion of a non-default database is handled through `delete_database`, which first validates the target and returns a yes-or-no confirmation prompt.


### Data Directory

Functions are persisted in a SQLite database created on first run. Execution output from `--exec-out` is stored in the `output/` folder alongside it:

- Windows: `%LOCALAPPDATA%\ScriptMCP\`
- macOS: `~/Library/Application Support/ScriptMCP/`
- Linux: `~/.local/share/ScriptMCP/`

| File           | Purpose                                                                                               |
| -------------- | ----------------------------------------------------------------------------------------------------- |
| `scriptmcp.db` | SQLite database of registered functions                                                               |
| `output/`      | Timestamped files or append-mode `<function>.txt` files written by `--exec-out` / `--exec-out-append` |

## Agent Instructions (CLAUDE.md / AGENTS.md)

ScriptMCP delivers its agent instructions automatically during the MCP handshake — **no extra files are needed for normal operation.**

If ScriptMCP is not behaving as expected, you can reinforce the instructions by placing a markdown file in your project or globally:

| Agent        | File        | Global location                            |
| ------------ | ----------- | ------------------------------------------ |
| Claude Code  | `CLAUDE.md` | `~/.claude/CLAUDE.md`                      |
| OpenAI Codex | `AGENTS.md` | `~/AGENTS.md` (or common parent directory) |

If you only use one agent, you only need the corresponding file. Both files contain the same instructions.

## Scripting Environment

- **.NET 9 / C# 13** runtime
- Self-contained release zips do not require a separate .NET installation
- Framework-dependent release zips require a compatible .NET 9 runtime on the target machine
