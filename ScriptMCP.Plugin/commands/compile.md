---
description: Recompile a ScriptMCP dynamic code function
argument-hint: <function-name>
allowed-tools: ["mcp__scriptmcp__compile_dynamic_function", "mcp__scriptmcp__list_dynamic_functions", "mcp__scriptmcp__inspect_dynamic_function", "AskUserQuestion"]
---

Recompile a registered code-type dynamic function.

If an argument was provided ($ARGUMENTS), use it as the function name. Otherwise, call list_dynamic_functions to show available functions and ask the user which one to recompile.

Call compile_dynamic_function with the chosen name and report the result to the user. If compilation fails, show the errors.
