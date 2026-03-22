---
name: source
description: >-
  This skill should be used when the user asks to show source code, view script code, show the code,
  or wants to see the full source code of a ScriptMCP script.
version: 1.0.0
---

# Show ScriptMCP Script Source Code

Show the full source code and compiled status of a registered script.

If an argument was provided ($ARGUMENTS), use it as the script name. Otherwise, call `list_scripts` to show available scripts and ask the user which one to view.

Call `inspect_script` with the chosen name and set `fullInspection` to `true`. Present the result to the user exactly as returned.

If the user wants the source written to a file instead of displayed inline, use `export_script` rather than `inspect_script`.
