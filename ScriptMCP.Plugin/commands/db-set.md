---
description: Switch the active ScriptMCP database
argument-hint: <path-or-name>
allowed-tools: ["mcp__scriptmcp__set_database", "AskUserQuestion"]
---

Switch the active ScriptMCP database.

If an argument was provided ($ARGUMENTS), use it as the target path or database name. Otherwise, ask the user which database they want to switch to.

Call `set_database` with:
- `path`: the provided target

If the tool says the database does not exist, ask the user whether they want to create it. Only if they explicitly confirm, call `set_database` again with:
- `path`: the same target
- `create`: true

Present the tool result to the user exactly as returned.
