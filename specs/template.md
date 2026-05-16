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
- **No saveAsTable** — schema-enabled lakehouses don't have a default DB context for unqualified saves. Always write Delta via `df.write.format('delta').save(abfss_path)`.
- **Parameter cell** — every notebook receives a platform-injected parameters cell with `workspace_id, source_workspace_id, source_lakehouse_id, target_lakehouse_id, target_lakehouse_name, source_tables_csv, run_id, spec_url`. Don't redefine these.

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

### Data quality tests (in the gold notebook)

Add a `test` schema with table `test_results` (run_id, test_name, layer, table_name, status, actual, expected, details, checked_at). APPEND rows so history is preserved. Run at minimum:

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

