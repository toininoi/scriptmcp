---
description: Delete a ScriptMCP scheduled task
argument-hint: <function-name>
allowed-tools: ["mcp__scriptmcp__delete_scheduled_task", "AskUserQuestion"]
---

Delete a scheduled task created by ScriptMCP.

If an argument was provided ($ARGUMENTS), use it as the function name. Otherwise, ask the user which scheduled task they want to delete.

Ask the user for the interval in minutes if it is not already clear from context.

Call `delete_scheduled_task` with:
- `function_name`
- `interval_minutes`

Only delete the scheduled task the user identified.
