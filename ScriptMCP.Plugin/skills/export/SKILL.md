---
name: export
description: >-
  This skill should be used when the user asks to export a script, save script source to a file,
  write a stored ScriptMCP script to disk, or dump a script into a local source file.
version: 1.0.0
---

# Export a ScriptMCP Script to a File

Export stored script source from ScriptMCP to a local file.

If an argument was provided ($ARGUMENTS), use it as the script name. Otherwise, call `list_scripts` to show available scripts and ask the user which one to export.

Call `export_script` with:

- `name`
- optional `path` if the user specified a destination file

By default:

- code scripts export to `<name>.cs`
- instructions scripts export to `<name>.txt`

Report the resulting path to the user.
