using System.Text;
using CopilotMedallion.Api.Models;

namespace CopilotMedallion.Api.Services;

public class SpecGenerator
{
    private readonly string _templatePath;

    public SpecGenerator(IWebHostEnvironment env)
    {
        _templatePath = Path.Combine(env.ContentRootPath, "specs", "template.md");
        if (!File.Exists(_templatePath))
        {
            var alt = Path.Combine(AppContext.BaseDirectory, "specs", "template.md");
            if (File.Exists(alt)) _templatePath = alt;
        }
    }

    public string Generate(string runId, LakehouseInfo source, List<string> tables, string targetLakehouseName, string workspaceId)
    {
        string template;
        if (File.Exists(_templatePath))
        {
            template = File.ReadAllText(_templatePath);
        }
        else
        {
            template = BuiltinTemplate;
        }
        var nowUtc = DateTime.UtcNow.ToString("u");
        var tableList = string.Join("\n", tables.Select(t => $"- `{t}`"));
        return template
            .Replace("{{RUN_ID}}", runId)
            .Replace("{{TIMESTAMP_UTC}}", nowUtc)
            .Replace("{{WORKSPACE_ID}}", workspaceId)
            .Replace("{{SOURCE_LAKEHOUSE_ID}}", source.Id)
            .Replace("{{SOURCE_LAKEHOUSE_NAME}}", source.DisplayName)
            .Replace("{{TARGET_LAKEHOUSE_NAME}}", targetLakehouseName)
            .Replace("{{TABLES_LIST}}", tableList)
            .Replace("{{TABLES_CSV}}", string.Join(",", tables));
    }

    private const string BuiltinTemplate = @"# Run Spec {{RUN_ID}}

Generated at {{TIMESTAMP_UTC}}.

## Inputs
- Workspace: `{{WORKSPACE_ID}}`
- Source Lakehouse: **{{SOURCE_LAKEHOUSE_NAME}}** (`{{SOURCE_LAKEHOUSE_ID}}`)
- Tables to ingest into Bronze:
{{TABLES_LIST}}
- Target Lakehouse: **{{TARGET_LAKEHOUSE_NAME}}**

## Build plan
1. Create Lakehouse `{{TARGET_LAKEHOUSE_NAME}}` in the same workspace.
2. Bronze: copy each source table to `bronze.<table>` (Delta).
3. Silver: typed/clean copies into `silver.<table>` with column standardisation.
4. Gold: dim/fact star schema when source schema matches a known pattern (AdventureWorksLT).
5. (Optional) Power BI report when Gold matches the AdventureWorksLT shape.
";
}
