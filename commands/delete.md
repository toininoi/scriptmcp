---
description: Delete a ScriptMCP dynamic function
argument-hint: <function-name>
allowed-tools: ["mcp__scriptmcp__delete_dynamic_function", "mcp__scriptmcp__list_dynamic_functions", "mcp__scriptmcp__inspect_dynamic_function", "AskUserQuestion"]
---

Delete a registered dynamic function.

If an argument was provided ($ARGUMENTS), use it as the function name. Otherwise, call list_dynamic_functions to show available functions and ask the user which one to delete.

Before deleting, call inspect_dynamic_function on the target function and show the user what they are about to delete. Ask for explicit confirmation before proceeding.

Only call delete_dynamic_function after the user confirms.
