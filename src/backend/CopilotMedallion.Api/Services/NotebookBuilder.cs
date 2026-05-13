using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CopilotMedallion.Api.Services;

/// <summary>
/// Given a run-spec markdown + concrete parameters, asks the LLM to produce a list of
/// PySpark notebook cells implementing the spec. Falls back to the static medallion
/// template when the LLM is unavailable or returns nothing usable.
/// </summary>
public class NotebookBuilder
{
    private readonly LlmService _llm;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<NotebookBuilder> _log;

    public NotebookBuilder(LlmService llm, IHttpClientFactory http, ILogger<NotebookBuilder> log)
    {
        _llm = llm; _http = http; _log = log;
    }

    public async Task<string?> FetchSpecMarkdownAsync(string? specRawUrl)
    {
        if (string.IsNullOrWhiteSpace(specRawUrl) || !specRawUrl.StartsWith("http")) return null;
        try
        {
            var c = _http.CreateClient();
            c.Timeout = TimeSpan.FromSeconds(30);
            var md = await c.GetStringAsync(specRawUrl);
            return md;
        }
        catch (Exception ex) { _log.LogWarning(ex, "Could not fetch spec markdown"); return null; }
    }

    public async Task<List<string>?> GenerateCellsFromSpecAsync(
        string specMarkdown,
        string workspaceId, string sourceLakehouseId, string targetLakehouseId,
        string targetLakehouseName, List<string> sourceTables, string runId)
    {
        if (!_llm.Configured) return null;

        var system = @"You are an expert Microsoft Fabric data engineer. You write PySpark notebook code that runs inside a Fabric notebook (synapse_pyspark kernel) to build a medallion architecture exactly as described in a user-provided spec.

OUTPUT FORMAT (STRICT):
Return ONLY a JSON object with shape: {""cells"": [""<python source for cell 1>"", ""<python source for cell 2>"", ...]}
No prose, no markdown fences, just the JSON.

CODE REQUIREMENTS:
- Each cell is one self-contained block of valid Python.
- Use abfss paths to OneLake:
    src_base = f""abfss://{workspace_id}@onelake.dfs.fabric.microsoft.com/{source_lakehouse_id}""
    tgt_base = f""abfss://{workspace_id}@onelake.dfs.fabric.microsoft.com/{target_lakehouse_id}""
- Source tables live at:  f""{src_base}/Tables/{table_relative_path}""
  where table_relative_path is one of the provided strings (may contain '/').
- Write Bronze tables to: f""{tgt_base}/Tables/bronze_<flat>""  (flat = table_relative_path.replace('/','_').lower())
- Write Silver tables to: f""{tgt_base}/Tables/silver_<flat>""
- Write Gold tables (dim_*/fact_*) to: f""{tgt_base}/Tables/<gold_name>""
- Always use spark.read.format('delta').load(...) and DataFrame.write.format('delta').mode('overwrite').option('overwriteSchema','true').save(...).
- The FIRST cell will be supplied by the platform and defines these variables: workspace_id, source_lakehouse_id, target_lakehouse_id, target_lakehouse_name, source_tables_csv, run_id, spec_url. DO NOT redefine them; use them.
- Add a final cell that prints a JSON summary on one line: print(json.dumps({'run_id': run_id, 'status': 'ok', 'bronze': bronze_results, 'silver': silver_results, 'gold': gold_results}, default=str))
- Wrap each major step (bronze loop, silver loop, gold) in try/except that logs to print() and re-raises only after collecting partial results.
- Use the tables listed in source_tables_csv. Split on ','.
- If the spec asks for AdventureWorksLT-shaped gold, look for tables whose last path-segment matches (case-insensitive) Customer, Product, SalesOrderHeader, SalesOrderDetail and build dim_customer, dim_product, dim_date, fact_sales accordingly.
- Keep code defensive: trim strings, drop fully-null columns in Silver, add _silver_ts audit column.
- NEVER use credentials or hardcoded secrets. Trust the notebook's runtime identity for OneLake access.
- The total code should fit within reasonable bounds (~10 cells, ~150 lines).";

        var user = $@"## Run parameters
workspace_id = {workspaceId}
source_lakehouse_id = {sourceLakehouseId}
target_lakehouse_id = {targetLakehouseId}
target_lakehouse_name = {targetLakehouseName}
run_id = {runId}
source_tables_csv = {string.Join(",", sourceTables)}

## Spec (markdown)
{specMarkdown}

Produce the JSON now.";

        string answer;
        try { answer = await _llm.ChatAsync(system, user, maxTokens: 6000, temperature: 0.1); }
        catch (Exception ex) { _log.LogWarning(ex, "LLM cell-gen failed"); return null; }

        var json = ExtractJson(answer);
        if (json is null) { _log.LogWarning("Could not find JSON in LLM response"); return null; }
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("cells", out var arr)) return null;
            var cells = new List<string>();
            foreach (var c in arr.EnumerateArray())
                if (c.ValueKind == JsonValueKind.String) cells.Add(c.GetString() ?? "");
            return cells.Count == 0 ? null : cells;
        }
        catch (Exception ex) { _log.LogWarning(ex, "LLM JSON parse failed"); return null; }
    }

    private static string? ExtractJson(string s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        s = s.Trim();
        // strip code fences
        var m = Regex.Match(s, @"```(?:json)?\s*([\s\S]*?)```");
        if (m.Success) s = m.Groups[1].Value.Trim();
        // find first '{' and last '}'
        var i = s.IndexOf('{'); var j = s.LastIndexOf('}');
        if (i >= 0 && j > i) return s.Substring(i, j - i + 1);
        return null;
    }

    /// <summary>
    /// Build a complete .ipynb (JSON string) from a list of code cells.
    /// Always prepends a "parameters" cell with the seven standard variables.
    /// </summary>
    public string BuildNotebookJson(List<string> codeCells)
    {
        var cells = new List<object>();
        // Markdown header
        cells.Add(new
        {
            cell_type = "markdown",
            metadata = new { },
            source = new[] { "# Copilot Medallion (LLM-generated from spec)\n\nGenerated by copilot.roesli.org." }
        });
        // Parameters cell (Fabric Job Scheduler injects values here)
        cells.Add(new
        {
            cell_type = "code",
            execution_count = (int?)null,
            metadata = new { tags = new[] { "parameters" } },
            outputs = Array.Empty<object>(),
            source = new[] {
                "# Parameters injected by the Job Scheduler API\n",
                "source_lakehouse_id = \"\"\n",
                "source_tables_csv = \"\"\n",
                "target_lakehouse_id = \"\"\n",
                "target_lakehouse_name = \"\"\n",
                "workspace_id = \"\"\n",
                "run_id = \"\"\n",
                "spec_url = \"\"\n"
            }
        });
        // Imports + base paths
        cells.Add(new
        {
            cell_type = "code",
            execution_count = (int?)null,
            metadata = new { },
            outputs = Array.Empty<object>(),
            source = new[] {
                "import json, traceback\n",
                "from datetime import datetime\n",
                "from pyspark.sql import functions as F\n",
                "from pyspark.sql.utils import AnalysisException\n",
                "src_base = f\"abfss://{workspace_id}@onelake.dfs.fabric.microsoft.com/{source_lakehouse_id}\"\n",
                "tgt_base = f\"abfss://{workspace_id}@onelake.dfs.fabric.microsoft.com/{target_lakehouse_id}\"\n",
                "source_tables = [t.strip() for t in source_tables_csv.split(',') if t.strip()]\n",
                "print(f\"run_id={run_id} src_base={src_base} tgt_base={tgt_base} tables={source_tables}\")\n"
            }
        });
        // Generated cells
        foreach (var src in codeCells)
        {
            cells.Add(new
            {
                cell_type = "code",
                execution_count = (int?)null,
                metadata = new { },
                outputs = Array.Empty<object>(),
                source = SplitLines(src)
            });
        }
        var nb = new
        {
            cells,
            metadata = new
            {
                kernelspec = new { display_name = "synapse_pyspark", language = "python", name = "synapse_pyspark" },
                language_info = new { name = "python" },
                microsoft = new { language = "python" }
            },
            nbformat = 4,
            nbformat_minor = 5
        };
        return JsonSerializer.Serialize(nb);
    }

    private static string[] SplitLines(string src)
    {
        // ipynb expects each line to keep its trailing newline (except last).
        if (string.IsNullOrEmpty(src)) return new[] { "" };
        var lines = src.Replace("\r\n", "\n").Split('\n');
        var result = new string[lines.Length];
        for (int i = 0; i < lines.Length; i++)
            result[i] = i < lines.Length - 1 ? lines[i] + "\n" : lines[i];
        return result;
    }
}
