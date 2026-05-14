using System.Text.Json;
using CopilotMedallion.Api.Models;
using CopilotMedallion.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace CopilotMedallion.Api.Endpoints;

public static class ApiEndpoints
{
    private const string WorkspaceHeader = "X-Fabric-Workspace-Id";

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

        static string? Ws(HttpRequest req) {
            var v = req.Headers[WorkspaceHeader].ToString();
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }
        static string? SrcWs(HttpRequest req) {
            var v = req.Headers["X-Fabric-Source-Workspace-Id"].ToString();
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }
        static string? ItemId(HttpRequest req) {
            var v = req.Headers["X-Fabric-Item-Id"].ToString();
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }

        g.MapGet("/sources/lakehouses", async (HttpRequest req, FabricService fabric) =>
        {
            var tok = req.Headers["X-Fabric-Token"].ToString();
            if (string.IsNullOrEmpty(tok)) return Results.Unauthorized();
            return Results.Ok(await fabric.ListLakehousesAsync(tok, Ws(req)));
        });

        g.MapGet("/sources/lakehouses/{id}/tables", async (string id, HttpRequest req, FabricService fabric) =>
        {
            var tok = req.Headers["X-Fabric-Token"].ToString();
            if (string.IsNullOrEmpty(tok)) return Results.Unauthorized();
            var onelakeTok = req.Headers["X-Onelake-Token"].ToString();
            try
            {
                return Results.Ok(await fabric.ListTablesAsync(tok, id,
                    string.IsNullOrEmpty(onelakeTok) ? null : onelakeTok, Ws(req)));
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
            var ws = fabric.ResolveWorkspaceId(Ws(req));
            var srcWs = SrcWs(req) ?? ws;
            // The source can live in a different workspace than the item; use the override when set.
            var lakes = await fabric.ListLakehousesAsync(tok, srcWs);
            var src = lakes.FirstOrDefault(l => l.Id == body.SourceLakehouseId);
            // Fabric picker may return a lakehouse the listing API doesn't enumerate (e.g. cross-workspace
            // schema-enabled). Fall back to a direct GET.
            if (src is null)
            {
                src = await fabric.FindLakehouseByIdAsync(tok, body.SourceLakehouseId, srcWs);
            }
            if (src is null) return Results.NotFound("source lakehouse not accessible");
            var runId = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("n").Substring(0,6)}";
            var targetName = string.IsNullOrWhiteSpace(body.TargetLakehouseName)
                ? $"CopilotMedallion_{runId.Substring(0,15).Replace("-","_")}"
                : body.TargetLakehouseName!;
            var md = gen.Generate(runId, src, body.Tables, targetName, ws);
            return Results.Ok(new PreviewSpecsResponse(md, runId, targetName));
        });

        g.MapPost("/specs", async ([FromBody] GenerateSpecsRequest body, HttpRequest req,
                                    FabricService fabric, SpecGenerator gen, GitHubService gh, IRunStore store) =>
        {
            var tok = req.Headers["X-Fabric-Token"].ToString();
            if (string.IsNullOrEmpty(tok)) return Results.Unauthorized();

            var ws = fabric.ResolveWorkspaceId(Ws(req));
            var srcWs = SrcWs(req) ?? ws;
            var lakes = await fabric.ListLakehousesAsync(tok, srcWs);
            var src = lakes.FirstOrDefault(l => l.Id == body.SourceLakehouseId)
                      ?? await fabric.FindLakehouseByIdAsync(tok, body.SourceLakehouseId, srcWs);
            if (src is null) return Results.NotFound("source lakehouse not accessible");

            // If reusing an existing run, keep its id + target name + branch.
            RunInfo? existing = string.IsNullOrEmpty(body.ExistingRunId) ? null : await store.GetAsync(body.ExistingRunId);
            var runId = existing?.RunId ?? $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("n").Substring(0,6)}";
            var targetName = existing?.TargetLakehouseName
                ?? (string.IsNullOrWhiteSpace(body.TargetLakehouseName)
                    ? $"CopilotMedallion_{runId.Substring(0,15).Replace("-","_")}"
                    : body.TargetLakehouseName!);

            var spec = string.IsNullOrWhiteSpace(body.SpecMarkdown)
                ? gen.Generate(runId, src, body.Tables, targetName, ws)
                : body.SpecMarkdown!;

            if (!gh.Configured)
            {
                if (existing is null)
                    await store.CreateAsync(runId, "(local)", "(GITHUB_PAT not configured)",
                                             ws, body.SourceLakehouseId, string.Join(",", body.Tables), targetName, ItemId(req));
                return Results.Ok(new GenerateSpecsResponse(runId, "(local)", "(GITHUB_PAT not configured)", "(local)"));
            }

            var (branch, blobUrl, rawUrl) = await gh.PushSpecAsync(runId, spec);

            if (existing is null)
            {
                await store.CreateAsync(runId, branch, blobUrl,
                                         ws, body.SourceLakehouseId, string.Join(",", body.Tables), targetName, ItemId(req));
            }
            else
            {
                await store.UpdateStatusAsync(runId, "SpecsReady", null);
            }
            return Results.Ok(new GenerateSpecsResponse(runId, branch, blobUrl, rawUrl));
        });

        // Look up the latest run associated with a Fabric item, plus the spec markdown content.
        g.MapGet("/runs/by-item/{itemId}", async (string itemId, IRunStore store, NotebookBuilder builder) =>
        {
            var run = await store.GetLatestByItemAsync(itemId);
            if (run is null) return Results.NotFound();
            // Fetch the spec markdown (best effort).
            string? specMarkdown = null;
            if (!string.IsNullOrWhiteSpace(run.SpecUrl) && run.SpecUrl.StartsWith("http"))
            {
                var raw = run.SpecUrl.Replace("https://github.com/", "https://raw.githubusercontent.com/")
                                     .Replace("/blob/", "/");
                specMarkdown = await builder.FetchSpecMarkdownAsync(raw);
            }
            return Results.Ok(new { run, specMarkdown });
        });

        g.MapPost("/build", async ([FromBody] BuildRequest body, HttpRequest req,
                                     FabricService fabric, IRunStore store, IWebHostEnvironment env,
                                     NotebookBuilder builder, ILoggerFactory lf) =>
        {
            var tok = req.Headers["X-Fabric-Token"].ToString();
            if (string.IsNullOrEmpty(tok)) return Results.Unauthorized();

            var run = await store.GetAsync(body.RunId);
            if (run is null) return Results.NotFound();

            var ws = !string.IsNullOrWhiteSpace(run.WorkspaceId) ? run.WorkspaceId : fabric.ResolveWorkspaceId(Ws(req));

            await store.UpdateStatusAsync(run.RunId, "Queued");

            var bgLogger = lf.CreateLogger("BuildJob");
            _ = Task.Run(async () =>
            {
                try
                {
                    // Resolve target lakehouse — reuse existing if still there, else create.
                    string targetLakehouseId;
                    if (!string.IsNullOrEmpty(run.TargetLakehouseId))
                    {
                        var existing = await fabric.FindLakehouseByIdAsync(tok, run.TargetLakehouseId!, ws);
                        if (existing is not null)
                        {
                            targetLakehouseId = existing.Id;
                            bgLogger.LogInformation("Run {r}: reusing existing lakehouse {id}", run.RunId, existing.Id);
                            await store.UpdateStatusAsync(run.RunId, "ReusingLakehouse");
                        }
                        else
                        {
                            await store.UpdateStatusAsync(run.RunId, "CreatingLakehouse");
                            var lake = await fabric.CreateLakehouseAsync(tok, run.TargetLakehouseName!,
                                $"Created by copilot.roesli.org for run {run.RunId}", ws);
                            targetLakehouseId = lake.Id;
                        }
                    }
                    else
                    {
                        await store.UpdateStatusAsync(run.RunId, "CreatingLakehouse");
                        var lake = await fabric.CreateLakehouseAsync(tok, run.TargetLakehouseName!,
                            $"Created by copilot.roesli.org for run {run.RunId}", ws);
                        targetLakehouseId = lake.Id;
                    }

                    await store.UpdateStatusAsync(run.RunId, "GeneratingNotebook");
                    var rawUrl = ToRawUrl(run.SpecUrl);
                    var spec = await builder.FetchSpecMarkdownAsync(rawUrl);
                    var tables = (run.TablesCsv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
                    string nbContent;
                    var cells = spec is null ? null : await builder.GenerateCellsFromSpecAsync(
                        spec, ws, run.SourceLakehouseId ?? "", targetLakehouseId,
                        run.TargetLakehouseName!, tables, run.RunId);
                    if (cells is not null && cells.Count > 0)
                    {
                        nbContent = builder.BuildNotebookJson(cells, targetLakehouseId, run.TargetLakehouseName, ws);
                        bgLogger.LogInformation("Run {r}: using LLM-generated notebook with {n} cells", run.RunId, cells.Count);
                    }
                    else
                    {
                        var nbPath = Path.Combine(env.ContentRootPath, "notebooks", "medallion_builder.ipynb");
                        if (!File.Exists(nbPath))
                        {
                            var alt = Path.Combine(AppContext.BaseDirectory, "notebooks", "medallion_builder.ipynb");
                            if (File.Exists(alt)) nbPath = alt;
                        }
                        nbContent = await File.ReadAllTextAsync(nbPath);
                    }

                    // Resolve notebook — update existing in place, else create.
                    string notebookId;
                    var nbName = $"medallion_builder_{run.RunId}";
                    if (!string.IsNullOrEmpty(run.NotebookId))
                    {
                        await store.UpdateStatusAsync(run.RunId, "UpdatingNotebook");
                        try
                        {
                            await fabric.UpdateNotebookDefinitionAsync(tok, run.NotebookId!, nbContent, ws);
                            notebookId = run.NotebookId!;
                            bgLogger.LogInformation("Run {r}: updated notebook {id} in place", run.RunId, notebookId);
                        }
                        catch (Exception updateEx)
                        {
                            // In-place update failed — rename the old one with _old suffix and create new.
                            bgLogger.LogWarning(updateEx, "Update failed, falling back to recreate");
                            try { await fabric.RenameItemAsync(tok, run.NotebookId!, $"{nbName}_old_{DateTime.UtcNow:HHmmss}", ws); } catch { }
                            await store.UpdateStatusAsync(run.RunId, "DeployingNotebook");
                            notebookId = await fabric.CreateNotebookAsync(tok, nbName, nbContent, ws);
                        }
                    }
                    else
                    {
                        await store.UpdateStatusAsync(run.RunId, "DeployingNotebook");
                        // If a notebook with this name already exists in the workspace (e.g. from a previous deploy
                        // before we tracked the id), reuse its id.
                        var existing = await fabric.FindNotebookIdAsync(tok, nbName, ws);
                        if (existing is not null)
                        {
                            await fabric.UpdateNotebookDefinitionAsync(tok, existing, nbContent, ws);
                            notebookId = existing;
                        }
                        else
                        {
                            notebookId = await fabric.CreateNotebookAsync(tok, nbName, nbContent, ws);
                        }
                    }

                    await store.UpdateStatusAsync(run.RunId, "RunningNotebook");
                    var parameters = new Dictionary<string, object>
                    {
                        ["source_lakehouse_id"] = new { value = run.SourceLakehouseId ?? "", type = "string" },
                        ["source_tables_csv"] = new { value = run.TablesCsv ?? "", type = "string" },
                        ["target_lakehouse_id"] = new { value = targetLakehouseId, type = "string" },
                        ["target_lakehouse_name"] = new { value = run.TargetLakehouseName!, type = "string" },
                        ["workspace_id"] = new { value = ws, type = "string" },
                        ["run_id"] = new { value = run.RunId, type = "string" },
                        ["spec_url"] = new { value = run.SpecUrl ?? "", type = "string" }
                    };
                    var instanceId = await fabric.RunNotebookAsync(tok, notebookId, parameters, ws);

                    await store.UpdateBuildAsync(run.RunId, targetLakehouseId, notebookId, instanceId);
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

        static string? ToRawUrl(string? blobUrl)
        {
            if (string.IsNullOrWhiteSpace(blobUrl) || !blobUrl.StartsWith("https://github.com/")) return blobUrl;
            return blobUrl.Replace("https://github.com/", "https://raw.githubusercontent.com/")
                          .Replace("/blob/", "/");
        }

        g.MapGet("/runs/{runId}", async (string runId, HttpRequest req, IRunStore store, FabricService fabric) =>
        {
            var run = await store.GetAsync(runId);
            if (run is null) return Results.NotFound();
            if (run.Status == "Building" && run.NotebookId is not null && run.JobInstanceId is not null)
            {
                var tok = req.Headers["X-Fabric-Token"].ToString();
                if (!string.IsNullOrEmpty(tok))
                {
                    var (st, fail) = await fabric.GetJobStatusAsync(tok, run.NotebookId, run.JobInstanceId, run.WorkspaceId);
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
