---
description: Stop a ScriptMCP scheduled task
argument-hint: <function-name>
allowed-tools: ["mcp__scriptmcp__stop_scheduled_task", "AskUserQuestion"]
---

Stop or disable a ScriptMCP scheduled task.

If an argument was provided ($ARGUMENTS), use it as the function name. Otherwise, ask the user which scheduled task they want to stop.

Ask the user for the interval in minutes if it is not already clear from context.

Call `stop_scheduled_task` with:
- `function_name`
- `interval_minutes`

Only stop the scheduled task the user identified.
