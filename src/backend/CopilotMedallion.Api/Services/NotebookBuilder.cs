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

    // Layer-agnostic Spark column-reference rules. Embedded verbatim in the
    // '## Generic guidance' section of every spec proposed for a new item so the
    // notebook generator follows them on Bronze, Silver AND Gold. Keep in sync with
    // specs/template.md (the static fallback used when the LLM is unavailable).
    private const string GlobalSparkColumnRules = @"### Global Spark column-reference rules (apply to ALL layers: Bronze, Silver, Gold)
These rules exist to prevent recurring `UNRESOLVED_COLUMN` / `AnalysisException` analyzer errors. They are layer-agnostic — apply them anywhere a Spark DataFrame is transformed.

Rule A — No dotted alias strings.
- Never pass dotted strings like ""c.customer_id"", ""ca.address_type"", ""h.sales_person"", or ""pc_child.name"" to F.col(...), withColumn(...), Window.partitionBy(...), Window.orderBy(...), or select(...). Spark treats ""c.customer_id"" as a single column literally named c.customer_id, which does not resolve once any projection or rename has been applied.
- Alias scope (.alias(""c""), .alias(""ca""), ...) is only valid inside the SAME select / join expression that introduces it. Once you produce a new DataFrame via select(...) or withColumn(...), the dotted alias form is gone and you must reference plain column names.

Rule B — Materialize helper columns before they are needed downstream.
- For any column that will later be referenced by a Window, a withColumn, or a downstream join after a projection, first materialize it as a flat, unambiguous helper column (e.g. rank_customer_id, rank_address_type, sales_person_source) in the same select that introduces the join aliases.

Rule C — Do not drop a column before its last consumer has run.
- Before adding a withColumn, verify every F.col(...) referenced by that expression still exists on the DataFrame at that step. If a previous select(...) projection removed it, either:
  - (preferred) move the withColumn BEFORE the projection that drops the source column, OR
  - keep the source column in the projection, OR
  - re-derive the value from a column that IS still present (often a boolean/flag that was computed earlier from the same source).
- Example of the failure to avoid: dropping discontinued_date in a select(...) and then later writing F.when(F.col('discontinued_date').isNotNull(), ...) inside withColumn('is_sellable_currently', ...). The column is gone and Spark raises UNRESOLVED_COLUMN.
- When a boolean flag derived from a raw column already exists on the DataFrame (e.g. is_discontinued derived from discontinued_date), prefer reusing the flag (F.col('is_discontinued')) over re-reading the dropped raw column.

Rule D — Order of derived-column computations matters.
- When building several derived columns where one depends on another (e.g. is_discontinued, then is_sellable_currently which uses is_discontinued), add them in dependency order with sequential withColumn calls, and reference the already-derived flag in the next expression — do NOT reach back to a raw source column that may have been dropped.

Rule E — Validate schema between non-trivial transformation steps.
- After any select(...) / drop(...) / heavy withColumn chain, and BEFORE the next step that depends on specific columns, assert those columns exist. Fail fast with an error message that names the missing column and the DataFrame variable, so the auto-fixer gets an actionable diagnostic instead of a deep analyzer stack trace.

Rule F — Self-check pattern for every withColumn / Window.
- For every withColumn(name, expr) and every Window definition, confirm: ""Every column referenced inside expr / inside the window's partitionBy / orderBy exists on the DataFrame at this exact point."" If not, fix per Rule C before generating the code.

Rule G — Optional-column helpers must return typed Column nulls, not Python None.
- When defining a helper like `_maybe(df, name)` that returns the column if it exists on the DataFrame and a fallback otherwise, NEVER return Python `None`. Spark functions (`F.coalesce`, `F.greatest`, `F.least`, `F.concat`, `F.when(...).otherwise(...)`, etc.) reject `None` arguments with `PySparkTypeError: [NOT_COLUMN_OR_STR]` and the cell crashes BEFORE any later fallback (e.g. `F.current_timestamp()`) gets a chance to satisfy the call.
- Correct pattern — return a typed null literal as a Spark Column:
  ```
  def _maybe(df, name, dtype='timestamp'):
      return F.col(name) if name in df.columns else F.lit(None).cast(dtype)
  ```
  Pick the `dtype` to match the surrounding expression (`'timestamp'` for date/time coalesces, `'string'` for text, `'double'` for numeric, etc.) so Spark can resolve the result type without ambiguity.
- Alternative pattern (when the helper genuinely cannot know the dtype) — filter `None`s at the call site BEFORE invoking the Spark function:
  ```
  candidates = [c for c in (_maybe(df, 'modified_date'), _maybe(df, 'order_date')) if c is not None]
  df = df.withColumn('source_dt', F.to_date(F.coalesce(*candidates, F.current_timestamp())))
  ```
  Either approach is acceptable, but **never pass Python `None` directly into a Spark function**.
- Applies to ALL optional-column lookups across Bronze, Silver, Gold — including audit-timestamp coalesces, optional-key joins, fallback string formatting, etc. This is a layer-agnostic rule.
";

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

CELL COMMENTING (REQUIRED):
- Every cell you emit MUST begin with a short comment block describing what the cell does and why. Format:
    # ---
    # <one-line summary of the cell>
    # <optional 1-2 extra lines explaining intent, side effects, or assumptions>
- Then the actual Python code follows. Skipping the comment block is not allowed for ANY cell, including small helper cells.
- The comments should explain INTENT, not just repeat the code in prose.
- Goal: someone scrolling through the notebook in Fabric can understand each block at a glance.

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
            foreach (var layer in new[] { "bronze", "silver", "gold", "reporting" })
            {
                if (doc.RootElement.TryGetProperty(layer, out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    var list = ReadStringArray(arr);
                    if (list.Count > 0) result[layer] = list;
                }
            }
            // Back-compat: if old "cells" array is present and no per-layer keys, return everything as gold (single notebook fallback).
            if (result.Count == 0 && doc.RootElement.TryGetProperty("cells", out var legacyArr))
            {
                var list = ReadStringArray(legacyArr);
                if (list.Count > 0) result["gold"] = list;
            }
            return result.Count == 0 ? null : result;
        }
        catch (Exception ex) { _log.LogWarning(ex, "LLM JSON parse failed"); return null; }
    }

    private static List<string> ReadStringArray(JsonElement arr)
    {
        var list = new List<string>();
        foreach (var c in arr.EnumerateArray())
            if (c.ValueKind == JsonValueKind.String) list.Add(c.GetString() ?? "");
        return list;
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
    /// Generates the cells for ONE layer's notebook given:
    /// - the full spec markdown,
    /// - the ACTUAL produced-table schemas from the prior layer (or the source-table schemas for bronze),
    /// - the standard run parameters.
    /// This is the per-layer replacement for `GenerateNotebooksFromSpecAsync`. It lets the LLM
    /// write Spark code against real columns rather than guess what the previous layer produced.
    /// Returns null on LLM failure; the caller must hard-fail in that case rather than silently
    /// proceeding with hallucinated code.
    /// </summary>
    public async Task<List<string>?> GenerateLayerNotebookAsync(
        string layer,
        string specMarkdown,
        string workspaceId, string sourceWorkspaceId, string sourceLakehouseId,
        string targetLakehouseId, string targetLakehouseName,
        List<string> sourceTables,
        IReadOnlyDictionary<string, string> priorLayerSchemas,
        string runId, string? model = null)
    {
        if (!_llm.Configured) return null;
        layer = layer.Trim().ToLowerInvariant();
        var allowed = new[] { "bronze", "silver", "gold", "reporting" };
        if (!allowed.Contains(layer)) throw new ArgumentException($"unknown layer '{layer}'");

        // Layer-specific responsibilities (kept compact so the prompt fits comfortably).
        var layerSpec = layer switch
        {
            "bronze" => @"BRONZE notebook:
- Loop over source_tables. For each: read from src_base/Tables/<table_relative_path> with spark.read.format('delta'), add metadata columns (ingestion_timestamp, source_path, batch_id=run_id, ingestion_date), write to f'{tgt_base}/Tables/bronze/<flat>' with mode='overwrite' option('overwriteSchema','true') partitioned by ingestion_date. flat = table_relative_path.split('/')[-1].lower().
- Build a bronze_results dict {table: {rows, path}} and print the summary as JSON at the end.
- Hard-fail (raise) on any per-table read or write error after calling _save_error('bronze', e).",
            "silver" => @"SILVER notebook:
- The 'prior_layer_schemas' provided in the user message tells you the EXACT columns currently sitting in the bronze schema. USE THEM — do not assume; do not invoke F.coalesce/F.greatest etc. with arguments that might be Python None (see Rule G in Generic guidance).
- Loop over bronze tables. For each: read from f'{tgt_base}/Tables/bronze/<flat>', drop fully-null columns, trim strings, snake_case-rename columns (using a deterministic helper), dedupe on natural key (per spec), add ingestion_dt and source_dt audit columns (use F.lit(None).cast('timestamp') when the source columns are missing — never Python None), add _silver_ts = F.current_timestamp(), write to f'{tgt_base}/Tables/silver/<flat>' mode='overwrite' overwriteSchema=true. After write try OPTIMIZE inside try/except.
- Build a silver_results dict and print the JSON summary.
- Hard-fail (raise) on any error after _save_error('silver', e).",
            "gold" => @"GOLD notebook:
- The 'prior_layer_schemas' input describes the EXACT columns now present in silver. Build dims/facts using ONLY columns that appear there.
- BEFORE any gold write, set: spark.conf.set('spark.sql.parquet.vorder.default','true'); spark.conf.set('spark.databricks.delta.optimizeWrite.enabled','true'); spark.conf.set('spark.databricks.delta.optimizeWrite.binSize','1g').
- Read silver tables into a dict keyed by lowercased table name (read from f'{tgt_base}/Tables/silver/<name>').
- Build the dims and facts described in the spec's '## Gold' section (e.g. dim_customer, dim_product, dim_sales, fact_sales). Always alias both sides of joins (`.alias('c').join(other.alias('ca'), c.customer_id == ca.customer_id, 'left')`). Write each to f'{tgt_base}/Tables/gold/<name>'.
- Then run the data-quality tests described in the spec's separate '## Test' section and APPEND rows to f'{tgt_base}/Tables/test/test_results'. Test rows schema: run_id STRING, test_name STRING, layer STRING, table_name STRING, status STRING (PASS/FAIL/ERROR), actual STRING, expected STRING, details STRING, checked_at TIMESTAMP. Use mode='append' option('mergeSchema','true'). Each test in its own try/except — a failing test produces a FAIL row, never crashes the cell.
- Build a gold_results dict + a test_summary dict and print combined JSON summary.
- Hard-fail (raise) on any error after _save_error('gold', e). (Test failures DO NOT raise — they just write FAIL rows; only infrastructure/cell errors raise.)",
            "reporting" => @"REPORTING notebook (Power BI semantic model + report + Data Agent):
- The 'prior_layer_schemas' input describes the EXACT columns currently sitting in gold (and test/test_results if discovered). Build TMSL columns from these — never from spec text alone.
- import requests, base64, json, time, traceback. Get tok = notebookutils.credentials.getToken('pbi'). BASE_URL = f'https://api.fabric.microsoft.com/v1/workspaces/{workspace_id}'.
- Define a defensive _fabric_post(path, body) helper that returns None on non-2xx or caught exceptions. EVERY caller MUST do `if x is None: raise RuntimeError(...)` BEFORE `.get('id')`.
- Item naming derived from target_lakehouse_name: '<name>_sm' (SemanticModel), '<name>_rpt' (Report), '<name>_agent' (AISkill).
- 1) GET /lakehouses/{target_lakehouse_id} and poll until properties.sqlEndpointProperties.provisioningStatus == 'Success' (up to 5 min). Capture connectionString and the SQL endpoint id.
- 2) POST /items to create a Direct Lake SemanticModel (TMSL model.bim, compatibilityLevel 1604) with tables for each discovered gold table (use the actual columns), relationships (per spec), explicit measures (per spec, PascalCase, with FORMAT strings). payloadType='InlineBase64', payload=base64(utf-8(json.dumps(tmsl_obj))), path='model.bim'.
- 3) POST /items to create a Report (PBIR format) bound to the new SemanticModel id, with at least one page that uses the explicit measures.
- 4) POST /items to create an AISkill data agent. Wrap in try/except so AISkill being preview-only never crashes the notebook.
- 5) Print a final JSON summary line with semantic_model_id, report_id, data_agent_id (or null).
- Hard-fail (raise) on the SemanticModel step's failure; reporting/agent steps may degrade gracefully.",
            _ => throw new InvalidOperationException()
        };

        var system = $@"You are an expert Microsoft Fabric data engineer following the Microsoft Fabric e2e-medallion-architecture and powerbi-authoring-cli best practices. You generate the cells for ONE notebook (the {layer.ToUpperInvariant()} layer of a Bronze → Silver → Gold + reporting medallion).

You are operating as the FabricDataEngineer agent (https://github.com/microsoft/skills-for-fabric/blob/main/agents/FabricDataEngineer.agent.md). Apply ALL of its core principles: decompose by endpoint-specific stage, parameterize, use Delta everywhere, idempotent overwrite patterns, validation gates, error-loud try/except.

OUTPUT FORMAT (STRICT):
Return ONLY a JSON object with this exact shape:
{{
  ""cells"": [""<cell1 source code>"", ""<cell2 source code>"", ...]
}}
No prose, no markdown fences. Each cell is a complete Python source string. The platform supplies a parameters cell (workspace_id, source_workspace_id, source_lakehouse_id, target_lakehouse_id, target_lakehouse_name, source_tables_csv, run_id, spec_url) and a bootstrap cell defining src_base, tgt_base, source_tables, _save_error — DO NOT redefine these. Aim for 4-7 cells, ~80-120 lines total.

CELL COMMENTING (REQUIRED):
- Every cell MUST begin with a comment block:
    # ---
    # <one-line summary of the cell>
    # <optional 1-2 extra lines explaining intent>
- The actual Python code follows. Comments explain INTENT.

PLATFORM CONSTRAINTS:
- Spark abfss paths:
    src_base = f""abfss://{{source_workspace_id}}@onelake.dfs.fabric.microsoft.com/{{source_lakehouse_id}}""
    tgt_base = f""abfss://{{workspace_id}}@onelake.dfs.fabric.microsoft.com/{{target_lakehouse_id}}""
- The TARGET lakehouse IS SCHEMA-ENABLED. Layer separator is the schema prefix: bronze/, silver/, gold/, test/.
- Always use spark.read.format('delta').load(...) and DataFrame.write.format('delta')...save(...). NEVER saveAsTable.
- Schemas (bronze/silver/gold/test) get auto-created on first write — do NOT issue CREATE SCHEMA.
- Trust the notebook runtime identity for OneLake access. Never embed secrets.

{layerSpec}

CRITICAL — USE THE REAL PRIOR-LAYER SCHEMAS:
The user message includes 'prior_layer_schemas' — a JSON-like list of {{ table: ""col:type, col:type, ..."" }}. These are the ACTUAL columns currently in the previous layer's Delta tables (or, for bronze, the source-lakehouse tables). The notebook you generate MUST:
1. Use ONLY the columns listed there. If a column you would have referenced is missing, either: (a) skip it gracefully, (b) substitute a typed null literal (F.lit(None).cast('<type>')) — NEVER Python None passed into F.coalesce/F.greatest/F.least/F.when/F.concat/etc. (see Rule G in the spec's Generic guidance).
2. Trust the schemas over any spec text that contradicts them. If the spec says ""dim_customer joins customeraddress on customer_id"" but the silver customeraddress schema lacks customer_id, write the join only if you can satisfy it with what's present, otherwise raise a clear error from a defensive assertion.
3. Apply the cross-cutting rules from the spec's '## Generic guidance' section (especially the Rule A-G column-reference rules) to every transformation step.

ROBUSTNESS:
- Wrap each major step in try/except that calls _save_error('{layer}', e) and re-raises. Never swallow errors silently — failures must propagate so the auto-fixer gets an actionable traceback.
- Defensive REST handling (reporting layer only): every helper returns None on failure, every caller checks `if x is None: raise` BEFORE `.get('id')`.

Code size: aim for 4-7 cells, ~80-120 lines total. Return the JSON now.";

        // Compact prior-layer schemas into a readable block. Limit to the first ~25 tables x 4KB
        // worth so we don't blow the prompt budget on huge lakehouses.
        var schemaBlock = priorLayerSchemas.Count == 0
            ? "(none — this is bronze; the source tables are listed below)"
            : string.Join("\n", priorLayerSchemas
                .Take(40)
                .Select(kv => $"- `{kv.Key}`: {kv.Value}"));

        var user = $@"## Run parameters
workspace_id = {workspaceId}
source_workspace_id = {sourceWorkspaceId}
source_lakehouse_id = {sourceLakehouseId}
target_lakehouse_id = {targetLakehouseId}
target_lakehouse_name = {targetLakehouseName}
run_id = {runId}
source_tables_csv = {string.Join(",", sourceTables)}

## Prior-layer schemas (REAL columns currently present — trust over spec text)
{schemaBlock}

## Spec (markdown)
{specMarkdown}

Produce the JSON for the {layer} notebook now.";

        string answer;
        try { answer = await _llm.ChatAsync(system, user, maxTokens: 16000, temperature: 0.1, model: model, runId: runId); }
        catch (Exception ex) { _log.LogWarning(ex, "LLM layer-cell-gen failed for {layer}", layer); return null; }

        var json = ExtractJson(answer);
        if (json is null)
        {
            _log.LogWarning("Could not find JSON in LLM response for {layer}. Raw preview: {preview}",
                layer, answer.Length > 800 ? answer.Substring(0, 800) + "...(truncated)" : answer);
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("cells", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                var list = ReadStringArray(arr);
                if (list.Count > 0) return list;
            }
            // Be lenient: some models return { "<layer>": [...] } instead of { "cells": [...] }.
            if (doc.RootElement.TryGetProperty(layer, out var arr2) && arr2.ValueKind == JsonValueKind.Array)
            {
                var list = ReadStringArray(arr2);
                if (list.Count > 0) return list;
            }
            _log.LogWarning("LLM response for {layer} missing 'cells' array", layer);
            return null;
        }
        catch (Exception ex) { _log.LogWarning(ex, "LLM JSON parse failed for {layer}", layer); return null; }
    }

    /// <summary>
    /// Generate an initial spec markdown tailored to the user's picked source tables.
    /// Returned markdown follows the canonical 5-section structure (Generic / Medallion /
    /// Semantic / Report / Data Agent) so the frontend can split it into the per-section editors.
    /// </summary>
    public async Task<string?> ProposeSpecAsync(string runId, string workspaceId, string sourceLakehouseName,
        string sourceLakehouseId, List<string> sourceTables, string targetLakehouseName,
        IReadOnlyDictionary<string, string>? tableSchemas = null, string? model = null, string? initialSpecs = null)
    {
        if (!_llm.Configured) return null;
        var system = $@"You are a senior Microsoft Fabric data architect. Given the user's selected source tables AND their actual column schemas, produce a complete medallion build spec. The output is shown to the user in 5 editable sections — keep that structure intact.

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

THEN include the standard cross-cutting code rules: defensive column references, alias-prefixed joins after every join, groupBy/agg column existence assertion, defensive REST handling with `if x is None: raise` BEFORE `.get()`, no `saveAsTable`, parameter cells, idempotent overwrite patterns, error-loud try/except that calls `_save_error(layer, e)` and re-raises.

THEN include the following ""Global Spark column-reference rules"" subsection VERBATIM (copy the entire block below into the Generic guidance section, preserving headings, bullets, and rule labels A–F exactly as written — do not paraphrase, abbreviate, or reorder):

{GlobalSparkColumnRules}

ALSO REQUIRE for every generated notebook: EACH code cell must start with a short markdown comment block (Python `# ---` divider + 1-3 lines of `# ` comments) describing what the cell is doing and why — never emit a cell with no leading comment. Example:
```
# ---
# Read raw bronze table and apply schema enforcement.
# Drops rows where any required key is null.
df = spark.read.table(...)
```
Keep the comments human-readable, not the code repeated in prose. The goal: someone scrolling through the notebook in Fabric can understand each block at a glance.)

## Bronze
(how each source table is landed into the `bronze` schema — metadata columns, write mode, partitioning. Be specific based on the picked tables.)

## Silver
(cleaning, dedup keys, snake_case rename, audit columns, OPTIMIZE — for each table call out the dedup key you'd use based on the columns)

## Gold
(dims + facts — propose specific dim/fact tables based on the ACTUAL columns; identify which tables look like facts vs dims vs junction tables. Do NOT include data-quality tests here — they live in the separate ## Test section below.)

## Test
(data quality tests to run after Gold writes — include the 5 standard tests: row counts per layer (bronze ≈ silver within 1%), no-null PKs in gold dims, unique PKs in gold dims, referential integrity (every fact FK exists in its dim), and at least one business-rule sanity check tailored to the data. Each test appends one row to a `test/test_results` table with schema: run_id, test_name, layer, table_name, status (PASS/FAIL/ERROR), actual, expected, details, checked_at.)

## Semantic model
(Direct Lake star schema with explicit measures — propose tables, relationships, measures relevant to the actual columns you see)

## Report
(pages + visuals tailored to the modeled data; always include a Data quality page)

## Data Agent
(AISkill grounded on the semantic model with role, domain hints, starter questions, guardrails)
```

PROPOSAL RULES:
- Use the ACTUAL column lists provided to make join/PK/FK decisions. Do not invent columns.
- If a table has obvious FK columns (e.g., 'customer_id' and the customer table exists), use it as a join key.
- If a table has clear PK pattern (e.g., 'id', '{{tablename}}_id'), note it.
- For each non-obvious modeling decision, mention alternatives in the spec so the user can edit.
- Include standard data quality tests (row counts, no-null PKs, unique PKs, referential integrity for join keys you identified).
- For the semantic model: propose ~4-6 explicit DAX measures appropriate to the data.
- For the report: propose ~2-3 pages with concrete visual types tied to the measures.
- For the Data Agent: write a role description, 6-10 starter questions matching the actual data domain, and tight guardrails.
- Keep markdown clean. Aim for ~120-220 lines total.

INTENT OVER DETAILED COLUMN LISTS (IMPORTANT):
- The build pipeline runs each layer's notebook ONE AT A TIME and inspects the ACTUAL produced tables between layers. The per-layer notebook generator receives the real column lists at runtime and is instructed to trust those over any spec text that contradicts them.
- Therefore, the layer sections (Bronze/Silver/Gold/Test/Semantic/Report) should describe INTENT and SHAPE — what tables to produce, what joins to perform, what measures to expose, what data-quality tests to run — NOT exhaustive column-by-column field listings. Naming specific business-meaningful columns is fine and helpful (e.g. ""include cleaned sales_person""), but do not try to enumerate every column of every table; that information becomes stale the moment the bronze rename happens and the LLM will rediscover it at runtime anyway.
- Rule of thumb: if a sentence reads like a schema dump (""columns: id INT, name STRING, …""), trim it. If it reads like a design decision (""dedupe customer rows on customer_id, keeping the latest modified_date""), keep it.";

        var schemaSection = tableSchemas != null && tableSchemas.Count > 0
            ? string.Join("\n", sourceTables.Select(t =>
                tableSchemas.TryGetValue(t, out var s) && !string.IsNullOrEmpty(s)
                    ? $"- `{t}`\n    columns: {s}"
                    : $"- `{t}`\n    columns: (could not introspect)"))
            : string.Join("\n", sourceTables.Select(t => $"- `{t}`"));

        var initialSpecsBlock = string.IsNullOrWhiteSpace(initialSpecs)
            ? ""
            : $@"

## USER-PROVIDED INITIAL SPECS (HIGHEST PRIORITY)
The user has explicitly provided the following intent / requirements for this build. Treat this as the PRIMARY source of design intent. Your generated spec must satisfy every point below, and where it implies modelling choices (specific dim/fact names, dedup keys, measures, report visuals, agent persona…), bake them in concretely rather than offering alternatives. If anything in the initial specs CONTRADICTS what the schemas allow (e.g. user asks to dedup by 'email_lower' but no email column exists), call that out IN the spec under the relevant section (e.g. ""## Silver — NOTE: requested dedup on email_lower is not directly possible; falling back to ... unless the user clarifies"") rather than silently ignoring.

```
{initialSpecs!.Trim()}
```
";

        var user = $@"## Run parameters
runId: {runId}
workspace_id: {workspaceId}
source_lakehouse: {sourceLakehouseName} ({sourceLakehouseId})
target_lakehouse: {targetLakehouseName}

## Source tables WITH ACTUAL SCHEMAS
{schemaSection}
{initialSpecsBlock}
Produce the spec markdown now. Base every modeling decision on the columns above AND the user-provided initial specs (if any). Do not assume any specific known data model.";

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
1. The current spec markdown (the user's build instructions, split into 8 sections: Generic guidance, Bronze, Silver, Gold, Test, Semantic model, Report, Data Agent).
2. The Spark traceback from the cell that failed.
3. Optional metadata: which iteration this is, which layer failed, and the run id.

Your job: produce an UPDATED spec markdown that prevents this specific failure on the next build. Be SURGICAL — keep the user's intent intact, only tighten the relevant section(s) of the spec with explicit constraints, column names, join keys, or data-shape clarifications.

Rules:
- Return ONLY the updated spec markdown. No prose around it, no JSON, no code fences wrapping the whole output.
- Preserve the existing top-level structure exactly: the '# Run Spec ...' heading, '## Inputs', '## Generic guidance', '## Bronze', '## Silver', '## Gold', '## Test', '## Semantic model', '## Report', '## Data Agent'. Do not delete the Inputs block.
- ## Generic guidance: you MAY revise it (the user trusts the LLM here). If the failure exposes a new cross-cutting rule, add it. Keep the skill/agent URL list intact at the top.
- Sharpen language in the relevant LAYER section (Bronze/Silver/Gold/Semantic/Report/DataAgent) that caused the failure (UNRESOLVED_COLUMN → name the column; AMBIGUOUS_REFERENCE → alias-prefix joins; missing junction-table column → name the real columns).
- CROSS-TABLE ROOT-CAUSE ANALYSIS — REQUIRED: before writing the fix, list every source table referenced in the '## Inputs' section of the spec and ask yourself: ""Could this same root cause hit ANY of the other tables on the next run?"" Many failures (snake_case rename collisions, columns that disappear after a select(), ambiguous join keys, NULL handling, type coercion, missing columns on junction tables) are SYSTEMIC. If the root cause is plausibly systemic across multiple tables, do ONE of the following — never both:
   (a) GENERALIZE — rewrite the relevant layer section with a single rule that defensively handles the issue for ALL tables (e.g. ""for every source table T, after the select() projection, assert column X exists; if absent, fall back to ...""). Prefer this when the rule is uniform.
   (b) ENUMERATE — list each affected table by name with its specific fix, in a sub-bullet list under the failing layer's section. Prefer this when each table needs a different concrete change (e.g. different column names, different keys).
  In your '## Updated specs' changelog entry, explicitly state which approach you took and why, AND list the other tables you considered (even if you decided they were not affected). This makes the analysis auditable.
- CRITICAL — UPSTREAM PRESERVATION: if 'failed_layer' is provided, you MUST NOT modify any layer section UPSTREAM of it (those layers already succeeded and their Delta tables are still in the lakehouse — the next build will resume FROM the failed_layer, so any upstream edits would be silently ignored). Upstream order is: bronze → silver → gold → reporting (semantic/report/agent live inside reporting). If you truly believe the root cause is in an upstream layer, do not edit upstream sections; instead, tighten the failed_layer section to defensively handle the upstream shape (e.g. coerce types, alias columns, add guards). The Generic guidance section MAY still be revised — it is cross-cutting, not a layer.
- Prepend a NEW section at the very top (between the '# Run Spec' line and '## Inputs') called '## Updated specs' that documents this change. Format:
  ```
  ## Updated specs

  ### Iteration <N> — <UTC timestamp> — failed layer: <layer> (run: <runId>)
  - **Root cause (1-line summary)**: <e.g. AMBIGUOUS_REFERENCE on 'customer_id' in dim_customer build>
  - **Cross-table audit**: <list every other source table you considered and whether the same root cause could hit it (yes/no + 1-line reason each). E.g. ""customeraddress: yes — also has customer_id; salesorderheader: yes — same; address: no — no customer_id column.""
  - **Fix approach**: <GENERALIZE or ENUMERATE — and why you chose it>
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
    /// User has just edited one section of the spec (e.g. Gold). Re-propose the
    /// downstream sections that depend on it so the chain stays consistent.
    /// Layer order: bronze → silver → gold → semantic → report → agent.
    /// "generic" doesn't trigger downstream regen (it's cross-cutting guidance).
    /// Returns the FULL updated spec markdown with downstream sections rewritten and
    /// the user's edited section + everything upstream preserved verbatim.
    /// </summary>
    public async Task<string?> PropagateDownstreamAsync(string currentSpec, string editedSection, string? model = null, string? runId = null)
    {
        if (!_llm.Configured) return null;
        var downstreamMap = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["bronze"]   = new[] { "Silver", "Gold", "Test", "Semantic model", "Report", "Data Agent" },
            ["silver"]   = new[] { "Gold", "Test", "Semantic model", "Report", "Data Agent" },
            ["gold"]     = new[] { "Test", "Semantic model", "Report", "Data Agent" },
            ["test"]     = new[] { "Semantic model", "Report", "Data Agent" },
            ["semantic"] = new[] { "Report", "Data Agent" },
            ["report"]   = new[] { "Data Agent" },
            ["agent"]    = Array.Empty<string>(),
            ["generic"]  = Array.Empty<string>(),
        };
        if (!downstreamMap.TryGetValue(editedSection, out var downstream) || downstream.Length == 0)
        {
            // No downstream to update — return the current spec unchanged.
            return currentSpec;
        }

        var downstreamList = string.Join(", ", downstream.Select(s => "## " + s));
        var system = $@"You are an expert Microsoft Fabric data architect maintaining the consistency of a multi-section build spec.

The user has just edited one section. Your job is to re-propose ONLY the downstream sections so they remain consistent with the upstream edit.

Edited section (upstream): ## {Capitalize(editedSection)}
Downstream sections to re-propose: {downstreamList}

STRICT RULES:
1. Return ONLY the full updated spec markdown. No prose, no code fences wrapping the whole thing, no JSON.
2. Preserve the existing top-level structure exactly: '# Run Spec ...', any '## Updated specs', '## Inputs', '## Generic guidance', '## Bronze', '## Silver', '## Gold', '## Test', '## Semantic model', '## Report', '## Data Agent'.
3. KEEP VERBATIM all sections at or upstream of the edited section ('## {Capitalize(editedSection)}' and everything above it). Do not modify Inputs, Generic guidance, or any upstream layer's content.
4. REWRITE the downstream sections ({downstreamList}) so they are consistent with the edited section. Be specific: name columns, tables, measures based on what the upstream now says. Don't invent things not implied by the upstream content.
5. Keep section ordering exactly the same.
6. Do NOT add a '## Updated specs' changelog entry for this — that section is reserved for build-failure auto-fix.";

        var user = $@"## Current full spec
{currentSpec}

The user just edited '## {Capitalize(editedSection)}'. Re-propose {downstreamList} to stay consistent. Return the entire updated spec markdown now.";

        try
        {
            var raw = await _llm.ChatAsync(system, user, maxTokens: 16000, temperature: 0.2, model: model, runId: runId);
            if (string.IsNullOrWhiteSpace(raw)) return currentSpec;
            // Defensive merge: never let a malformed LLM response wipe the user's editor.
            // The LLM is supposed to return the full updated spec, but it sometimes truncates,
            // wraps the answer in code fences, or only emits the downstream block. We:
            //   1. Strip a single pair of leading/trailing ```...``` code fences if present.
            //   2. Parse both `currentSpec` and the LLM response into sections.
            //   3. For sections AT or UPSTREAM of the edited section → keep verbatim from currentSpec.
            //   4. For downstream sections → take from the LLM response if non-empty; otherwise
            //      fall back to currentSpec's existing version so the editor never goes blank.
            // The recombined markdown always has all known sections, in canonical order.
            return MergePropagatedSpec(currentSpec, raw, editedSection);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "PropagateDownstream LLM call failed");
            return currentSpec;
        }
    }

    // Canonical section order for the spec markdown. Lowercase keys match the SECTION_PATTERNS in the frontend.
    private static readonly (string Key, string Heading)[] SpecSections = new[]
    {
        ("generic", "## Generic guidance"),
        ("bronze", "## Bronze"),
        ("silver", "## Silver"),
        ("gold", "## Gold"),
        ("test", "## Test"),
        ("semantic", "## Semantic model"),
        ("report", "## Report"),
        ("agent", "## Data Agent"),
    };

    // Strip a single pair of leading/trailing fenced code blocks (```...```) wrapping the whole text.
    private static string StripOuterCodeFences(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return s;
        var trimmed = s.Trim();
        var m = Regex.Match(trimmed, @"^```[A-Za-z0-9_-]*\s*\r?\n(?<body>[\s\S]*?)\r?\n```\s*$");
        return m.Success ? m.Groups["body"].Value : s;
    }

    // Split a spec into (header, sectionMap) by H2 headings. 'header' is everything before the
    // first known section heading (preserves '# Run Spec', '## Updated specs', '## Inputs').
    private static (string header, Dictionary<string, string> sections) SplitSpec(string spec)
    {
        var sections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, _) in SpecSections) sections[k] = "";
        if (string.IsNullOrWhiteSpace(spec)) return ("", sections);

        var headingRx = new Dictionary<string, Regex>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, h) in SpecSections)
            headingRx[k] = new Regex("^" + Regex.Escape(h) + @"\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline);

        var lines = spec.Split('\n');
        var slots = new List<(int line, string key)>();
        for (int i = 0; i < lines.Length; i++)
        {
            foreach (var (k, rx) in headingRx)
                if (rx.IsMatch(lines[i])) slots.Add((i, k));
        }
        slots.Sort((a, b) => a.line.CompareTo(b.line));
        if (slots.Count == 0) return (spec.TrimEnd(), sections);

        var header = string.Join('\n', lines, 0, slots[0].line).TrimEnd();
        for (int i = 0; i < slots.Count; i++)
        {
            var start = slots[i].line;
            var end = i + 1 < slots.Count ? slots[i + 1].line : lines.Length;
            sections[slots[i].key] = string.Join('\n', lines, start, end - start).TrimEnd();
        }
        return (header, sections);
    }

    private static string MergePropagatedSpec(string currentSpec, string llmResponse, string editedSection)
    {
        var cleaned = StripOuterCodeFences(llmResponse);
        var (curHeader, curSections) = SplitSpec(currentSpec);
        var (newHeader, newSections) = SplitSpec(cleaned);

        // editedSection determines the boundary. Sections AT or BEFORE this index are preserved
        // from the current spec; sections AFTER are taken from the LLM response (with fallback).
        var orderKeys = SpecSections.Select(x => x.Key).ToArray();
        // Map 'generic' edit → editedIdx = 0 ; 'agent' → last. Unknown → -1 (defensive: keep all current).
        int editedIdx = Array.FindIndex(orderKeys, k => string.Equals(k, editedSection, StringComparison.OrdinalIgnoreCase));

        var merged = new Dictionary<string, string>(curSections, StringComparer.OrdinalIgnoreCase);
        if (editedIdx >= 0)
        {
            for (int i = editedIdx + 1; i < orderKeys.Length; i++)
            {
                var k = orderKeys[i];
                var fromLlm = newSections.TryGetValue(k, out var v) ? v : "";
                merged[k] = !string.IsNullOrWhiteSpace(fromLlm) ? fromLlm : curSections[k];
            }
        }

        // Recombine using the CURRENT spec's header (we never let the LLM rewrite the # Run Spec /
        // ## Updated specs / ## Inputs block, even if it tried).
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(curHeader)) sb.AppendLine(curHeader).AppendLine();
        foreach (var (k, _) in SpecSections)
        {
            var body = merged[k];
            if (!string.IsNullOrWhiteSpace(body))
            {
                sb.AppendLine(body.TrimEnd());
                sb.AppendLine();
            }
        }
        return sb.ToString().TrimEnd() + "\n";
    }

    private static string Capitalize(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s.Substring(1);

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
