---
description: Show the full source code of a ScriptMCP dynamic function
argument-hint: <function-name>
allowed-tools: ["mcp__scriptmcp__inspect_dynamic_function", "mcp__scriptmcp__list_dynamic_functions", "AskUserQuestion"]
---

Show the full source code and compiled status of a registered dynamic function.

If an argument was provided ($ARGUMENTS), use it as the function name. Otherwise, call list_dynamic_functions to show available functions and ask the user which one to view.

Call inspect_dynamic_function with the chosen name and set fullInspection to true. Present the result to the user exactly as returned.
