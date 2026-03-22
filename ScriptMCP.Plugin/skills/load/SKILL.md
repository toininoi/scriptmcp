---
name: load
description: >-
  This skill should be used when the user asks to load a script from a file, import script source,
  sync a local script file into ScriptMCP, or create/update a ScriptMCP script from disk.
version: 1.0.0
---

# Load a ScriptMCP Script from a File

Load script source from a local file into ScriptMCP.

If an argument was provided ($ARGUMENTS), use it as the source file path. Otherwise, ask the user which file to load.

Call `load_script` with:

- `path`
- optional `name` if the user wants a script name different from the file name
- optional `description`, `parameters`, `scriptType`, or `outputInstructions` only if the user explicitly wants to replace existing metadata

Behavior:

- If the script does not exist, `load_script` creates it
- If the script exists, `load_script` updates it from the file contents
- Source-affecting changes compile automatically for code scripts

Report the result exactly as returned unless the user asked for extra explanation.
