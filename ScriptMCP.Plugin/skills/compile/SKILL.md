---
name: compile
description: >-
  This skill should be used when the user asks to recompile a script, compile a script, rebuild a script,
  export a compiled assembly, or wants to trigger recompilation of a ScriptMCP code script.
version: 1.0.0
---

# Compile and Export a ScriptMCP Script

Compile a registered code-type script, refresh the stored compiled assembly, and export the compiled assembly to a `.dll` file.

If an argument was provided ($ARGUMENTS), use it as the script name. Otherwise, call `list_scripts` to show available scripts and ask the user which one to compile.

Call `compile_script` with the chosen name and an optional destination path if the user requested one. Report the result to the user. If compilation fails, show the errors.
