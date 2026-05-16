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

## Build instructions

You are a data agent, and you use your skills: https://github.com/microsoft/skills-for-fabric/blob/main/agents/FabricDataEngineer.agent.md

In silver I want to add fields for `ingestion_dt` and `source_dt`.

In gold I want to have the following tables:

- **Dim_Customer** — join customer, customer address, address and leave all relevant/meaningful fields.
- **Dim_Product** — join product, productcategory, productdescription, productmodel, productmodelproductdescription. Please notice that in productcategory there is a self join: in the end I have the category (which is the parent category) and a subcategory.
- **Dim_Sales** — Use the salesperson field from salesorderheader, but remove the `adventureworks/` in front of the name and remove the last number at the end of the name. The result is a degenerate dimension with one row per distinct cleaned salesperson.
- **Fact_Sales** — join salesorderdetail and salesorderheader and keep all relevant fields. **MUST also include the cleaned `sales_person` column (same cleaning rule as Dim_Sales)** so that downstream reports can aggregate sales and discounts by salesperson.

Use only one OneLake for bronze, silver and gold; use schemas to separate them. Also use 3 different notebooks (one per layer).

## Data quality tests

Add a fourth schema called `test` and write a results table `test.test_results` after the gold layer completes. The table must have these columns: run_id (string), test_name (string), layer (string), table_name (string), status (PASS/FAIL), actual (string), expected (string), details (string), checked_at (timestamp). Append rows (don't overwrite) so history is preserved.

Run AT LEAST these tests:
1. Row counts per layer — for each selected source table, count rows in bronze, silver and gold; bronze and silver counts must match within 1%.
2. No-null primary keys in gold dims (dim_customer.customer_id, dim_product.product_id, dim_sales.sales_person).
3. Unique primary keys in gold dims (count == distinct count).
4. Referential integrity — every fact_sales FK exists in the corresponding dim.
5. Salesperson cleaning sanity — no dim_sales.sales_person value contains 'adventureworks/' (case-insensitive) and none end with a digit.

The Power BI report should include a 'Data quality' page showing the latest test_results rows.

## Semantic model & reports

Create a semantic model on top of the gold layer where you can count the distinct salespersons. Also I want to know which salespersons sell the most, but also give the most discounts. Create some reports on top of this!
";
}
