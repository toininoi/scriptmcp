---
name: create
description: >-
  This skill should be used when the user asks to create a script, make a new script,
  add a script, or wants to create a new ScriptMCP script (code or instructions type).
version: 1.0.0
---

# Create a New ScriptMCP Script

Guide the user through creating a new script.

If an argument was provided ($ARGUMENTS), use it as the script name. Otherwise, ask the user what script they want to create.

## Steps

1. Call `list_scripts` to check if a script with that name already exists
2. Ensure the name uses only letters, numbers, underscore, or hyphen
3. Ask the user to describe what the script should do
4. Determine the appropriate script type: `code` for C# compiled scripts, `instructions` for plain-English guidance
5. Define the parameters as a JSON array
6. Write the script body (top-level C# source for code type, plain English for instructions type)
7. Register the script with `create_script`
8. If compilation fails, fix the errors and re-register
9. Once registered, call `inspect_script` to confirm and show the result to the user
