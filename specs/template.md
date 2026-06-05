# Run Spec {{RUN_ID}}

## Inputs
- Workspace: `{{WORKSPACE_ID}}`
- Source Lakehouse: **{{SOURCE_LAKEHOUSE_NAME}}** (`{{SOURCE_LAKEHOUSE_ID}}`)
- Tables to ingest into Bronze:
{{TABLES_LIST}}
- Target Lakehouse: **{{TARGET_LAKEHOUSE_NAME}}**

## Generic guidance

You are a data agent following the FabricDataEngineer agent (https://github.com/microsoft/skills-for-fabric/blob/main/agents/FabricDataEngineer.agent.md). Apply ALL of its principles: decompose into endpoint-specific stages, parameterize environments, never hard-code IDs/secrets, use Delta Lake everywhere, separate raw/validated/serving layers, validation gates between layers, idempotent overwrite patterns.

Also apply these reference skills (use their patterns from training):
- https://github.com/microsoft/skills-for-fabric/tree/main/skills/e2e-medallion-architecture
- https://github.com/microsoft/skills-for-fabric/tree/main/skills/spark-authoring-cli
- https://github.com/microsoft/skills-for-fabric/tree/main/skills/powerbi-authoring-cli
- https://github.com/microsoft/skills-for-fabric/tree/main/skills/powerbi-consumption-cli
- https://github.com/RuiRomano/powerbi-agentic-plugins/tree/main/plugins/powerbi/skills/powerbi-semantic-model-authoring
- https://github.com/RuiRomano/powerbi-agentic-plugins/tree/main/plugins/powerbi/skills/powerbi-report-authoring

### Cross-cutting code rules (apply in every notebook)

- **Defensive column references** — never assume a column exists. AdventureWorksLT junction tables (CustomerAddress, ProductModelProductDescription) only have FK columns, no own ID columns. CamelCase IDs become snake_case after silver rename (SalesOrderID → sales_order_id).
- **Ambiguous columns after joins** — always alias both sides of joins and reference columns with the alias prefix: `F.col('c.customer_id')`, never bare `F.col('customer_id')` when both joined tables have that name. Use Python attribute access on aliased DFs in join conditions: `c.customer_id == ca.customer_id`.
- **GroupBy / aggregation safety** — before any `df.groupBy(col)` or `df.agg(...)`, assert the column exists in `df.columns`. If the spec asks for aggregations by a column, the FACT table MUST materialize that column.
- **Error-loud** — every layer wraps major steps in try/except that calls `_save_error('<layer>', e)` and re-raises. Never swallow per-table errors into a results dict and continue.
- **Defensive REST handling (reporting notebook)** — every REST helper returns `None` on failure (non-2xx, caught exception). Callers MUST check `if x is None: raise RuntimeError(...)` BEFORE calling `.get(...)` on the result. Never write `something.get('id')` without a None check first.
- **Use saveAsTable for writes (schema-enabled lakehouse)** — the target lakehouse is schema-enabled and attached as the notebook's default lakehouse, so write Delta with schema-qualified `df.write.format('delta').mode('overwrite').option('overwriteSchema','true').saveAsTable('<schema>.<table>')` after `spark.sql('CREATE SCHEMA IF NOT EXISTS <schema>')`. Do NOT write target tables via raw abfss `.save(path)` — on a schema-enabled lakehouse that lands the data at a broken nested `Tables/Tables/<schema>/<table>` path the SQL endpoint and discovery cannot see. (Reads may use `spark.read.table('<schema>.<table>')` or abfss `.load(...)`.)
- **Parameter cell** — every notebook receives a platform-injected parameters cell with `workspace_id, source_workspace_id, source_lakehouse_id, target_lakehouse_id, target_lakehouse_name, source_tables_csv, run_id, spec_url`. Don't redefine these.

### Global Spark column-reference rules (apply to ALL layers: Bronze, Silver, Gold)
These rules exist to prevent recurring `UNRESOLVED_COLUMN` / `AnalysisException` analyzer errors. They are layer-agnostic — apply them anywhere a Spark DataFrame is transformed.

Rule A — No dotted alias strings.
- Never pass dotted strings like "c.customer_id", "ca.address_type", "h.sales_person", or "pc_child.name" to F.col(...), withColumn(...), Window.partitionBy(...), Window.orderBy(...), or select(...). Spark treats "c.customer_id" as a single column literally named c.customer_id, which does not resolve once any projection or rename has been applied.
- Alias scope (.alias("c"), .alias("ca"), ...) is only valid inside the SAME select / join expression that introduces it. Once you produce a new DataFrame via select(...) or withColumn(...), the dotted alias form is gone and you must reference plain column names.

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
- For every withColumn(name, expr) and every Window definition, confirm: "Every column referenced inside expr / inside the window's partitionBy / orderBy exists on the DataFrame at this exact point." If not, fix per Rule C before generating the code.

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

Rule H — Per-table isolation; one table's failure must not cancel the Spark session for the rest.
- Spark cancels the entire session when one statement crashes. If your notebook builds a single chained plan that touches every source table (one big SELECT, one big DataFrame, one big SQL script), any one table's failure kills ALL tables.
- ALWAYS process source tables in a `for tbl in source_tables:` loop where each iteration is a SELF-CONTAINED unit: read → transform → write → record-result → recover. Wrap the loop body in `try/except` that calls `_save_error(layer, e, table=tbl)` and APPENDS the failure to a results dict, then re-raises only AFTER the loop has attempted all tables (or, if your spec says "fail-fast-first-table", re-raise immediately — but per-table-isolated by default).
- Do NOT build a single multi-CTE Spark SQL statement that joins/transforms many source tables in one shot. Each table's transform is its own DataFrame chain with its own `.write` call.
- Do NOT share intermediate temp views across tables. Temp views from one iteration must not be assumed to exist in the next. If you need cross-table joins (typical for Gold), do them in a SECOND loop AFTER all per-table Silver/Gold writes are complete.

Rule I — Optional audit columns on junction / bridge / view tables.
- In typical operational sources (AdventureWorksLT, Northwind, AdventureWorks2019, etc.), entity tables (Customer, Product, SalesOrderHeader) have system audit columns: `ModifiedDate`, `rowguid`. **Junction / bridge tables** (CustomerAddress, ProductModelProductDescription, SalesTerritoryHistory) typically have only the FK columns and may have NO ModifiedDate and NO rowguid. **Views** (vGetAllCategories, vProductAndDescription) may have whatever columns the underlying query projects — frequently NO audit columns.
- When you write Silver dedup / tie-break / audit logic, you MUST NOT assume `modified_date` (or any other audit column) exists on every table. Use `'modified_date' in df.columns` as a guard and fall back to:
  - For dedup: a deterministic ranking expression that uses only the natural-key columns (`row_number().over(Window.partitionBy(*pk_cols).orderBy(*pk_cols))`), OR a literal F.lit(timestamp).
  - For `source_dt` / `_silver_ts`: a typed null literal (`F.lit(None).cast('timestamp')`) or `F.current_timestamp()`.
- Junction tables: dedupe on the COMPOSITE FK key (e.g. `(customer_id, address_id, address_type)`) — never on a non-existent surrogate key.
- View tables: project ONLY the columns actually returned by the view. Do not assume any standard naming.

Rule J — Validate column existence BEFORE the expensive transform.
- For every join, withColumn, groupBy, agg, or filter that names a specific column, ASSERT the column exists in `df.columns` BEFORE the line that uses it. Pattern:
  ```
  for required in ('customer_id', 'order_date'):
      if required not in df.columns:
          raise RuntimeError(f"[{layer}] {tbl}: required column '{required}' missing; available={df.columns}")
  # … now the join / withColumn that uses customer_id and order_date
  ```
- Catches missing-column bugs in a SPECIFIC cell with a SPECIFIC table name, instead of a session-wide Spark cancellation 30 minutes later that the auto-fixer can't pinpoint.
- Especially important AFTER a select(), drop(), or rename() — re-validate before the next consumer of those columns.

Rule K — Resilience to partial output: every layer MUST write Delta tables the next layer can discover.
- The build pipeline runs each layer's notebook then inspects the lakehouse for the layer's output Delta tables before generating the next layer. If Bronze runs "successfully" (Spark Completed) but writes zero discoverable tables in the `bronze` schema, the build hard-fails with "prior layer produced no discoverable tables".
- The target lakehouse is SCHEMA-ENABLED and attached as the default lakehouse. To guarantee discoverability, the Bronze notebook MUST:
  - Run `spark.sql('CREATE SCHEMA IF NOT EXISTS bronze')`, then write every source table via `df.write.format('delta').mode('overwrite').option('overwriteSchema','true').partitionBy(...).saveAsTable(f"bronze.<flat>")`, where `<flat>` is the lowercased last segment of `table_relative_path`. NEVER write target tables with abfss `.save(path)` (a raw .save() to `Tables/bronze/<t>` lands at a broken nested `Tables/Tables/bronze/<t>` path discovery + SQL endpoint cannot see).
  - Print a final summary line `print(json.dumps({"bronze_results": {<table>: {"rows": N, "target": "bronze.<flat>"}, ...}}))` listing every table actually written. Use this as a self-check.
  - Raise (not just log) if zero tables were written by the end of the notebook.
- Same rule applies recursively to Silver (`silver.<table>`) and Gold (`gold.<table>` + `test.test_results`) — each via `CREATE SCHEMA IF NOT EXISTS` + saveAsTable.

Rule L — Disambiguate shared columns in join projections (avoid AMBIGUOUS_REFERENCE).
- When you `select(...)` directly off a join whose sides share a column name, selecting that column as a BARE string raises `[AMBIGUOUS_REFERENCE]` and cancels the whole Spark session. Typical Gold offenders: joining product `p` with product_model `m` (both expose `product_model_id`), or product `p` with product_category `pc` (both expose `product_category_id`), or any dimension built from several aliased source tables that carry the same key.
- Inside the SAME join+select expression the alias scope is still live, so reference EVERY shared/overlapping column with its alias and rename it explicitly: `F.col('p.product_model_id').alias('product_model_id')`, `F.col('pc.parent_product_category_id').alias('category_id')`. A bare string in a join `select` is ONLY safe for a column that exists on EXACTLY ONE side of the join.
- Before emitting a join's select list, enumerate the columns on each side; for any name present on more than one side, alias-qualify the side you want. When in doubt in a multi-table join, alias-qualify ALL columns in the select list — it is always safe and never ambiguous.
- This is the in-join counterpart to Rule A: dotted alias references (`F.col('p.col')`) are valid ONLY inside the join/select that introduces the alias; once the joined DataFrame has been materialized by that select, switch back to plain, already-renamed column names (Rule A).
- Concrete failure to avoid: `.select(F.col('p.product_id').alias('product_id'), 'product_number', 'product_model_id', ...)` after `p.join(m, ...)` — `product_model_id` exists on both `p` and `m`, so it MUST be `F.col('p.product_model_id').alias('product_model_id')` (or the `m.` side), never the bare `'product_model_id'`.

## Bronze

1:1 ingestion of the selected source tables into the `bronze` schema, adding `ingestion_timestamp`, `source_path`, `batch_id`, `ingestion_date` metadata columns. Mode: overwrite + overwriteSchema=true. Partitioned by `ingestion_date`.

## Silver

Dedupe on natural key (per table), snake_case rename, drop fully-null columns, trim strings, add `ingestion_dt` and `source_dt` audit columns plus `_silver_ts` timestamp. Mode: overwrite. After write run OPTIMIZE.

## Gold

AdventureWorksLT shape:

- **Dim_Customer** — join customer + customeraddress + address; keep all relevant fields.
- **Dim_Product** — join product + productcategory (self-join: subcategory → parent category) + productdescription + productmodel + productmodelproductdescription; expose `category` and `subcategory`.
- **Dim_Sales** — degenerate dimension on the cleaned salesperson from salesorderheader (strip leading `adventureworks/`, strip trailing digits).
- **Fact_Sales** — join salesorderdetail + salesorderheader, keep all relevant fields, AND include the cleaned `sales_person` column (same cleaning as Dim_Sales) so reports can aggregate by it.

## Test

Data quality tests run by the gold notebook AFTER the gold tables are written. Write rows to a `test` schema, table `test_results` with columns (run_id, test_name, layer, table_name, status, actual, expected, details, checked_at). Use APPEND mode so history is preserved. Each test wraps in try/except — failures become `FAIL`/`ERROR` rows, not crashed cells. Run at minimum:

1. Row counts per layer (bronze ≈ silver within 1%).
2. No-null primary keys in gold dims.
3. Unique primary keys in gold dims.
4. Referential integrity (every fact_sales FK exists in its dim).
5. Salesperson cleaning sanity (no `adventureworks/`, no trailing digit).

## Semantic model

Create a **Direct Lake** semantic model on the gold tables, with:
- Tables: `dim_customer`, `dim_product`, `dim_sales`, `fact_sales`.
- Relationships:
  - `fact_sales[customer_id]` → `dim_customer[customer_id]` (many-to-one)
  - `fact_sales[product_id]` → `dim_product[product_id]` (many-to-one)
  - `fact_sales[sales_person]` → `dim_sales[sales_person]` (many-to-one)
- Explicit measures (PascalCase, with format strings):
  - `[Total Sales] = SUM(fact_sales[line_total])` — currency
  - `[Total Discount Amount] = SUMX(fact_sales, fact_sales[line_total] * fact_sales[unit_price_discount])` — currency
  - `[Discount %] = DIVIDE([Total Discount Amount], [Total Sales])` — percent
  - `[Distinct Salespersons] = DISTINCTCOUNT(dim_sales[sales_person])`
  - `[Sales Count] = COUNTROWS(fact_sales)`
- Mark `fact_sales[order_date]` as a date column so time intelligence works without a separate date dim.
- Hide all FK columns from the report view.

## Report

Create a Power BI report bound to the semantic model above. Pages:

1. **Sales overview** — card visuals for `[Total Sales]`, `[Distinct Salespersons]`, `[Sales Count]`; bar chart "Top salespersons by sales" (axis = `dim_sales[sales_person]`, value = `[Total Sales]`, sort desc, top 10); bar chart "Top salespersons by discount given" (axis = `dim_sales[sales_person]`, value = `[Total Discount Amount]`, sort desc, top 10); line chart sales by month.
2. **Product mix** — matrix of `[Total Sales]` by `dim_product[category]` rows × `dim_product[subcategory]` columns; bar chart top 10 products by `[Total Sales]`.
3. **Data quality** — table visual showing the latest `test_results` (filter to MAX(run_id)), columns: layer, table_name, test_name, status, actual, expected; conditional formatting on `status` (green=PASS, red=FAIL, amber=ERROR).

## Data Agent

Create a **Data Agent (AISkill)** grounded on the semantic model created above (not on the raw tables), so it answers through curated measures and relationships.

**System instructions for the agent:**

> You are a sales analytics assistant for the AdventureWorksLT business. Answer questions about sales performance, salespeople, products, customers, and data quality. Always use the curated semantic-model measures (`Total Sales`, `Total Discount Amount`, `Discount %`, `Distinct Salespersons`, `Sales Count`) and the cleaned `dim_sales[sales_person]` dimension. NEVER aggregate columns directly from `fact_sales` when an equivalent measure exists.
>
> When asked "who", return the cleaned salesperson name (no `adventureworks/` prefix, no trailing digit). When asked "how much", format numbers as currency with two decimals. When asked about data quality, query the latest `test_results` (filter to MAX(run_id)) and summarise PASS/FAIL counts per layer and table. If the question cannot be answered from this model, say so clearly and suggest a follow-up. Be concise (3-5 sentences) unless the user asks for a deep dive.

**Domain context to inject:**

- "Salesperson names are stored cleaned in `dim_sales[sales_person]`; the raw value in `salesorderheader[sales_person]` had an `adventureworks/` prefix and a trailing employee-number digit, both stripped."
- "Discount math: line discount amount = `fact_sales[line_total] * fact_sales[unit_price_discount]`. Use `[Total Discount Amount]` rather than inline math."
- "There is no separate date dimension — use `fact_sales[order_date]` for time aggregations."
- "Data quality lives in the `test` schema, table `test_results`, one row per (run_id, test_name)."

**Starter / example questions:**

- "Who are the top 5 salespersons by total sales?"
- "Which salespersons give the largest discounts (by total discount amount and as % of their sales)?"
- "How many distinct salespersons are there?"
- "What is the average order value by salesperson?"
- "Which products generate the most revenue?"
- "Which product category drives the most discount give-away?"
- "Show me sales trend by month over the last year."
- "What is the latest data quality status? Are any tests failing?"

**Guardrails:**

- Refuse questions outside this dataset (no external lookups, no other businesses).
- Do not invent column names that aren't in the semantic model — if a metric isn't exposed, propose adding it and stop.

