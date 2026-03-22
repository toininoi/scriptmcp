using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Server;

namespace ScriptMCP.Library;

[McpServerResourceType]
public class ScriptResources
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private static string ConnectionString => $"Data Source={ScriptTools.SavePath}";

    [McpServerResource(
        Name = "scripts_catalog",
        Title = "Scripts Catalog",
        MimeType = "application/json",
        UriTemplate = "scriptmcp://scripts")]
    [Description("Lists all registered scripts with their descriptions, types, and parameter metadata.")]
    public string GetScriptsCatalog()
    {
        EnsureInitialized();

        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT name, description, parameters, script_type, output_instructions
            FROM scripts
            ORDER BY name
            """;

        using var reader = cmd.ExecuteReader();
        var scripts = new List<object>();

        while (reader.Read())
        {
            var parametersJson = reader.GetString(2);
            var parameters = JsonSerializer.Deserialize<List<DynParam>>(parametersJson)
                ?? new List<DynParam>();

            scripts.Add(new
            {
                name = reader.GetString(0),
                description = reader.GetString(1),
                scriptType = reader.GetString(3),
                parameters,
                outputInstructions = reader.IsDBNull(4) ? null : reader.GetString(4),
            });
        }

        return JsonSerializer.Serialize(new
        {
            resource = "scriptmcp://scripts",
            count = scripts.Count,
            scripts,
        }, JsonOptions);
    }

    [McpServerResource(
        Name = "script_details",
        Title = "Script Details",
        MimeType = "text/plain",
        UriTemplate = "scriptmcp://scripts/{name}")]
    [Description("Returns the inspection view for a single registered script.")]
    public string GetScriptDetails(
        [Description("The script name to inspect")] string name)
    {
        EnsureInitialized();
        return new ScriptTools().InspectScript(name);
    }

    private static void EnsureInitialized()
    {
        _ = new ScriptTools();
    }
}
