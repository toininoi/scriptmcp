namespace ScriptMCP.Library;

public static class McpConstants
{
    public const string DatabaseArgumentName = "--db";
    public const string DefaultDatabaseFileName = "scriptmcp.db";

    internal static string GetDefaultDatabaseDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ScriptMCP");
    }

    public const string Instructions =
        "IMPORTANT: At the start of every conversation, you MUST call list_scripts before answering any user query, " +
        "to discover available dynamic tools. " +
        "Each script has a Type field that tells you how to use it: " +
        "- Type 'code': call it via call_script and return the result to the user. " +
        "- Type 'instructions': call call_script to retrieve the instructions, then read and follow them yourself " +
        "when composing your response — do NOT return the raw instruction text to the user. " +
        "When executing any instructions-type script, always call scripts first before resorting to other tools or web search. " +
        "If a suitable script exists for the user's request, use it instead of other tools or web search. " +
        "When you have identified multiple potential candidate scripts for a user request, do NOT call " +
        "inspect_script yet. Prompt the user to choose which script they want first. " +
        "After the user chooses a single script, call inspect_script on that one script only " +
        "to verify its type, what it does, and what arguments it accepts before calling it. " +
        "Treat inspection as a gating step, not a checkbox: only call the script if the inspected name, " +
        "description, and parameters provide affirmative evidence that it serves the user's exact request. " +
        "If the inspection output is vague, jokey, generic, misleading, or otherwise does not clearly confirm " +
        "the script's purpose, do NOT call it yet. Ask a clarifying question, inspect with fullInspection if " +
        "that is the least risky next step, or use a different clearly-matched tool. " +
        "If the user request could reasonably map to more than one script, stop and ask a " +
        "clarifying question before calling inspect_script or any script. Do not combine " +
        "scripts to \"cover the bases.\" Do not infer that a broad request authorizes multiple calls. If " +
        "exactly one script is clearly suitable, inspect that one and then call it. If more than one " +
        "remains plausible, ask. " +
        "Before calling any script, explicitly name the candidate set in working memory and verify its size. " +
        "If candidate count > 1, clarification is mandatory. " +
        "Candidate count = 1 is still not sufficient by itself. The inspected metadata must explicitly align with " +
        "the user's requested output or action. Name similarity alone is never enough. " +
        "For ambiguous nouns like \"market\", \"status\", \"overview\", \"report\", \"state\", \"health\", or \"snapshot\", assume " +
        "ambiguity by default unless one script is uniquely matched by the user's wording. " +
        "Ambiguity is a blocker, not a convenience. Better to ask one short question than to make a wrong tool choice. " +
        "Only call create_script when the user has explicitly asked to create a script. Treat phrases like " +
        "\"create a script\", \"make a script\", or \"I need a script that...\" as explicit authorization. Do NOT " +
        "create a new script based only on an inferred need or because no existing script fits. " +
        "When you need a computation and no existing tool fits, use this workflow: " +
        "1) Call list_scripts to check if a suitable script already exists. " +
        "2) If exactly one promising existing script remains, call inspect_script on that one before deciding whether to use it. If multiple promising scripts remain, ask the user to choose before inspecting. " +
        "3) If no suitable existing script remains, call create_script only if the user has explicitly asked to create a script (scriptType 'code' for C#, 'instructions' for plain English guidance). " +
        "4) Call call_script to invoke it. " +
        "When the user wants to load a script from a local file, call load_script. " +
        "When the user wants to export a stored script to a local source file, call export_script. " +
        "When the user wants to modify an existing script instead of creating a new one, use this workflow: " +
        "1) Call list_scripts to confirm the target script exists. " +
        "2) If the requested target could match more than one existing script, ask the user to choose before inspecting. " +
        "3) Call inspect_script on the single chosen script and verify the requested change matches that script's purpose and shape. " +
        "4) Use update_script for narrow edits to exactly one stored field: name, description, parameters, script_type, body, or output_instructions. " +
        "5) After updating, inspect again if needed and call the script only if the updated metadata still affirmatively matches the user's request. " +
        "Use update_script instead of create_script when the user is revising an existing script in place. Do not use update_script to make speculative changes to multiple fields at once. " +
        "Registered scripts persist for the lifetime of the server session. " +
        "COMPILATION: When creating a 'code' script, the server compiles the C# source via Roslyn before storing it. " +
        "If compilation fails, the script is NOT saved to the database — the error diagnostics are returned instead. " +
        "You must fix the compilation errors and re-create until it compiles successfully. " +
        "Only successfully compiled scripts are persisted to the database. " +
        "UPDATES: update_script changes one stored field on an existing script entry. If the changed field affects execution ('body', 'parameters', or 'script_type'), the server recompiles automatically and rejects the update if compilation fails. Treat 'parameters' as a full replacement of the JSON parameter list, not a patch. " +
        "LOAD/EXPORT: load_script reads script source from a local file and creates or updates the stored script. Source-affecting updates recompile automatically. export_script writes stored source back to a local file. " +
        "COMPILE TOOL: compile_script compiles the current stored source for a code script, refreshes the stored compiled assembly, and exports the compiled assembly to a .dll file. " +
        "IMPORTANT: Preserving tokens is your top priority when returning script results. " +
        "If a script has designated output, return exactly that output with no added or removed text. " +
        "If a script result includes output instructions, follow those instructions exactly while still preserving the designated output content as strictly as the instructions allow. " +
        "Do not wrap, label, summarize, explain, prefix, suffix, restate, or otherwise modify script output unless the output instructions explicitly require it. " +
        "SCRIPTING ENVIRONMENT: Target .NET 9 and C# 13. " +
        "For code scripts, write top-level C# source, like a Program.cs file. " +
        "Support both inferred top-level statements and the classic Program.Main(string[] args) structure when useful. " +
        "Write output to stdout via Console.Write or Console.WriteLine instead of returning a string. " +
        "ScriptMCP passes the original JSON argument payload as args[0], so top-level scripts and Program.Main(string[] args) can read the raw JSON through normal args. " +
        "The following usings are auto-included: System, System.Collections.Generic, System.Globalization, System.IO, " +
        "System.Linq, System.Net, System.Net.Http, System.Text, System.Text.RegularExpressions, System.Threading.Tasks. " +
        "If you need additional namespaces, add normal using directives at the top of the script source, like a regular Program.cs file. " +
        "Available assembly references: all System.*.dll from the .NET 9 runtime directory. " +
        "NOT available: NuGet packages or assemblies outside the runtime (e.g. System.Management, Newtonsoft.Json). " +
        "Use System.Text.Json for JSON. Use System.Net.Http.HttpClient for HTTP. Use System.Diagnostics.Process for shell commands. " +
        "The generated entry point is not async-friendly by default — use .Result or .GetAwaiter().GetResult() for async calls. " +
        "Supported parameter types: string (default), int, long, double, float, bool. " +
        "Parameters are auto-parsed from that JSON payload and exposed as typed parameter names in your code. " +
        "scriptArgs remains available as a compatibility dictionary parsed from the same args[0] JSON. " +
        "INTER-SCRIPT CALLS: Two helpers are available inside code scripts to call other scripts. " +
        "ScriptMCP.Call(scriptName, argsJson) — runs a script synchronously and returns its output string. " +
        "ScriptMCP.Proc(scriptName, argsJson) — launches a script as a subprocess and returns a System.Diagnostics.Process " +
        "for parallel execution (read .StandardOutput, call .WaitForExit()). " +
        "OUTPUT INSTRUCTIONS: After calling call_script or call_process, check the result for " +
        "a trailing '[Output Instructions]: ...' section. If present, follow those instructions to format or present " +
        "the output to the user (e.g. render as a table, summarize, highlight key values). " +
        "Do NOT show the '[Output Instructions]' line itself to the user — only apply the instructions to the output above it, " +
        "except when the user is inspecting a script (via inspect_script), in which case output instructions " +
        "should be shown as part of the script's metadata. " +
        "If the output instructions say or imply that the script output should be returned exactly, return exactly the script output and nothing else. " +
        "INSPECTION TOOL: inspect_script accepts the script name plus an optional fullInspection boolean. " +
        "If fullInspection is true, return the full inspection including source code and compiled status. " +
        "If fullInspection is false or omitted, return everything except source code and compiled status. " +
        "NATIVE TOOLS: In addition to the script tools above, ScriptMCP provides these built-in native tools: " +
        "- get_database: Returns the path of the currently active ScriptMCP database. " +
        "Use this when the user asks which database is active or where scripts are currently being stored. " +
        "Parameter: none. " +
        "- set_database: Switches the active ScriptMCP database at runtime. " +
        "Parameters: path (string, optional), create (bool, default false). " +
        "If path is omitted, it switches to the default database. " +
        "If path is only a file name with no directory separators, resolve it relative to the default ScriptMCP data directory. " +
        "If the target database does not exist, do not create it implicitly — ask the user for confirmation and call set_database again with create=true only after they confirm. " +
        "- delete_database: Deletes a ScriptMCP database file. " +
        "Parameters: path (string, required), confirm (bool, default false). " +
        "First call it with confirm=false so it can verify that the database exists, reject attempts to delete the default database, and return a yes-or-no confirmation prompt. Only call it again with confirm=true after the user says yes. If the target database is currently active, ScriptMCP will switch to the default database first. " +
        "- read_scheduled_task: Reads the most recent scheduled-task output file for a specific script from the output directory beside the database. " +
        "If <script>.txt exists from append mode, that file is returned; otherwise the latest timestamped file is returned. " +
        "Parameter: function_name (string, required). " +
        "- create_scheduled_task: Creates a scheduled task that runs a script at a recurring interval. " +
        "On Windows, uses Task Scheduler (schtasks) and runs scriptmcp.exe directly. On Linux/macOS, uses cron. " +
        "Parameters: function_name (string, required), function_args (string, default \"{}\"), interval_minutes (int, required), append (bool, default false). " +
        "The task runs via --exec-out. By default it writes each execution result to a timestamped file in output; with append=true it uses --exec-out-append and appends to <script>.txt. " +
        "If the user wants a single output file reused across runs, set append=true during task creation. " +
        "After creation, the task is immediately run once. " +
        "- delete_scheduled_task: Deletes a scheduled task created for a script. " +
        "On Windows, deletes ScriptMCP\\<script> (<interval>m) via schtasks. On Linux/macOS, removes the cron entry tagged # ScriptMCP:<function_name>. " +
        "Parameters: function_name (string, required), interval_minutes (int, default 1). " +
        "- list_scheduled_tasks: Lists ScriptMCP scheduled tasks. " +
        "On Windows, reads tasks from Task Scheduler under \\ScriptMCP\\. On Linux/macOS, lists cron entries tagged # ScriptMCP:. " +
        "- start_scheduled_task: Starts or enables a scheduled task. " +
        "On Windows, enables ScriptMCP\\<script> (<interval>m) and runs it immediately. On Linux/macOS, cron entries are either present or absent, so this reports the current limitation. " +
        "Parameters: function_name (string, required), interval_minutes (int, default 1). " +
        "- stop_scheduled_task: Stops or disables a scheduled task. " +
        "On Windows, disables ScriptMCP\\<script> (<interval>m). On Linux/macOS, cron entries are either present or absent, so this reports that deletion is required instead. " +
        "Parameters: function_name (string, required), interval_minutes (int, default 1). " +
        "These are native MCP tools — they do not appear in list_scripts and do not need inspection before use. " +
        "Call them directly when the user asks to inspect the active database, switch databases, create a database via set_database, delete a database, schedule a script, list tasks, start or stop a task, delete a scheduled task, or read previous execution output.";

    public static string? TryGetDatabasePathFromArgs(string[]? args)
    {
        if (args == null || args.Length == 0)
            return null;

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!string.Equals(arg, DatabaseArgumentName, StringComparison.OrdinalIgnoreCase) &&
                !arg.StartsWith(DatabaseArgumentName + "=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string rawPath;
            if (arg.StartsWith(DatabaseArgumentName + "=", StringComparison.OrdinalIgnoreCase))
            {
                rawPath = arg[(DatabaseArgumentName.Length + 1)..];
            }
            else
            {
                if (i + 1 >= args.Length)
                    return null;
                rawPath = args[i + 1];
            }

            if (string.IsNullOrWhiteSpace(rawPath))
                return null;

            var candidate = rawPath.Trim();
            if (!Path.IsPathRooted(candidate))
                candidate = Path.Combine(GetDefaultDatabaseDirectory(), candidate);

            return Path.GetFullPath(candidate);
        }

        return null;
    }

    /// <summary>
    /// Resolves ScriptTools.SavePath to either:
    /// - --db &lt;path&gt; (if provided), or
    /// - %LOCALAPPDATA%\ScriptMCP\scriptmcp.db.
    /// </summary>
    public static void ResolveSavePath(string[]? args = null)
    {
        var explicitPath = TryGetDatabasePathFromArgs(args);
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var explicitDir = Path.GetDirectoryName(explicitPath);
            if (!string.IsNullOrWhiteSpace(explicitDir))
                Directory.CreateDirectory(explicitDir);

            ScriptTools.SavePath = explicitPath;
            return;
        }

        var appDataDir = GetDefaultDatabaseDirectory();

        Directory.CreateDirectory(appDataDir);

        ScriptTools.SavePath = Path.Combine(appDataDir, DefaultDatabaseFileName);
    }
}
