---
description: Delete a non-default ScriptMCP database
argument-hint: <path-or-name>
allowed-tools: ["mcp__scriptmcp__delete_database", "AskUserQuestion"]
---

Delete a ScriptMCP database file.

If an argument was provided ($ARGUMENTS), use it as the target path or database name. Otherwise, ask the user which database they want to delete.

Before deletion, ask for explicit confirmation.

Only after the user confirms, call `delete_database` with:
- `path`: the chosen target
- `confirm`: true

Present the tool result to the user exactly as returned.
