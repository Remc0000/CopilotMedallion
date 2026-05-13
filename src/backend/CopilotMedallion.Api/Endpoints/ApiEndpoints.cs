using System.Text.Json;
using CopilotMedallion.Api.Models;
using CopilotMedallion.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace CopilotMedallion.Api.Endpoints;

public static class ApiEndpoints
{
    public static IEndpointRouteBuilder MapApiEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api");

        g.MapGet("/health", () => Results.Ok(new { ok = true, ts = DateTime.UtcNow }));

        g.MapGet("/config", (IConfiguration cfg) => Results.Ok(new
        {
            tenantId = cfg["AzureAd:TenantId"],
            clientId = cfg["AzureAd:ClientId"],
            workspaceId = cfg["Fabric:WorkspaceId"],
            scope = cfg["Fabric:Scope"],
            runsRepo = $"{cfg["GitHub:Owner"]}/{cfg["GitHub:RunsRepo"]}"
        }));

        g.MapGet("/sources/lakehouses", async (HttpRequest req, FabricService fabric) =>
        {
            var tok = req.Headers["X-Fabric-Token"].ToString();
            if (string.IsNullOrEmpty(tok)) return Results.Unauthorized();
            return Results.Ok(await fabric.ListLakehousesAsync(tok));
        });

        g.MapGet("/sources/lakehouses/{id}/tables", async (string id, HttpRequest req, FabricService fabric) =>
        {
            var tok = req.Headers["X-Fabric-Token"].ToString();
            if (string.IsNullOrEmpty(tok)) return Results.Unauthorized();
            var onelakeTok = req.Headers["X-Onelake-Token"].ToString();
            try
            {
                return Results.Ok(await fabric.ListTablesAsync(tok, id, string.IsNullOrEmpty(onelakeTok) ? null : onelakeTok));
            }
            catch (Exception ex)
            {
                return Results.Problem(title: "ListTables failed", detail: ex.Message, statusCode: 500);
            }
        });

        g.MapPost("/specs/preview", async ([FromBody] PreviewSpecsRequest body, HttpRequest req,
                                            FabricService fabric, SpecGenerator gen) =>
        {
            var tok = req.Headers["X-Fabric-Token"].ToString();
            if (string.IsNullOrEmpty(tok)) return Results.Unauthorized();
            var lakes = await fabric.ListLakehousesAsync(tok);
            var src = lakes.FirstOrDefault(l => l.Id == body.SourceLakehouseId);
            if (src is null) return Results.NotFound("source lakehouse not in workspace");
            var runId = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("n").Substring(0,6)}";
            var targetName = string.IsNullOrWhiteSpace(body.TargetLakehouseName)
                ? $"CopilotMedallion_{runId.Substring(0,15).Replace("-","_")}"
                : body.TargetLakehouseName!;
            var md = gen.Generate(runId, src, body.Tables, targetName, fabric.WorkspaceId);
            return Results.Ok(new PreviewSpecsResponse(md, runId, targetName));
        });

        g.MapPost("/specs", async ([FromBody] GenerateSpecsRequest body, HttpRequest req,
                                    FabricService fabric, SpecGenerator gen, GitHubService gh, IRunStore store) =>
        {
            var tok = req.Headers["X-Fabric-Token"].ToString();
            if (string.IsNullOrEmpty(tok)) return Results.Unauthorized();

            var lakes = await fabric.ListLakehousesAsync(tok);
            var src = lakes.FirstOrDefault(l => l.Id == body.SourceLakehouseId);
            if (src is null) return Results.NotFound("source lakehouse not in workspace");

            var runId = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("n").Substring(0,6)}";
            var targetName = string.IsNullOrWhiteSpace(body.TargetLakehouseName)
                ? $"CopilotMedallion_{runId.Substring(0,15).Replace("-","_")}"
                : body.TargetLakehouseName!;

            // Caller-supplied spec markdown wins; otherwise fall back to template.
            var spec = string.IsNullOrWhiteSpace(body.SpecMarkdown)
                ? gen.Generate(runId, src, body.Tables, targetName, fabric.WorkspaceId)
                : body.SpecMarkdown!;

            if (!gh.Configured)
            {
                await store.CreateAsync(runId, "(local)", "(GITHUB_PAT not configured)",
                                         body.SourceLakehouseId, string.Join(",", body.Tables), targetName);
                return Results.Ok(new GenerateSpecsResponse(runId, "(local)", "(GITHUB_PAT not configured)", "(local)"));
            }

            var (branch, blobUrl, rawUrl) = await gh.PushSpecAsync(runId, spec);
            await store.CreateAsync(runId, branch, blobUrl,
                                     body.SourceLakehouseId, string.Join(",", body.Tables), targetName);
            return Results.Ok(new GenerateSpecsResponse(runId, branch, blobUrl, rawUrl));
        });

        g.MapPost("/build", async ([FromBody] BuildRequest body, HttpRequest req,
                                     FabricService fabric, IRunStore store, IWebHostEnvironment env, ILoggerFactory lf) =>
        {
            var tok = req.Headers["X-Fabric-Token"].ToString();
            if (string.IsNullOrEmpty(tok)) return Results.Unauthorized();

            var run = await store.GetAsync(body.RunId);
            if (run is null) return Results.NotFound();

            await store.UpdateStatusAsync(run.RunId, "Queued");

            // Fire-and-forget: do the long-running work in the background, return immediately.
            // App Service request timeout (~230s) is shorter than notebook creation can take.
            var bgLogger = lf.CreateLogger("BuildJob");
            _ = Task.Run(async () =>
            {
                try
                {
                    await store.UpdateStatusAsync(run.RunId, "CreatingLakehouse");
                    var lake = await fabric.CreateLakehouseAsync(tok, run.TargetLakehouseName!,
                        $"Created by copilot.roesli.org for run {run.RunId}");

                    await store.UpdateStatusAsync(run.RunId, "DeployingNotebook");
                    var nbPath = Path.Combine(env.ContentRootPath, "notebooks", "medallion_builder.ipynb");
                    if (!File.Exists(nbPath))
                    {
                        var alt = Path.Combine(AppContext.BaseDirectory, "notebooks", "medallion_builder.ipynb");
                        if (File.Exists(alt)) nbPath = alt;
                    }
                    var nbContent = await File.ReadAllTextAsync(nbPath);
                    var notebookId = await fabric.CreateNotebookAsync(tok, $"medallion_builder_{run.RunId}", nbContent);

                    await store.UpdateStatusAsync(run.RunId, "RunningNotebook");
                    var parameters = new Dictionary<string, object>
                    {
                        ["source_lakehouse_id"] = new { value = run.SourceLakehouseId ?? "", type = "string" },
                        ["source_tables_csv"] = new { value = run.TablesCsv ?? "", type = "string" },
                        ["target_lakehouse_id"] = new { value = lake.Id, type = "string" },
                        ["target_lakehouse_name"] = new { value = run.TargetLakehouseName!, type = "string" },
                        ["workspace_id"] = new { value = fabric.WorkspaceId, type = "string" },
                        ["run_id"] = new { value = run.RunId, type = "string" },
                        ["spec_url"] = new { value = run.SpecUrl ?? "", type = "string" }
                    };
                    var instanceId = await fabric.RunNotebookAsync(tok, notebookId, parameters);

                    await store.UpdateBuildAsync(run.RunId, lake.Id, notebookId, instanceId);
                    await store.UpdateStatusAsync(run.RunId, "Building", $"Notebook job {instanceId} started");
                }
                catch (Exception ex)
                {
                    bgLogger.LogError(ex, "Background build failed for run {runId}", run.RunId);
                    await store.UpdateStatusAsync(run.RunId, "Failed", ex.Message);
                }
            });

            return Results.Accepted($"/api/runs/{run.RunId}", await store.GetAsync(run.RunId));
        });

        g.MapGet("/runs/{runId}", async (string runId, HttpRequest req, IRunStore store, FabricService fabric) =>
        {
            var run = await store.GetAsync(runId);
            if (run is null) return Results.NotFound();
            if (run.Status == "Building" && run.NotebookId is not null && run.JobInstanceId is not null)
            {
                var tok = req.Headers["X-Fabric-Token"].ToString();
                if (!string.IsNullOrEmpty(tok))
                {
                    var (st, fail) = await fabric.GetJobStatusAsync(tok, run.NotebookId, run.JobInstanceId);
                    if (st == "Completed") await store.UpdateStatusAsync(runId, "Succeeded");
                    else if (st == "Failed" || st == "Cancelled") await store.UpdateStatusAsync(runId, st, fail);
                    run = await store.GetAsync(runId);
                }
            }
            return Results.Ok(run);
        });

        return app;
    }
}
