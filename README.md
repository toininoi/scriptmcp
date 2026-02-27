# ScriptMCP

A dynamic function runtime for AI agents via the Model Context Protocol (MCP). ScriptMCP lets your AI agent create, compile, and execute C# functions on the fly — no restart required. Functions persist in a local SQLite database and can be invoked in-process or out-of-process for parallel execution.

![ScriptMCP in Claude Code](snapshot2.png)

## Overview

ScriptMCP exposes 8 MCP tools that together form a self-extending toolbox:

| Tool | Description |
|------|-------------|
| `register_dynamic_function` | Register a new function (C# code or plain English instructions) |
| `call_dynamic_function` | Execute a function in-process |
| `call_dynamic_process` | Execute a function out-of-process (subprocess) |
| `list_dynamic_functions` | List all registered functions |
| `inspect_dynamic_function` | View source code and metadata of a function |
| `compile_dynamic_function` | Compile a code function from its stored source |
| `delete_dynamic_function` | Remove a function |
| `save_dynamic_functions` | Legacy no-op (functions auto-persist to SQLite) |

### How It Works

1. **Register** — the AI agent writes and registers C# functions or plain English instructions on your behalf (or you provide explicit code)
2. **Execute** — functions are invoked automatically by the AI via `call_dynamic_function` (in-process) or `call_dynamic_process` (out-of-process)
3. **Persist** — functions are **compiled via Roslyn** on registration and **stored in SQLite** — they survive server restarts
4. **Discover** — the AI agent discovers available functions via `list_dynamic_functions` at the start of each conversation

### Function Types

- **`code`** — C# method bodies compiled at runtime. Has access to .NET 9 APIs including HTTP, JSON, regex, diagnostics, and more.
- **`instructions`** — Plain English instructions the AI reads and follows (e.g. multi-step workflows combining multiple tools and web search).

### Output Instructions

Functions can include optional **output instructions** that tell the AI how to format results. When present, a `[Output Instructions]` tag is appended to the function output. The AI reads the instructions and formats the output accordingly — e.g. render as a markdown table, display in an ASCII box, summarize in bullet points.

### Out-of-Process Execution

`call_dynamic_process` spawns `scriptmcp.exe --exec <functionName> [argsJson]` as a subprocess. This enables:
- **Parallel execution** — run multiple functions concurrently without blocking the MCP server
- **Isolation** — function crashes don't affect the server
- **Composition** — functions can spawn other functions as subprocesses (e.g. `market_fast` runs NASDAQ and Dow queries in parallel)

## Examples

### Let the AI create a function for you

Just describe what you need in natural language — the AI writes the C# code, registers it, and calls it:

```
You:    create a function that returns the current time, nothing else
Agent:  [registers get_time → return DateTime.Now.ToString(“hh:mm:ss tt”);]

You:    what time is it?
Agent:  10:07:39 pm
```

### Or provide explicit code

If you prefer full control, you can dictate the exact implementation:

```
You:    register a code function called get_time with body: return DateTime.Now.ToString(“hh:mm:ss tt”);
Agent:  [registers get_time with your exact code]

You:    what time is it?
Agent:  10:07:39 pm
```

### Filesystem helper

```
You:    create a function that lists all .log files under the current directory
Agent:  [registers list_logs → return Directory.GetFiles(".", "*.log", SearchOption.AllDirectories)]

You:    run list_logs
Agent:  ./logs/app.log
        ./logs/errors.log
```

### HTTP JSON fetch

```
You:    create a function that fetches a JSON endpoint and returns the top-level keys
Agent:  [registers json_keys → fetches via HttpClient, parses with System.Text.Json]

You:    run json_keys on https://api.example.com/status
Agent:  status, version, uptime
```

### Text processing

```
You:    create a function that counts words in a string input
Agent:  [registers word_count with a text parameter]

You:    word_count: "hello there general kenobi"
Agent:  4
```

### System info

```
You:    create a function that returns machine name and current uptime
Agent:  [registers uptime → uses Environment.MachineName + Environment.TickCount64]

You:    run uptime
Agent:  devbox-01 — 03:42:19
```

### Simple math utility

```
You:    create a function that converts Fahrenheit to Celsius
Agent:  [registers f_to_c with a temp parameter]

You:    f_to_c: 98.6
Agent:  37.0
```

### Small finance example (optional)

```
You:    create a function that gets the current stock price for a given ticker symbol
Agent:  [registers get_stock_price with a symbol parameter, fetches from a financial API]

You:    what's the price of AAPL?
Agent:  AAPL: $266.86 (+3.37, +1.28%)
```

### Instructions-type functions

Not everything needs code. Plain English instructions let the AI orchestrate multi-step workflows:

```
You:    find the stock ticker for “that electric car company elon runs”
Agent:  [calls find_stock_symbol → reads instructions → searches Yahoo Finance]
        TSLA — Tesla, Inc. (NASDAQ)
```

### Output instructions

Control how results are presented:

```
You:    add output instructions to get_time: “Display the time inside an ASCII box”

You:    what time is it?
Agent:  ┌──────────────┐
        │  10:40:00 pm │
        └──────────────┘
```

## Install

### Prebuilt Console App

ScriptMCP.Console is published as a self-contained, single-file executable for:
- Windows x64
- Linux x64
- macOS x64
- macOS arm64

1. Download the release zip for your OS and extract it to a location of your choice (e.g. `C:\Tools\ScriptMcp 1.1.1`).
2. Add an MCP server config to your AI agent that targets the executable.
   - `type` must be `stdio`.

### Claude Code

#### Via CLI (recommended)

Use the `claude mcp add` command to register ScriptMCP as a user-level MCP server:

Windows:

```bash
claude mcp add -s user -t stdio scriptmcp -- 'C:\Tools\ScriptMcp 1.1.1\scriptmcp.exe'
```

macOS/Linux:

```bash
claude mcp add -s user -t stdio scriptmcp -- /opt/scriptmcp/scriptmcp
```

The `-s user` flag makes ScriptMCP available across all your projects. To scope it to a single project, use `-s project` instead.

To remove it:

```bash
claude mcp remove -s user scriptmcp
```

#### Via .mcp.json

Alternatively, create a `.mcp.json` in your project directory.

Windows:

```json
{
  “mcpServers”: {
    “scriptmcp”: {
      “type”: “stdio”,
      “command”: “C:\\Tools\\ScriptMcp 1.1.1\\scriptmcp.exe”,
      “args”: []
    }
  }
}
```

macOS/Linux example:

```json
{
  “mcpServers”: {
    “scriptmcp”: {
      “type”: “stdio”,
      “command”: “/opt/scriptmcp/scriptmcp”,
      “args”: []
    }
  }
}
```

### Claude Desktop

In Claude Desktop, go to **Settings → Developer** and click **Edit Config** to open `claude_desktop_config.json`. Add ScriptMCP to the `mcpServers` section:

![Claude Desktop MCP Settings](snapshot3.png)

Windows:

```json
{
  “mcpServers”: {
    “scriptmcp”: {
      “command”: “C:\\Tools\\ScriptMcp 1.1.1\\scriptmcp.exe”,
      “args”: []
    }
  }
}
```

macOS/Linux:

```json
{
  “mcpServers”: {
    “scriptmcp”: {
      “command”: “/opt/scriptmcp/scriptmcp”,
      “args”: []
    }
  }
}
```

### CLI Mode

ScriptMCP can also run a single function from the command line without starting the MCP server:

```bash
scriptmcp.exe --exec get_time
# 10:07:39 pm

scriptmcp.exe --exec get_stock_price '{“symbol”:”AAPL”}'
# AAPL: $266.86 (+3.37, +1.28%)
```

This is what `call_dynamic_process` uses under the hood.

### tools.db Location

Functions are persisted in a SQLite database created on first run:

- Windows: `%LOCALAPPDATA%\ScriptMCP\tools.db`
- macOS: `~/Library/Application Support/ScriptMCP/tools.db`
- Linux: `~/.local/share/ScriptMCP/tools.db`

## Scripting Environment

- **.NET 9 / C# 13** runtime — no .NET installation required, the executable is self-contained
