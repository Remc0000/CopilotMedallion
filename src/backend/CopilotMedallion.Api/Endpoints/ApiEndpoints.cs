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

        static string? Header(HttpRequest req, string name)
        {
            var v = req.Headers[name].ToString();
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }
        static string? FabricToken(HttpRequest req) => Header(req, "X-Fabric-Token");
        static string? OneLakeToken(HttpRequest req) => Header(req, "X-Onelake-Token");
        static string? Ws(HttpRequest req) => Header(req, WorkspaceHeader);
        static string? SrcWs(HttpRequest req) => Header(req, "X-Fabric-Source-Workspace-Id");
        static string? ItemId(HttpRequest req) => Header(req, "X-Fabric-Item-Id");

        g.MapGet("/sources/lakehouses", async (HttpRequest req, FabricService fabric) =>
        {
            var tok = FabricToken(req);
            if (tok is null) return Results.Unauthorized();
            return Results.Ok(await fabric.ListLakehousesAsync(tok, Ws(req)));
        });

        g.MapGet("/sources/lakehouses/{id}", async (string id, HttpRequest req, FabricService fabric) =>
        {
            var tok = FabricToken(req);
            if (tok is null) return Results.Unauthorized();
            var wsOverride = req.Query["workspaceId"].ToString();
            var ws = !string.IsNullOrWhiteSpace(wsOverride) ? wsOverride : (SrcWs(req) ?? Ws(req));
            var lh = await fabric.FindLakehouseByIdAsync(tok, id, ws);
            if (lh is null) return Results.NotFound();
            return Results.Ok(new { id = lh.Id, displayName = lh.DisplayName });
        });

        g.MapGet("/models", (LlmService llm) =>
            Results.Ok(new { models = llm.AvailableModels, defaultModel = llm.DefaultModel }));

        g.MapGet("/workspaces/{id}", async (string id, HttpRequest req, FabricService fabric, ILoggerFactory lf) =>
        {
            var tok = FabricToken(req);
            if (tok is null) return Results.Unauthorized();
            try
            {
                var ws = await fabric.GetWorkspaceAsync(tok, id);
                if (ws is null) return Results.Ok(new { id, displayName = (string?)null });
                return Results.Ok(new { id = ws.Value.Id, displayName = ws.Value.DisplayName });
            }
            catch (Exception ex)
            {
                lf.CreateLogger("WorkspaceLookup").LogWarning(ex, "GetWorkspace({id}) failed", id);
                // Never 500 — the caller just needs a name for display.
                return Results.Ok(new { id, displayName = (string?)null });
            }
        });

        g.MapGet("/runs/{runId}/error", async (string runId, HttpRequest req, IRunStore store, FabricService fabric) =>
        {
            var run = await store.GetAsync(runId);
            if (run is null || string.IsNullOrEmpty(run.TargetLakehouseId)) return Results.NotFound();
            var ws = !string.IsNullOrWhiteSpace(run.WorkspaceId) ? run.WorkspaceId! : fabric.ResolveWorkspaceId(Ws(req));
            var path = $"Files/_copilot_medallion/runs/{runId}/error.txt";
            // Try the user's OneLake token first (works for users with workspace access).
            var userOnelakeTok = OneLakeToken(req);
            string? text = null;
            if (!string.IsNullOrEmpty(userOnelakeTok))
                text = await fabric.TryDownloadTextFromLakehouseAsync(userOnelakeTok, ws, run.TargetLakehouseId!, path);
            // Fall back to the App Service's MI storage token (covers the case where the user is in Fabric iframe
            // and doesn't have a OneLake token in the request).
            if (string.IsNullOrEmpty(text))
                text = await fabric.TryDownloadTextWithServiceTokenAsync(ws, run.TargetLakehouseId!, path);
            return Results.Ok(new { error = text });
        });

        g.MapPost("/runs/{runId}/fix-spec", async (string runId, [FromBody] FixSpecRequest body,
                                                    IRunStore store, NotebookBuilder builder) =>
        {
            var run = await store.GetAsync(runId);
            if (run is null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(body.CurrentSpec) || string.IsNullOrWhiteSpace(body.ErrorTrace))
                return Results.BadRequest(new { error = "currentSpec and errorTrace are required" });
            var updated = await builder.FixSpecAsync(body.CurrentSpec, body.ErrorTrace, body.Model,
                iteration: body.Iteration, failedLayer: body.FailedLayer, runId: runId);
            if (string.IsNullOrEmpty(updated))
                return Results.Problem(title: "Could not generate a fixed spec", statusCode: 500);
            return Results.Ok(new { markdown = updated });
        });

        g.MapGet("/sources/lakehouses/{id}/tables", async (string id, HttpRequest req, FabricService fabric) =>
        {
            var tok = FabricToken(req);
            if (tok is null) return Results.Unauthorized();
            var onelakeTok = OneLakeToken(req);
            try
            {
                return Results.Ok(await fabric.ListTablesAsync(tok, id,
                    onelakeTok, SrcWs(req) ?? Ws(req)));
            }
            catch (Exception ex)
            {
                return Results.Problem(title: "ListTables failed", detail: ex.ToString(), statusCode: 500);
            }
        });

        g.MapPost("/specs/preview", async ([FromBody] PreviewSpecsRequest body, HttpRequest req,
                                            FabricService fabric, SpecGenerator gen, NotebookBuilder builder) =>
        {
            var tok = FabricToken(req);
            if (tok is null) return Results.Unauthorized();
            var ws = fabric.ResolveWorkspaceId(Ws(req));
            var srcWs = SrcWs(req) ?? ws;
            var lakes = await fabric.ListLakehousesAsync(tok, srcWs);
            var src = lakes.FirstOrDefault(l => l.Id == body.SourceLakehouseId);
            if (src is null) src = await fabric.FindLakehouseByIdAsync(tok, body.SourceLakehouseId, srcWs);
            if (src is null) return Results.NotFound("source lakehouse not accessible");
            var runId = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("n").Substring(0,6)}";
            var targetName = string.IsNullOrWhiteSpace(body.TargetLakehouseName)
                ? $"CopilotMedallion_{runId.Substring(0,15).Replace("-","_")}"
                : body.TargetLakehouseName!;

            // Introspect actual Delta schemas for the picked tables (up to 25, in parallel).
            // The LLM proposes based on the REAL columns/types — never assumes a known model.
            // Use the user's storage token (from X-Onelake-Token) so cross-workspace sources work.
            var userOnelakeTok = OneLakeToken(req);
            var userTokForSchemas = userOnelakeTok;
            var schemas = new Dictionary<string, string>();
            try
            {
                var capped = body.Tables.Take(25).ToList();
                var tasks = capped.Select(async t =>
                {
                    var s = await fabric.GetTableSchemaSummaryAsync(srcWs, body.SourceLakehouseId, t, userTokForSchemas);
                    return (t, s);
                }).ToArray();
                var results = await Task.WhenAll(tasks);
                foreach (var (t, s) in results)
                {
                    if (!string.IsNullOrEmpty(s)) schemas[t] = s;
                }
            }
            catch { /* best-effort; LLM will still get table names */ }

            // First attempt: LLM-proposed spec tailored to the picked tables AND their actual schemas.
            string? md = null;
            try { md = await builder.ProposeSpecAsync(runId, ws, src.DisplayName, src.Id, body.Tables, targetName, schemas, body.Model); }
            catch { /* fall back to template */ }
            // Fallback: static template if LLM unavailable or threw.
            if (string.IsNullOrWhiteSpace(md))
            {
                md = gen.Generate(runId, src, body.Tables, targetName, ws);
            }
            return Results.Ok(new PreviewSpecsResponse(md, runId, targetName));
        });

        g.MapPost("/specs", async ([FromBody] GenerateSpecsRequest body, HttpRequest req,
                                    FabricService fabric, SpecGenerator gen, GitHubService gh, IRunStore store) =>
        {
            var tok = FabricToken(req);
            if (tok is null) return Results.Unauthorized();

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

            // Always persist the spec markdown in our runs DB — this is now the canonical store.
            // GitHub push is optional/best-effort for human-readable history.
            string branch = "(local)";
            string blobUrl = "(stored in lakehouse Files/spec.md)";
            string rawUrl = "(local)";
            if (gh.Configured)
            {
                try
                {
                    var (br, blob, raw) = await gh.PushSpecAsync(runId, spec);
                    branch = br; blobUrl = blob; rawUrl = raw;
                }
                catch (Exception ex)
                {
                    // GitHub is now optional; surface the failure in the URL but continue.
                    blobUrl = $"(GitHub push failed: {ex.Message})";
                }
            }

            if (existing is null)
            {
                await store.CreateAsync(runId, branch, blobUrl,
                                         ws, body.SourceLakehouseId, string.Join(",", body.Tables), targetName, ItemId(req),
                                         body.TargetLakehouseId, body.TargetWorkspaceId, srcWs);
            }
            else
            {
                await store.UpdateStatusAsync(runId, "SpecsReady", null);
            }
            // Persist the spec markdown text itself in the DB so we can rebuild without GitHub.
            await store.UpdateSpecMarkdownAsync(runId, spec);
            // Snapshot the Generic guidance section so it accumulates across runs/lakehouses.
            try
            {
                var gMatch = System.Text.RegularExpressions.Regex.Match(spec,
                    @"^##\s+Generic guidance\s*$(?<body>[\s\S]*?)(?=^##\s|\z)",
                    System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (gMatch.Success)
                {
                    var section = ("## Generic guidance" + gMatch.Groups["body"].Value).Trim();
                    if (section.Length > 0)
                        await store.SaveGuidanceSnapshotAsync(runId, section);
                }
            }
            catch { /* best-effort */ }
            return Results.Ok(new GenerateSpecsResponse(runId, branch, blobUrl, rawUrl));
        });

        g.MapGet("/guidance", async (HttpRequest req, IRunStore store) =>
        {
            int limit = 50;
            if (int.TryParse(req.Query["limit"].ToString(), out var n) && n > 0 && n <= 500) limit = n;
            var items = await store.ListGuidanceSnapshotsAsync(limit);
            return Results.Ok(items.Select(i => new {
                id = i.Id,
                capturedAt = i.CapturedAt,
                runId = i.RunId,
                content = i.Content
            }));
        });

        // Look up the latest run associated with a Fabric item, plus the spec markdown content.
        g.MapGet("/runs/by-item/{itemId}", async (string itemId, IRunStore store) =>
        {
            var run = await store.GetLatestByItemAsync(itemId);
            if (run is null) return Results.NotFound();
            // Spec markdown is now stored directly in the runs DB.
            return Results.Ok(new { run, specMarkdown = run.SpecMarkdown });
        });

        g.MapPost("/build", async ([FromBody] BuildRequest body, HttpRequest req,
                                     FabricService fabric, IRunStore store,
                                     NotebookBuilder builder, ILoggerFactory lf) =>
        {
            var tok = FabricToken(req);
            if (tok is null) return Results.Unauthorized();

            var run = await store.GetAsync(body.RunId);
            if (run is null) return Results.NotFound();

            var ws = !string.IsNullOrWhiteSpace(run.WorkspaceId) ? run.WorkspaceId : fabric.ResolveWorkspaceId(Ws(req));

            await store.UpdateStatusAsync(run.RunId, "Queued");

            var bgLogger = lf.CreateLogger("BuildJob");
            _ = Task.Run(async () =>
            {
                try
                {
                    // Best-effort: ensure the App Service MI has access to this workspace so OneLake
                    // reads/writes (error.txt, spec.txt, table cleanup) work without per-user tokens.
                    await fabric.EnsureMiHasWorkspaceAccessAsync(tok, ws);

                    // Resolve target lakehouse — reuse existing by id, then by name, else create.
                    string targetLakehouseId = "";
                    bool reusedExisting = false;
                    if (!string.IsNullOrEmpty(run.TargetLakehouseId))
                    {
                        var existing = await fabric.FindLakehouseByIdAsync(tok, run.TargetLakehouseId!, ws);
                        if (existing is not null)
                        {
                            targetLakehouseId = existing.Id;
                            reusedExisting = true;
                            bgLogger.LogInformation("Run {r}: reusing existing lakehouse {id} (by id)", run.RunId, existing.Id);
                            await store.UpdateStatusAsync(run.RunId, "ReusingLakehouse");
                        }
                    }
                    if (string.IsNullOrEmpty(targetLakehouseId))
                    {
                        // Try find-by-name first so we don't create a "_2" suffix when a lakehouse
                        // with this name already exists from a previous run.
                        var byName = await fabric.FindLakehouseByNameAsync(tok, run.TargetLakehouseName!, ws);
                        if (byName is not null)
                        {
                            targetLakehouseId = byName.Id;
                            reusedExisting = true;
                            bgLogger.LogInformation("Run {r}: reusing existing lakehouse '{n}' ({id}) by name", run.RunId, byName.DisplayName, byName.Id);
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

                    // Persist target_lakehouse_id IMMEDIATELY so any subsequent failure still has
                    // a known lakehouse to read error.txt from (the auto-fix loop and UI depend on this).
                    await store.UpdateBuildAsync(run.RunId, targetLakehouseId, null, null);

                    // When reusing, clear Tables/ (NOT Files/) so stale tables from a previous spec
                    // don't pollute the new build. Files/ is preserved (spec.txt, error.txt, etc.).
                    if (reusedExisting)
                    {
                        try
                        {
                            await fabric.ClearLakehouseTablesAsync(ws, targetLakehouseId);
                            bgLogger.LogInformation("Run {r}: cleared Tables/ in reused lakehouse {id}", run.RunId, targetLakehouseId);
                        }
                        catch (Exception ex)
                        {
                            bgLogger.LogWarning(ex, "Run {r}: could not clear Tables/; the build will overwrite per-table", run.RunId);
                        }
                    }

                    await store.UpdateStatusAsync(run.RunId, "GeneratingNotebook");
                    // Spec is now stored directly in the runs DB (canonical) — fall back to GitHub for
                    // legacy runs that don't have it.
                    var spec = run.SpecMarkdown;
                    if (string.IsNullOrEmpty(spec))
                    {
                        var rawUrl = ToRawUrl(run.SpecUrl);
                        spec = await builder.FetchSpecMarkdownAsync(rawUrl);
                    }
                    var tables = (run.TablesCsv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();

                    // Persist the spec to OneLake Files/spec.md so the lakehouse is self-documenting.
                    if (!string.IsNullOrEmpty(spec))
                    {
                        try
                        {
                            await fabric.UploadFileToLakehouseAsync(ws, targetLakehouseId,
                                "Files/spec.md", spec, "text/markdown; charset=utf-8");
                            bgLogger.LogInformation("Run {r}: wrote spec.md to lakehouse Files/", run.RunId);
                        }
                        catch (Exception ex)
                        {
                            bgLogger.LogWarning(ex, "Run {r}: could not write spec.md to lakehouse", run.RunId);
                        }
                    }

                    // Ask the LLM for per-layer cell lists.
                    var perLayer = spec is null ? null : await builder.GenerateNotebooksFromSpecAsync(
                        spec, ws, run.SourceLakehouseId ?? "", targetLakehouseId,
                        run.TargetLakehouseName!, tables, run.RunId, body.Model);
                    if (perLayer is null || perLayer.Count == 0)
                    {
                        throw new Exception("LLM did not return any notebook cells. Check the spec and try again.");
                    }
                    bgLogger.LogInformation("Run {r}: LLM produced {n} notebooks ({layers})",
                        run.RunId, perLayer.Count, string.Join(",", perLayer.Keys));

                    var srcWsForRun = !string.IsNullOrWhiteSpace(run.SourceWorkspaceId) ? run.SourceWorkspaceId! : ws;
                    var parameters = new Dictionary<string, object>
                    {
                        ["source_workspace_id"] = new { value = srcWsForRun, type = "string" },
                        ["source_lakehouse_id"] = new { value = run.SourceLakehouseId ?? "", type = "string" },
                        ["source_tables_csv"] = new { value = run.TablesCsv ?? "", type = "string" },
                        ["target_lakehouse_id"] = new { value = targetLakehouseId, type = "string" },
                        ["target_lakehouse_name"] = new { value = run.TargetLakehouseName!, type = "string" },
                        ["workspace_id"] = new { value = ws, type = "string" },
                        ["run_id"] = new { value = run.RunId, type = "string" },
                        ["spec_url"] = new { value = run.SpecUrl ?? "", type = "string" }
                    };

                    // Run each layer's notebook sequentially: deploy → run → poll until complete.
                    foreach (var layer in new[] { "bronze", "silver", "gold", "reporting" })
                    {
                        if (!perLayer.TryGetValue(layer, out var layerCells) || layerCells.Count == 0)
                        {
                            bgLogger.LogWarning("Run {r}: no cells for layer {l} — skipping", run.RunId, layer);
                            continue;
                        }

                        // Notebook name: <lakehouse>_<layer> e.g. "MyLake_bronze". Sanitised to safe chars.
                        var safeLh = System.Text.RegularExpressions.Regex.Replace(run.TargetLakehouseName ?? "medallion", "[^A-Za-z0-9_]+", "_").Trim('_');
                        if (string.IsNullOrEmpty(safeLh)) safeLh = "medallion";
                        var nbName = $"{safeLh}_{layer}";
                        var nbContent = builder.BuildNotebookJson(layerCells, targetLakehouseId, run.TargetLakehouseName, ws);

                        await store.UpdateStatusAsync(run.RunId, $"Deploying{Capitalize(layer)}");
                        var priorNb = layer switch {
                            "bronze" => run.BronzeNotebookId,
                            "silver" => run.SilverNotebookId,
                            "gold"   => run.GoldNotebookId,
                            "reporting" => run.ReportingNotebookId,
                            _ => null
                        };
                        string notebookId;
                        if (!string.IsNullOrEmpty(priorNb))
                        {
                            try
                            {
                                await fabric.UpdateNotebookDefinitionAsync(tok, priorNb!, nbContent, ws);
                                notebookId = priorNb!;
                            }
                            catch (Exception updateEx)
                            {
                                bgLogger.LogWarning(updateEx, "Run {r}: in-place update of {l} notebook failed; recreating", run.RunId, layer);
                                try { await fabric.RenameItemAsync(tok, priorNb!, $"{nbName}_old_{DateTime.UtcNow:HHmmss}", ws); } catch { }
                                notebookId = await fabric.CreateNotebookAsync(tok, nbName, nbContent, ws);
                            }
                        }
                        else
                        {
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

                        await store.UpdateStatusAsync(run.RunId, $"Running{Capitalize(layer)}");
                        await store.UpdateLayerAsync(run.RunId, layer, notebookId, null);

                        var instanceId = await fabric.RunNotebookAsync(tok, notebookId, parameters, ws,
                            defaultLakehouseId: targetLakehouseId, defaultLakehouseName: run.TargetLakehouseName);
                        await store.UpdateLayerAsync(run.RunId, layer, notebookId, instanceId);

                        // Poll until terminal.
                        var pollStart = DateTime.UtcNow;
                        while (true)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(8));
                            var (st, fail) = await fabric.GetJobStatusAsync(tok, notebookId, instanceId, ws);
                            if (st == "Completed") break;
                            if (st == "Failed" || st == "Cancelled")
                            {
                                await store.UpdateStatusAsync(run.RunId, "Failed", $"{layer}: {fail ?? "(no details)"}");
                                return;
                            }
                            if (DateTime.UtcNow - pollStart > TimeSpan.FromMinutes(45))
                            {
                                await store.UpdateStatusAsync(run.RunId, "Failed", $"{layer}: timed out after 45 min");
                                return;
                            }
                        }
                        bgLogger.LogInformation("Run {r}: {layer} notebook completed", run.RunId, layer);
                    }

                    // Save final pointer for /api/build response + finalize.
                    await store.UpdateBuildAsync(run.RunId, targetLakehouseId, run.GoldNotebookId ?? run.SilverNotebookId ?? run.BronzeNotebookId, null);
                    await store.UpdateStatusAsync(run.RunId, "Succeeded", "All three medallion notebooks ran successfully.");
                }
                catch (Exception ex)
                {
                    bgLogger.LogError(ex, "Background build failed for run {runId}", run.RunId);
                    await store.UpdateStatusAsync(run.RunId, "Failed", ex.Message);
                }
            });

            return Results.Accepted($"/api/runs/{run.RunId}", await store.GetAsync(run.RunId));
        });

        static string Capitalize(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s.Substring(1);

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
                var tok = FabricToken(req);
                if (tok is not null)
                {
                    var (st, fail) = await fabric.GetJobStatusAsync(tok, run.NotebookId, run.JobInstanceId, run.WorkspaceId);
                    if (st == "Completed") await store.UpdateStatusAsync(runId, "Succeeded");
                    else if (st == "Failed" || st == "Cancelled") await store.UpdateStatusAsync(runId, st, fail);
                    run = await store.GetAsync(runId);
                }
            }
            return Results.Ok(run);
        });

        g.MapGet("/runs/{runId}/usage", (string runId, RunUsageTracker usage) =>
        {
            var u = usage.Get(runId);
            if (u is null) return Results.Ok(new {
                promptTokens = 0, completionTokens = 0, totalTokens = 0,
                requests = 0, estimatedCostUsd = 0m,
                perModel = Array.Empty<object>()
            });
            return Results.Ok(new {
                promptTokens = u.PromptTokens,
                completionTokens = u.CompletionTokens,
                totalTokens = u.TotalTokens,
                requests = u.Requests,
                estimatedCostUsd = u.EstimatedCostUsd,
                perModel = u.PerModel.Values.Select(m => new {
                    model = m.Model, promptTokens = m.PromptTokens,
                    completionTokens = m.CompletionTokens, requests = m.Requests
                }).ToArray()
            });
        });

        return app;
    }
}
