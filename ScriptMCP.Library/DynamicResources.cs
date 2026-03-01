using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Server;

namespace ScriptMCP.Library;

[McpServerResourceType]
public class DynamicResources
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private static string ConnectionString => $"Data Source={DynamicTools.SavePath}";

    [McpServerResource(
        Name = "dynamic_functions_catalog",
        Title = "Dynamic Functions Catalog",
        MimeType = "application/json",
        UriTemplate = "scriptmcp://functions")]
    [Description("Lists all registered dynamic functions with their descriptions, types, and parameter metadata.")]
    public string GetDynamicFunctionsCatalog()
    {
        EnsureInitialized();

        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT name, description, parameters, function_type, output_instructions
            FROM functions
            ORDER BY name
            """;

        using var reader = cmd.ExecuteReader();
        var functions = new List<object>();

        while (reader.Read())
        {
            var parametersJson = reader.GetString(2);
            var parameters = JsonSerializer.Deserialize<List<DynParam>>(parametersJson)
                ?? new List<DynParam>();

            functions.Add(new
            {
                name = reader.GetString(0),
                description = reader.GetString(1),
                functionType = reader.GetString(3),
                parameters,
                outputInstructions = reader.IsDBNull(4) ? null : reader.GetString(4),
            });
        }

        return JsonSerializer.Serialize(new
        {
            resource = "scriptmcp://functions",
            count = functions.Count,
            functions,
        }, JsonOptions);
    }

    [McpServerResource(
        Name = "dynamic_function_details",
        Title = "Dynamic Function Details",
        MimeType = "text/plain",
        UriTemplate = "scriptmcp://functions/{name}")]
    [Description("Returns the inspection view for a single registered dynamic function.")]
    public string GetDynamicFunctionDetails(
        [Description("The dynamic function name to inspect")] string name)
    {
        EnsureInitialized();
        return new DynamicTools().InspectDynamicFunction(name);
    }

    private static void EnsureInitialized()
    {
        _ = new DynamicTools();
    }
}
