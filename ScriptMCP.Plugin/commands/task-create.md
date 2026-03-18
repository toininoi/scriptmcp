---
description: Manage ScriptMCP scheduled tasks
argument-hint: <function-name>
allowed-tools: ["mcp__scriptmcp__create_scheduled_task", "mcp__scriptmcp__read_scheduled_task", "mcp__scriptmcp__delete_scheduled_task", "mcp__scriptmcp__list_scheduled_tasks", "mcp__scriptmcp__start_scheduled_task", "mcp__scriptmcp__stop_scheduled_task", "AskUserQuestion"]
---

Manage scheduled tasks created by ScriptMCP.

If an argument was provided ($ARGUMENTS), use it as the function name. Otherwise, ask the user which function's scheduled task they want to manage.

Determine whether the user wants to create a task, read the latest task output, list tasks, start a task, stop a task, or delete a task.

For create:
- Ask for the interval in minutes if it is not already clear.
- Ask for JSON arguments if the function needs them. Otherwise use `"{}"`.
- Ask whether the user wants append mode. Default to `false` if not specified.
- Call `create_scheduled_task` with:
  - `function_name`
  - `function_args`
  - `interval_minutes`
  - `append`

For read:
- Call `read_scheduled_task` with:
  - `function_name`

For list:
- Call `list_scheduled_tasks` with no arguments.

For start:
- Ask for the interval in minutes if it is not already clear.
- Call `start_scheduled_task` with:
  - `function_name`
  - `interval_minutes`

For stop:
- Ask for the interval in minutes if it is not already clear.
- Call `stop_scheduled_task` with:
  - `function_name`
  - `interval_minutes`

For delete:
- Ask for the interval in minutes if it is not already clear.
- Call `delete_scheduled_task` with:
  - `function_name`
  - `interval_minutes`

Only perform the scheduled-task action the user requested.
