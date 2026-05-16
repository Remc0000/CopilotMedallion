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

    public async Task<Dictionary<string, List<string>>?> GenerateNotebooksFromSpecAsync(
        string specMarkdown,
        string workspaceId, string sourceLakehouseId, string targetLakehouseId,
        string targetLakehouseName, List<string> sourceTables, string runId,
        string? model = null)
    {
        if (!_llm.Configured) return null;

        var system = @"You are an expert Microsoft Fabric data engineer following the Microsoft Fabric e2e-medallion-architecture, powerbi-authoring-cli, and powerbi-consumption-cli best practices. You generate FOUR separate PySpark notebooks (one per layer: bronze, silver, gold, reporting) that together build a complete Bronze → Silver → Gold medallion AND a Power BI semantic model + report + Data Agent on top.

You are operating as the FabricDataEngineer agent (https://github.com/microsoft/skills-for-fabric/blob/main/agents/FabricDataEngineer.agent.md). Apply ALL of its core principles:
  * Decompose broad requests into endpoint-specific stages (here: one notebook per medallion layer + one for reporting).
  * Require explicit environment parameterization — every workspace/lakehouse/source ID flows in via the parameters cell. NEVER hard-code IDs or secrets in cells.
  * Use Delta Lake for ALL lakehouse tables.
  * Maintain clear separation of raw (bronze), validated (silver), and serving (gold) layers — never mix them.
  * Insert validation gates between layers — quality tests in the `test` schema, written as part of the gold layer.
  * Prefer incremental/idempotent patterns (overwrite-with-mergeSchema makes the pipeline re-buildable).
  * Architecture decisions must be consistent across Spark, SQL endpoint and Power BI semantic model layers.
  * Cross-environment portability — same notebook should run unchanged in dev/test/prod, only parameters change.

OUTPUT FORMAT (STRICT):
Return ONLY a JSON object with this exact shape:
{
  ""bronze"":     [""<cell1>"", ""<cell2>"", ...],
  ""silver"":     [""<cell1>"", ""<cell2>"", ...],
  ""gold"":       [""<cell1>"", ""<cell2>"", ...],
  ""reporting"":  [""<cell1>"", ""<cell2>"", ...]
}
No prose, no markdown fences — only the JSON. Each notebook is deployed and run sequentially.

NOTEBOOK INDEPENDENCE:
- Each notebook receives the SAME parameter cell at the top (provided by the platform) with: workspace_id, source_workspace_id, source_lakehouse_id, target_lakehouse_id, target_lakehouse_name, source_tables_csv, run_id, spec_url. DO NOT redefine these.
- Each notebook receives a bootstrap cell that defines: src_base, tgt_base, source_tables, _save_error helper.
- Each notebook is INDEPENDENT: it does NOT share Python state with the others (different Spark sessions, different runtimes). The silver notebook reads from the bronze schema in OneLake; the gold notebook reads from the silver schema in OneLake. You may NOT reference variables defined in another notebook.
- Each notebook MUST have its own local results dict (bronze_results / silver_results / gold_results) and print the JSON summary at the end of its OWN notebook.

PLATFORM CONSTRAINTS (apply per notebook):
- Spark abfss paths:
    src_base = f""abfss://{source_workspace_id}@onelake.dfs.fabric.microsoft.com/{source_lakehouse_id}""   # uses SOURCE workspace
    tgt_base = f""abfss://{workspace_id}@onelake.dfs.fabric.microsoft.com/{target_lakehouse_id}""           # uses ITEM workspace
- Source tables live at: f""{src_base}/Tables/{table_relative_path}"" where table_relative_path is one of the values in source_tables_csv. The source can be either schema-enabled (path = '<schema>/<table>') OR classic (path = '<table>'). Treat the value as-is.
- The TARGET lakehouse IS SCHEMA-ENABLED. Use schemas as the layer separator. Write paths:
    Bronze tables → f""{tgt_base}/Tables/bronze/<flat>""   (schema = 'bronze')
    Silver tables → f""{tgt_base}/Tables/silver/<flat>""   (schema = 'silver')
    Gold tables   → f""{tgt_base}/Tables/gold/<gold_name>"" (schema = 'gold', e.g. dim_customer, fact_sales)
    Test results  → f""{tgt_base}/Tables/test/test_results"" (schema = 'test', APPEND mode)
  where flat = table_relative_path.split('/')[-1].lower().
- Schemas (bronze/silver/gold/test) get auto-created by the lakehouse on first write — do NOT issue CREATE SCHEMA statements (they fail against the abfss path API).
- Always use spark.read.format('delta').load(...) and DataFrame.write.format('delta')...save(...). NEVER use saveAsTable.
- Never use credentials/secrets — trust the notebook runtime identity for OneLake access.
- Per-table read errors MUST cause the JOB TO FAIL (raise/re-raise after logging). Do NOT silently capture errors in a dict and continue.

WHAT EACH NOTEBOOK MUST DO:

BRONZE notebook:
- Loop over source_tables. For each: read from src_base, add metadata columns (ingestion_timestamp, source_path, batch_id=run_id, ingestion_date), write to bronze/<flat> with mode='overwrite' option('overwriteSchema','true') partitioned by ingestion_date.
- Build a bronze_results dict {table: {rows, path}} and print summary as JSON at end.

SILVER notebook:
- Loop over source_tables. For each: read from tgt_base/Tables/bronze/<flat>, drop fully-null columns, trim strings, snake_case rename, dedupe on natural key, add ingestion_dt and source_dt columns (per spec), add _silver_ts audit, write to silver/<flat> with mode='overwrite' overwriteSchema=true. After write run OPTIMIZE inside try/except.
- Build a silver_results dict and print summary.

GOLD notebook:
- BEFORE any Gold write, set: spark.conf.set('spark.sql.parquet.vorder.default','true'); spark.conf.set('spark.databricks.delta.optimizeWrite.enabled','true'); spark.conf.set('spark.databricks.delta.optimizeWrite.binSize','1g').
- Read silver tables into a dict keyed by lowercased flat name.
- Build the dims and facts described in the spec (dim_customer, dim_product, dim_sales, fact_sales for AdventureWorksLT shape). Write to gold/<name>.
- Then run the data-quality tests described in the spec and APPEND rows to test/test_results.
- Build a gold_results dict + a test_summary dict and print combined summary as JSON.

REPORTING notebook (Power BI semantic model + report + Data Agent):
- Follow these reference skills for best practices (the LLM should know these patterns from its training; URLs are documentation pointers only):
  * https://github.com/microsoft/skills-for-fabric/tree/main/skills/powerbi-authoring-cli
  * https://github.com/microsoft/skills-for-fabric/tree/main/skills/powerbi-consumption-cli
  * https://github.com/RuiRomano/powerbi-agentic-plugins/tree/main/plugins/powerbi/skills/powerbi-semantic-model-authoring  (modeling-guidelines, direct-lake-guidelines, TMDL syntax, DAX patterns)
  * https://github.com/RuiRomano/powerbi-agentic-plugins/tree/main/plugins/powerbi/skills/powerbi-report-authoring  (PBIR format)
- Apply standard star-schema modeling: explicit measures only, snake_case → PascalCase optional, descriptive measure names, consistent naming, mark date table if a date dim exists. Avoid implicit measures on the report side.
- This notebook calls the Fabric REST API to create three downstream items. Use `import requests` and authenticate with `notebookutils.credentials.getToken('pbi')` for Power BI / Fabric API calls.
- Item names must be derived from target_lakehouse_name: `{target_lakehouse_name}_sm` (SemanticModel), `{target_lakehouse_name}_rpt` (Report), `{target_lakehouse_name}_agent` (AISkill / Data Agent).
- Fabric REST base URL: `https://api.fabric.microsoft.com/v1/workspaces/{workspace_id}`.

CRITICAL: every REST helper function MUST return None on failure (caught exception, non-2xx status, etc.) and the calling code MUST check `if X is None` BEFORE calling `.get(...)` on it. Example pattern:
```
def _fabric_post(path, body):
    try:
        r = requests.post(BASE_URL + path, headers={'Authorization': f'Bearer {tok}'}, json=body, timeout=60)
        if r.status_code >= 400:
            print(f'REST {path} returned {r.status_code}: {r.text[:500]}')
            return None
        return r.json() if r.text else {}
    except Exception:
        print(traceback.format_exc())
        return None

sm_resp = _fabric_post('/items', sm_body)
if sm_resp is None:
    print('Semantic model creation failed; aborting report + data agent steps')
    raise RuntimeError('Failed to create semantic model')
semantic_model_id = sm_resp.get('id')
```
NEVER write `something.get('id')` without first checking `if something is None`.

1) Discover the Gold lakehouse SQL endpoint:
   - GET `/lakehouses/{target_lakehouse_id}` → wait for `properties.sqlEndpointProperties.provisioningStatus == 'Success'` (poll up to 5 min). Capture `connectionString` and `id` (the SQL endpoint id).

2) Create the Direct Lake SemanticModel:
   - POST `/items` with `{ displayName, type:'SemanticModel', definition:{ parts:[{ path, payload:<base64 utf8>, payloadType:'InlineBase64' }] } }`.
   - The definition is a single-file TMSL `model.bim` JSON (compatibilityLevel 1604 for Direct Lake). It must contain:
     - dataSources: a structured source pointing at the lakehouse SQL endpoint (mode = `directQuery`, but tables use DirectLake by virtue of the partitions referencing the SQL endpoint).
     - tables for each gold table the spec defines (dim_customer, dim_product, dim_sales, fact_sales). Columns must mirror the gold table schemas. Each table has a single partition with source = entityName from the SQL endpoint.
     - relationships: fact_sales[customer_id] → dim_customer[customer_id], fact_sales[product_id] → dim_product[product_id], fact_sales[sales_person] → dim_sales[sales_person].
     - measures on fact_sales (suggested, follow DAX best practices — explicit measures, FORMAT STRINGS, named in PascalCase):
         [Total Sales] = SUM ( fact_sales[line_total] )
         [Total Discount Amount] = SUMX ( fact_sales, fact_sales[line_total] * fact_sales[unit_price_discount] )
         [Distinct Salespersons] = DISTINCTCOUNT ( dim_sales[sales_person] )
         [Sales Count] = COUNTROWS ( fact_sales )
   - Use `payload` = base64(utf-8(json.dumps(tmsl_obj)))` and `path` = 'model.bim'. payloadType = 'InlineBase64'.

3) Create the Report (PBIR format):
   - POST `/items` with `{ displayName, type:'Report', definition:{ parts:[...] } }`. The parts include a `definition.pbir` JSON pointing at the new SemanticModel id, and at least one page in `report.json` with these visuals:
     - Page 1 'Sales overview': card showing [Total Sales], card [Distinct Salespersons], bar chart sales by sales_person sorted desc, bar chart total discount by sales_person sorted desc.
     - Page 2 'Data quality': table visual showing test_results filtered to latest run_id, color-coded by status.
   - It's OK to keep visuals minimal — a card + a bar chart is enough; the user can extend. Use the same payload pattern.

4) Create the Data Agent (AISkill item):
   - POST `/items` with `{ displayName, type:'AISkill', description }`. If the item type 'AISkill' is rejected (preview), fall back to printing a clear message that the data agent must be created manually with these starter prompts:
     - 'Who are the top 5 salespersons by total sales?'
     - 'Which salespersons give the largest discounts?'
     - 'How many distinct salespersons are there?'
     - 'Show me the latest data quality results.'
   - Wrap the AISkill call in try/except so failure does not crash the notebook (its preview status varies by tenant).

5) Print a final JSON summary line with semantic_model_id, report_id, data_agent_id (or null).

Wrap each step in try/except + _save_error('reporting', e); print clear progress messages.

Robustness:
- Wrap each major step in try/except. On exception: call `_save_error('<layer-name>', e)`, print traceback, re-raise.

Defensive column references (CRITICAL):
- AdventureWorksLT junction tables (CustomerAddress, ProductModelProductDescription, etc.) only have FK columns — no own ID columns. There is no customer_address_id.
- Source CamelCase ID columns become snake_case after the silver rename (SalesOrderID → sales_order_id, CustomerID → customer_id).
- Use a _maybe(df, name) helper to skip columns that don't exist in df.columns rather than erroring on them.

Ambiguous columns after joins (CRITICAL):
- ALWAYS alias both sides of every join (`customer.alias('c').join(customeraddress.alias('ca'), c.customer_id == ca.customer_id, 'left')`) and reference columns with F.col('c.customer_id') / F.col('ca.address_id') — never bare F.col('customer_id').

GroupBy / aggregation safety (CRITICAL):
- BEFORE any groupBy(col)/agg(...), assert the column exists in df.columns.
- If the spec asks for aggregations by sales_person, fact_sales MUST include a cleaned sales_person column (regex: `regexp_replace(regexp_replace(sales_person, r'^[Aa]dventure[-_ ]?[Ww]orks/', ''), r'\d+$', '')`).

Test-results table schema (in the gold notebook):
- Columns in order: run_id STRING, test_name STRING, layer STRING, table_name STRING, status STRING (PASS/FAIL/ERROR), actual STRING, expected STRING, details STRING, checked_at TIMESTAMP.
- Use mode='append' option('mergeSchema','true'). Build with a StructType. Each test in its own try/except — bad test = FAIL row, not a crashed cell.

Code size per notebook: aim for 4-7 cells, ~80-120 lines.";

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
        try { answer = await _llm.ChatAsync(system, user, maxTokens: 32000, temperature: 0.1, model: model, runId: runId); }
        catch (Exception ex) { _log.LogWarning(ex, "LLM cell-gen failed"); return null; }

        var json = ExtractJson(answer);
        if (json is null)
        {
            _log.LogWarning("Could not find JSON in LLM response. Raw preview: {preview}",
                answer.Length > 800 ? answer.Substring(0, 800) + "...(truncated)" : answer);
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(json);
            var result = new Dictionary<string, List<string>>();
            foreach (var layer in new[] { "bronze", "silver", "gold" })
            {
                if (doc.RootElement.TryGetProperty(layer, out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    var list = new List<string>();
                    foreach (var c in arr.EnumerateArray())
                        if (c.ValueKind == JsonValueKind.String) list.Add(c.GetString() ?? "");
                    if (list.Count > 0) result[layer] = list;
                }
            }
            // Back-compat: if old "cells" array is present and no per-layer keys, return everything as gold (single notebook fallback).
            if (result.Count == 0 && doc.RootElement.TryGetProperty("cells", out var legacyArr))
            {
                var list = new List<string>();
                foreach (var c in legacyArr.EnumerateArray())
                    if (c.ValueKind == JsonValueKind.String) list.Add(c.GetString() ?? "");
                if (list.Count > 0) result["gold"] = list;
            }
            // Also accept the 4th key 'reporting' (above loop already handles bronze/silver/gold but not reporting).
            if (doc.RootElement.TryGetProperty("reporting", out var rep) && rep.ValueKind == JsonValueKind.Array)
            {
                var list = new List<string>();
                foreach (var c in rep.EnumerateArray())
                    if (c.ValueKind == JsonValueKind.String) list.Add(c.GetString() ?? "");
                if (list.Count > 0) result["reporting"] = list;
            }
            return result.Count == 0 ? null : result;
        }
        catch (Exception ex) { _log.LogWarning(ex, "LLM JSON parse failed"); return null; }
    }

    public async Task<List<string>?> GenerateCellsFromSpecAsync(
        string specMarkdown,
        string workspaceId, string sourceLakehouseId, string targetLakehouseId,
        string targetLakehouseName, List<string> sourceTables, string runId,
        string? model = null)
    {
        var perLayer = await GenerateNotebooksFromSpecAsync(specMarkdown, workspaceId, sourceLakehouseId,
            targetLakehouseId, targetLakehouseName, sourceTables, runId, model);
        if (perLayer == null) return null;
        var all = new List<string>();
        foreach (var k in new[] { "bronze", "silver", "gold" })
            if (perLayer.TryGetValue(k, out var cells)) all.AddRange(cells);
        return all.Count == 0 ? null : all;
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
    /// Given the previous spec markdown plus the Spark error traceback, ask the LLM to
    /// produce an updated spec markdown that adds explicit constraints/clarifications
    /// addressing the root cause of the failure. The structure of the spec is preserved
    /// (same top-level headings) — only the content of the build instructions is tightened.
    /// </summary>
    /// <summary>
    /// Generate an initial spec markdown tailored to the user's picked source tables.
    /// Returned markdown follows the canonical 5-section structure (Generic / Medallion /
    /// Semantic / Report / Data Agent) so the frontend can split it into the per-section editors.
    /// </summary>
    public async Task<string?> ProposeSpecAsync(string runId, string workspaceId, string sourceLakehouseName,
        string sourceLakehouseId, List<string> sourceTables, string targetLakehouseName,
        IReadOnlyDictionary<string, string>? tableSchemas = null, string? model = null)
    {
        if (!_llm.Configured) return null;
        var system = @"You are a senior Microsoft Fabric data architect. Given the user's selected source tables AND their actual column schemas, produce a complete medallion build spec. The output is shown to the user in 5 editable sections — keep that structure intact.

CRITICAL: BASE YOUR PROPOSAL ON THE ACTUAL TABLE NAMES AND COLUMNS PROVIDED. Do NOT assume any specific source shape (AdventureWorksLT, Northwind, etc.) unless the actual table names and columns clearly match that shape. If they don't match a well-known pattern, propose dims/facts based on what you actually see: identify which tables look like facts (transactional, time-stamped, foreign-key heavy), which look like dimensions (small, master-data, descriptive), and which are junction tables. If multiple modeling routes are plausible, briefly describe each and ask the user to choose by editing the spec.

Apply these reference skills/agents:
- https://github.com/microsoft/skills-for-fabric/blob/main/agents/FabricDataEngineer.agent.md
- https://github.com/microsoft/skills-for-fabric/tree/main/skills/e2e-medallion-architecture
- https://github.com/microsoft/skills-for-fabric/tree/main/skills/powerbi-authoring-cli
- https://github.com/RuiRomano/powerbi-agentic-plugins/tree/main/plugins/powerbi/skills/powerbi-semantic-model-authoring
- https://github.com/RuiRomano/powerbi-agentic-plugins/tree/main/plugins/powerbi/skills/powerbi-report-authoring

OUTPUT FORMAT (STRICT):
Return ONLY the markdown spec — no prose around it, no code fences. Use EXACTLY these top-level headings in this order:

```
# Run Spec <runId>

## Inputs
- Workspace: `<workspace_id>`
- Source Lakehouse: **<source_name>** (`<source_id>`)
- Tables to ingest into Bronze:
  - `<table1>`
  - `<table2>`
- Target Lakehouse: **<target_name>**

## Generic guidance
(MUST include the full list of skills/agents the LLM follows AND the cross-cutting code rules. Use this exact structure:

```
Apply these reference skills/agents at all times:
- FabricDataEngineer agent: https://github.com/microsoft/skills-for-fabric/blob/main/agents/FabricDataEngineer.agent.md
- e2e-medallion-architecture skill: https://github.com/microsoft/skills-for-fabric/tree/main/skills/e2e-medallion-architecture
- spark-authoring-cli skill: https://github.com/microsoft/skills-for-fabric/tree/main/skills/spark-authoring-cli
- powerbi-authoring-cli skill: https://github.com/microsoft/skills-for-fabric/tree/main/skills/powerbi-authoring-cli
- powerbi-consumption-cli skill: https://github.com/microsoft/skills-for-fabric/tree/main/skills/powerbi-consumption-cli
- powerbi-semantic-model-authoring: https://github.com/RuiRomano/powerbi-agentic-plugins/tree/main/plugins/powerbi/skills/powerbi-semantic-model-authoring
- powerbi-report-authoring: https://github.com/RuiRomano/powerbi-agentic-plugins/tree/main/plugins/powerbi/skills/powerbi-report-authoring
```

THEN include the standard cross-cutting code rules: defensive column references, alias-prefixed joins after every join, groupBy/agg column existence assertion, defensive REST handling with `if x is None: raise` BEFORE `.get()`, no `saveAsTable`, parameter cells, idempotent overwrite patterns, error-loud try/except that calls `_save_error(layer, e)` and re-raises.)

## Bronze
(how each source table is landed into the `bronze` schema — metadata columns, write mode, partitioning. Be specific based on the picked tables.)

## Silver
(cleaning, dedup keys, snake_case rename, audit columns, OPTIMIZE — for each table call out the dedup key you'd use based on the columns)

## Gold
(dims + facts + data quality tests — propose specific dim/fact tables based on the ACTUAL columns; identify which tables look like facts vs dims vs junction tables. Include the 5 standard tests in a `### Data quality tests` subsection.)

## Semantic model
(Direct Lake star schema with explicit measures — propose tables, relationships, measures relevant to the actual columns you see)

## Report
(pages + visuals tailored to the modeled data; always include a Data quality page)

## Data Agent
(AISkill grounded on the semantic model with role, domain hints, starter questions, guardrails)
```

PROPOSAL RULES:
- Use the ACTUAL column lists provided to make decisions. Do not invent columns.
- If a table has obvious FK columns (e.g., 'customer_id' and the customer table exists), use it as a join key.
- If a table has clear PK pattern (e.g., 'id', '{tablename}_id'), note it.
- For each non-obvious modeling decision, mention alternatives in the spec so the user can edit.
- Include standard data quality tests (row counts, no-null PKs, unique PKs, referential integrity for join keys you identified).
- For the semantic model: propose ~4-6 explicit DAX measures appropriate to the data.
- For the report: propose ~2-3 pages with concrete visual types tied to the measures.
- For the Data Agent: write a role description, 6-10 starter questions matching the actual data domain, and tight guardrails.
- Keep markdown clean. Aim for ~120-220 lines total.";

        var schemaSection = tableSchemas != null && tableSchemas.Count > 0
            ? string.Join("\n", sourceTables.Select(t =>
                tableSchemas.TryGetValue(t, out var s) && !string.IsNullOrEmpty(s)
                    ? $"- `{t}`\n    columns: {s}"
                    : $"- `{t}`\n    columns: (could not introspect)"))
            : string.Join("\n", sourceTables.Select(t => $"- `{t}`"));

        var user = $@"## Run parameters
runId: {runId}
workspace_id: {workspaceId}
source_lakehouse: {sourceLakehouseName} ({sourceLakehouseId})
target_lakehouse: {targetLakehouseName}

## Source tables WITH ACTUAL SCHEMAS
{schemaSection}

Produce the spec markdown now. Base every modeling decision on the columns above — do not assume any specific known data model.";

        try
        {
            return await _llm.ChatAsync(system, user, maxTokens: 16000, temperature: 0.3, model: model, runId: runId);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "ProposeSpec LLM call failed");
            return null;
        }
    }

    public async Task<string?> FixSpecAsync(string currentSpec, string errorTrace, string? model = null, int? iteration = null, string? failedLayer = null, string? runId = null)
    {
        if (!_llm.Configured) return null;
        var system = @"You are an expert Microsoft Fabric data engineer reviewing a failed medallion build.

You are given:
1. The current spec markdown (the user's build instructions, split into 7 sections: Generic guidance, Bronze, Silver, Gold, Semantic model, Report, Data Agent).
2. The Spark traceback from the cell that failed.
3. Optional metadata: which iteration this is, which layer failed, and the run id.

Your job: produce an UPDATED spec markdown that prevents this specific failure on the next build. Be SURGICAL — keep the user's intent intact, only tighten the relevant section(s) of the spec with explicit constraints, column names, join keys, or data-shape clarifications.

Rules:
- Return ONLY the updated spec markdown. No prose around it, no JSON, no code fences wrapping the whole output.
- Preserve the existing top-level structure exactly: the '# Run Spec ...' heading, '## Inputs', '## Generic guidance', '## Bronze', '## Silver', '## Gold', '## Semantic model', '## Report', '## Data Agent'. Do not delete the Inputs block.
- ## Generic guidance: you MAY revise it (the user trusts the LLM here). If the failure exposes a new cross-cutting rule, add it. Keep the skill/agent URL list intact at the top.
- Sharpen language in the relevant LAYER section (Bronze/Silver/Gold/Semantic/Report/DataAgent) that caused the failure (UNRESOLVED_COLUMN → name the column; AMBIGUOUS_REFERENCE → alias-prefix joins; missing junction-table column → name the real columns).
- Prepend a NEW section at the very top (between the '# Run Spec' line and '## Inputs') called '## Updated specs' that documents this change. Format:
  ```
  ## Updated specs

  ### Iteration <N> — <UTC timestamp> — failed layer: <layer> (run: <runId>)
  - **Root cause (1-line summary)**: <e.g. AMBIGUOUS_REFERENCE on 'customer_id' in dim_customer build>
  - **What was changed**: <which section(s) you tightened and how, in 1-3 bullets>
  ```
  If a '## Updated specs' section already exists, KEEP its entries and APPEND a new '### Iteration ...' subsection underneath them (newest at the bottom).
- Keep the markdown clean and readable.";

        var meta = "";
        if (iteration.HasValue) meta += $"iteration: {iteration.Value}\n";
        if (!string.IsNullOrEmpty(failedLayer)) meta += $"failed_layer: {failedLayer}\n";
        if (!string.IsNullOrEmpty(runId)) meta += $"run_id: {runId}\n";
        meta += $"timestamp_utc: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z\n";

        var user = $@"## Failure metadata
{meta}
## Current spec
{currentSpec}

## Spark error traceback
```
{errorTrace}
```

Produce the updated spec markdown now. Remember to PREPEND a '## Updated specs' changelog entry between '# Run Spec' and '## Inputs'.";
        try
        {
            return await _llm.ChatAsync(system, user, maxTokens: 12000, temperature: 0.1, model: model, runId: runId);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "FixSpec LLM call failed");
            return null;
        }
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
                "source_workspace_id = \"\"\n",
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
                "# Source path uses the SOURCE workspace (may differ from the item's workspace).\n",
                "_src_ws = source_workspace_id or workspace_id\n",
                "src_base = f\"abfss://{_src_ws}@onelake.dfs.fabric.microsoft.com/{source_lakehouse_id}\"\n",
                "tgt_base = f\"abfss://{workspace_id}@onelake.dfs.fabric.microsoft.com/{target_lakehouse_id}\"\n",
                "source_tables = [t.strip() for t in source_tables_csv.split(',') if t.strip()]\n",
                "print(f\"run_id={run_id} src_base={src_base} tgt_base={tgt_base} tables={source_tables}\")\n",
                "\n",
                "# Helper: persist exception details to OneLake so the web UI can surface them without Fabric monitor.\n",
                "_error_path = f\"{tgt_base}/Files/_copilot_medallion/runs/{run_id}/error.txt\"\n",
                "def _save_error(layer, exc):\n",
                "    try:\n",
                "        from notebookutils import mssparkutils\n",
                "        body = f\"[{datetime.utcnow().isoformat()}Z] LAYER={layer} RUN={run_id}\\n\\n{traceback.format_exc()}\"\n",
                "        mssparkutils.fs.put(_error_path, body, True)\n",
                "        print(f\"_save_error: wrote {_error_path}\")\n",
                "    except Exception:\n",
                "        print(f\"_save_error itself failed:\\n{traceback.format_exc()}\")\n"
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
