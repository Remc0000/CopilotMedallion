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

        var system = @"You are an expert Microsoft Fabric data engineer following the Microsoft Fabric e2e-medallion-architecture best practices. You generate PySpark notebook cells that run inside a Fabric notebook (synapse_pyspark kernel) and build a complete Bronze → Silver → Gold medallion implementation of the user's spec.

OUTPUT FORMAT (STRICT):
Return ONLY a JSON object: {""cells"": [""<python source for cell 1>"", ""<python source for cell 2>"", ...]}.
No prose, no markdown fences — only the JSON.

PLATFORM CONSTRAINTS:
- The first cell of the notebook will be provided by the platform with these variables ALREADY defined: workspace_id, source_lakehouse_id, target_lakehouse_id, target_lakehouse_name, source_tables_csv (comma-separated relative table paths under Tables/), run_id, spec_url. You MUST NOT redefine them — use them as-is.
- Spark abfss paths:
    src_base = f""abfss://{workspace_id}@onelake.dfs.fabric.microsoft.com/{source_lakehouse_id}""
    tgt_base = f""abfss://{workspace_id}@onelake.dfs.fabric.microsoft.com/{target_lakehouse_id}""
- Source tables live at: f""{src_base}/Tables/{table_relative_path}"" where table_relative_path is one of the values in source_tables_csv (may contain '/').
- Write Bronze tables to:  f""{tgt_base}/Tables/bronze_<flat>"" (flat = table_relative_path.replace('/','_').lower())
- Write Silver tables to:  f""{tgt_base}/Tables/silver_<flat>""
- Write Gold tables to:    f""{tgt_base}/Tables/<gold_name>"" (e.g. dim_customer, fact_sales)
- Always use spark.read.format('delta').load(...) and DataFrame.write.format('delta')...save(...).
- Never use credentials/secrets — trust the notebook runtime identity for OneLake access.
- Output a single line of JSON to stdout at the end (see SUMMARY rule).

MEDALLION RULES (apply ALL of these — they come from the official Microsoft Fabric e2e-medallion-architecture skill):

Bronze (write-heavy, append):
- For each table from source_tables_csv: read from src_base, add metadata columns ingestion_timestamp (current_timestamp), source_path (literal abfss source path), batch_id (literal run_id).
- Write to target with mode='overwrite' (this is a re-buildable pipeline), option('overwriteSchema','true'), partitioned by ingestion_date when present, otherwise unpartitioned.
- Track row counts in a dict bronze_results.

Silver (balanced):
- Read each bronze table, drop fully-null columns, trim string columns, deduplicate on natural key when discoverable (else dedupe by all columns), add _silver_ts audit column.
- Use snake_case column names (rename CamelCase → snake_case).
- Write with mode='overwrite' + overwriteSchema=true.
- After write: spark.sql(f""OPTIMIZE delta.`{silver_path}`"") inside try/except.

Gold (read-heavy, analytics-ready):
- BEFORE any Gold write, set these Spark configs (required by the skill):
    spark.conf.set('spark.sql.parquet.vorder.default','true')
    spark.conf.set('spark.databricks.delta.optimizeWrite.enabled','true')
    spark.conf.set('spark.databricks.delta.optimizeWrite.binSize','1g')
- Detect AdventureWorksLT-shaped sources by checking last-segment names against {customer, product, salesorderheader, salesorderdetail}. If all four are present, build:
    dim_customer (from silver_customer), dim_product (from silver_product), dim_date (distinct OrderDate with Year/Quarter/Month/Day), fact_sales (join SalesOrderDetail × SalesOrderHeader on SalesOrderID → CustomerKey/ProductKey/DateKey/Qty/UnitPrice/LineTotal).
- Otherwise build sensible summary tables based on what the spec asks for (use your judgment from the spec markdown).
- After each Gold write: try OPTIMIZE delta and ZORDER BY the most likely filter columns (date/key columns) inside try/except.
- Track gold_results dict.

Robustness:
- Wrap each major step (bronze loop, silver loop, gold construction) in try/except. On exception, log via print() + traceback.format_exc(), then re-raise to fail the job.
- Inside loops, per-table errors should be captured into the results dict as ""ERROR: ..."" but should not abort the entire layer.

Final cell: print one line of JSON:
    import json
    print(json.dumps({'run_id': run_id, 'workspace_id': workspace_id, 'target_lakehouse_id': target_lakehouse_id, 'bronze': bronze_results, 'silver': silver_results, 'gold': gold_results}, default=str))

Code size: aim for ~8–12 cells, ~200 lines total.";

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
    /// Always prepends a "parameters" cell with the seven standard variables and
    /// binds the target lakehouse via `metadata.dependencies.lakehouse` so the
    /// notebook can use Spark relative paths and SQL endpoint without manual binding.
    /// </summary>
    public string BuildNotebookJson(List<string> codeCells, string? targetLakehouseId = null,
                                     string? targetLakehouseName = null, string? workspaceId = null)
    {
        var cells = new List<object>();
        cells.Add(new
        {
            cell_type = "markdown",
            metadata = new { },
            source = new[] { "# Copilot Medallion (LLM-generated from spec)\n\nGenerated by copilot.roesli.org following the Microsoft Fabric e2e-medallion-architecture skill." }
        });
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

        // Notebook metadata. If we know the target lakehouse, bind it via
        // dependencies.lakehouse so the notebook is opened/run with the right context.
        object metadata;
        if (!string.IsNullOrEmpty(targetLakehouseId) && !string.IsNullOrEmpty(workspaceId))
        {
            metadata = new
            {
                kernelspec = new { display_name = "synapse_pyspark", language = "python", name = "synapse_pyspark" },
                language_info = new { name = "python" },
                microsoft = new { language = "python" },
                dependencies = new
                {
                    lakehouse = new
                    {
                        default_lakehouse = targetLakehouseId,
                        default_lakehouse_name = targetLakehouseName ?? "",
                        default_lakehouse_workspace_id = workspaceId,
                        known_lakehouses = new[] { new { id = targetLakehouseId } }
                    }
                }
            };
        }
        else
        {
            metadata = new
            {
                kernelspec = new { display_name = "synapse_pyspark", language = "python", name = "synapse_pyspark" },
                language_info = new { name = "python" },
                microsoft = new { language = "python" }
            };
        }

        var nb = new
        {
            cells,
            metadata,
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
