---
description: Run a ScriptMCP dynamic function out-of-process (isolated)
argument-hint: <function-name>
allowed-tools: ["mcp__scriptmcp__call_dynamic_process", "mcp__scriptmcp__list_dynamic_functions", "mcp__scriptmcp__inspect_dynamic_function", "AskUserQuestion"]
---

Execute a registered dynamic function out-of-process in an isolated subprocess.

If an argument was provided ($ARGUMENTS), use it as the function name. Otherwise, call list_dynamic_functions to show available functions and ask the user which one to run.

Call inspect_dynamic_function on the chosen function to verify its parameters. If the function requires arguments, ask the user to provide them.

Ask whether the user wants output persisted to file:
- Use `output_mode: "Default"` for `--exec` (no persisted output file)
- Use `output_mode: "WriteNew"` for `--exec-out` (new timestamped file per execution)
- Use `output_mode: "WriteAppend"` for `--exec-out-append` (append to stable `<function>.txt`)
If not specified, default to `Default`.

Call call_dynamic_process with the name, arguments, and chosen `output_mode`. Return the output exactly as received — do not add, remove, wrap, or modify the output in any way. If the result contains [Output Instructions], follow them precisely and do not show the [Output Instructions] line itself.
