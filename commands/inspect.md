---
description: Inspect a ScriptMCP dynamic function's metadata and parameters
argument-hint: <function-name>
allowed-tools: ["mcp__scriptmcp__inspect_dynamic_function", "mcp__scriptmcp__list_dynamic_functions", "AskUserQuestion"]
---

Inspect a registered dynamic function and display its details to the user.

If an argument was provided ($ARGUMENTS), use it as the function name. Otherwise, call list_dynamic_functions to show available functions and ask the user which one to inspect.

Call inspect_dynamic_function with the chosen name. Do not use fullInspection unless the user specifically asks for source code — default to the summary view.

Present the inspection result to the user exactly as returned.
