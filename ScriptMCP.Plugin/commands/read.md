---
description: Read the latest ScriptMCP scheduled task output
argument-hint: <function-name>
allowed-tools: ["mcp__scriptmcp__read_scheduled_task", "AskUserQuestion"]
---

Read the latest scheduled-task output written by ScriptMCP for a function.

If an argument was provided ($ARGUMENTS), use it as the function name. Otherwise, ask the user which function's scheduled-task output they want to read.

Call `read_scheduled_task` with:
- `function_name`

Return the output exactly as received.
