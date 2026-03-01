# C# Code Patterns for ScriptMCP Dynamic Functions

## Environment

- **.NET 9 / C# 13** runtime
- Method signature: `public static string Run(Dictionary<string, string> args)`
- Write only the method body — return a string
- NOT async — use `.Result` or `.GetAwaiter().GetResult()`
- All `System.*` assemblies available; no NuGet packages

## Auto-Included Usings

```
System, System.Collections.Generic, System.Globalization, System.IO,
System.Linq, System.Net, System.Net.Http, System.Text,
System.Text.RegularExpressions, System.Threading.Tasks
```

For anything else, use fully qualified names (e.g., `System.Diagnostics.Process`).

## HTTP Patterns

### Simple GET

```csharp
var client = new HttpClient();
client.DefaultRequestHeaders.Add("User-Agent", "ScriptMCP");
return client.GetStringAsync(url).Result;
```

### GET with Headers

```csharp
var client = new HttpClient();
client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
client.DefaultRequestHeaders.Add("Accept", "application/json");
var response = client.GetStringAsync(url).Result;
return response;
```

### POST with JSON Body

```csharp
var client = new HttpClient();
var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
var response = client.PostAsync(url, content).Result;
return response.Content.ReadAsStringAsync().Result;
```

### Download File

```csharp
var client = new HttpClient();
var bytes = client.GetByteArrayAsync(url).Result;
File.WriteAllBytes(outputPath, bytes);
return $"Downloaded {bytes.Length} bytes to {outputPath}";
```

## JSON Patterns

### Parse and Extract

```csharp
var doc = System.Text.Json.JsonDocument.Parse(jsonString);
var root = doc.RootElement;
var name = root.GetProperty("name").GetString();
var count = root.GetProperty("count").GetInt32();
return $"Name: {name}, Count: {count}";
```

### Build JSON

```csharp
using System.Text.Json;
var options = new JsonSerializerOptions { WriteIndented = true };
var obj = new Dictionary<string, object> {
    ["name"] = name,
    ["value"] = int.Parse(value)
};
return JsonSerializer.Serialize(obj, options);
```

### Iterate JSON Array

```csharp
var doc = System.Text.Json.JsonDocument.Parse(jsonString);
var sb = new StringBuilder();
foreach (var item in doc.RootElement.EnumerateArray())
{
    sb.AppendLine(item.GetProperty("name").GetString());
}
return sb.ToString();
```

## File I/O Patterns

### Read and Process Lines

```csharp
var lines = File.ReadAllLines(path);
var filtered = lines.Where(l => l.Contains(keyword)).ToArray();
return $"Found {filtered.Length} matching lines:\n{string.Join("\n", filtered)}";
```

### Write Output

```csharp
var result = ProcessData(input);
File.WriteAllText(outputPath, result);
return $"Written to {outputPath}";
```

### CSV Processing

```csharp
var lines = File.ReadAllLines(path);
var header = lines[0].Split(',');
var sb = new StringBuilder();
sb.AppendLine(string.Join(" | ", header));
sb.AppendLine(new string('-', 40));
foreach (var line in lines.Skip(1))
{
    sb.AppendLine(string.Join(" | ", line.Split(',')));
}
return sb.ToString();
```

## Process Execution Patterns

### Run Shell Command

```csharp
var psi = new System.Diagnostics.ProcessStartInfo {
    FileName = "cmd",
    Arguments = $"/c {command}",
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    UseShellExecute = false,
    CreateNoWindow = true
};
var proc = System.Diagnostics.Process.Start(psi);
var stdout = proc.StandardOutput.ReadToEnd();
var stderr = proc.StandardError.ReadToEnd();
proc.WaitForExit();
if (proc.ExitCode != 0) return $"Error (exit {proc.ExitCode}): {stderr}";
return stdout;
```

### Run PowerShell

```csharp
var psi = new System.Diagnostics.ProcessStartInfo {
    FileName = "powershell",
    Arguments = $"-NoProfile -Command \"{script}\"",
    RedirectStandardOutput = true,
    UseShellExecute = false,
    CreateNoWindow = true
};
var proc = System.Diagnostics.Process.Start(psi);
var output = proc.StandardOutput.ReadToEnd();
proc.WaitForExit();
return output;
```

## String and Text Patterns

### Regex Extraction

```csharp
var matches = Regex.Matches(input, pattern);
var sb = new StringBuilder();
foreach (Match m in matches)
{
    sb.AppendLine(m.Value);
}
return sb.ToString();
```

### String Building with Formatting

```csharp
var sb = new StringBuilder();
sb.AppendLine($"| {"Name",-20} | {"Value",-15} |");
sb.AppendLine($"|{new string('-', 22)}|{new string('-', 17)}|");
foreach (var item in items)
{
    sb.AppendLine($"| {item.Key,-20} | {item.Value,-15} |");
}
return sb.ToString();
```

## Date and Time Patterns

```csharp
var now = DateTime.Now;
var utc = DateTime.UtcNow;
return $"Local: {now:yyyy-MM-dd HH:mm:ss}\nUTC: {utc:yyyy-MM-dd HH:mm:ss}";
```

## Inter-Function Calling

### Synchronous Call

```csharp
// Call another dynamic function and use its result
var result = ScriptMCP.Call("other_function", "{\"param\": \"value\"}");
return $"Other function returned: {result}";
```

### Parallel Execution

```csharp
// Launch multiple functions in parallel
var proc1 = ScriptMCP.Proc("func_a", "{}");
var proc2 = ScriptMCP.Proc("func_b", "{}");
var output1 = proc1.StandardOutput.ReadToEnd();
var output2 = proc2.StandardOutput.ReadToEnd();
proc1.WaitForExit();
proc2.WaitForExit();
return $"A: {output1}\nB: {output2}";
```

## Scheduling & Shared Output

### Create a Scheduled Task (Native Tool)

Call `create_scheduled_task` directly — it is a native MCP tool, not a dynamic function:

- `function_name`: name of the dynamic function to run
- `function_args`: JSON arguments (default `"{}"`)
- `interval_minutes`: recurrence interval

The task runs via `--exec_out`, which appends results to `exec_output.jsonl`.

### Read Exec Output (Native Tool)

Call `read_shared_memory` directly to read results written by scheduled tasks or `--exec_out`:

- No `func` parameter: returns all entries with size header
- With `func` parameter: returns the `out` field of the most recent matching entry

### Writing Functions for Scheduled Use

Functions intended for scheduled execution should be self-contained and return meaningful output, since the result is captured to the output file:

```csharp
// Good: returns a meaningful result string
var client = new HttpClient();
var price = client.GetStringAsync("https://api.example.com/price").Result;
return $"Price: {price}";
```

## Error Handling

### Standard Pattern

```csharp
try {
    // risky operation
    var result = DoSomething();
    return result;
} catch (HttpRequestException ex) {
    return $"HTTP Error: {ex.Message}";
} catch (FileNotFoundException ex) {
    return $"File not found: {ex.FileName}";
} catch (Exception ex) {
    return $"Error: {ex.GetType().Name}: {ex.Message}";
}
```

### Timeout Pattern

```csharp
try {
    var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
    var client = new HttpClient();
    var response = client.GetStringAsync(url, cts.Token).Result;
    return response;
} catch (TaskCanceledException) {
    return "Error: Request timed out after 30 seconds";
}
```

## Parameter Type Examples

### Registration with Typed Parameters

```json
[
  {"name": "url", "type": "string", "description": "Target URL"},
  {"name": "count", "type": "int", "description": "Number of retries"},
  {"name": "timeout", "type": "double", "description": "Timeout in seconds"},
  {"name": "verbose", "type": "bool", "description": "Enable verbose output"}
]
```

Parameters are auto-parsed and available as local variables matching their declared types.
