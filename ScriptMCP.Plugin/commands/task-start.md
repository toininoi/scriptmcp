---
description: Start a ScriptMCP scheduled task
argument-hint: <function-name>
allowed-tools: ["mcp__scriptmcp__start_scheduled_task", "AskUserQuestion"]
---

Start or enable a ScriptMCP scheduled task.

If an argument was provided ($ARGUMENTS), use it as the function name. Otherwise, ask the user which scheduled task they want to start.

Ask the user for the interval in minutes if it is not already clear from context.

Call `start_scheduled_task` with:
- `function_name`
- `interval_minutes`

Only start the scheduled task the user identified.
