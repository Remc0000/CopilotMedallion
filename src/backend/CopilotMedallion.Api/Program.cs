using CopilotMedallion.Api.Services;
using CopilotMedallion.Api.Endpoints;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<IRunStore, SqliteRunStore>();
builder.Services.AddSingleton<FabricService>();
builder.Services.AddSingleton<GitHubService>();
builder.Services.AddSingleton<SpecGenerator>();
builder.Services.AddSingleton<RunUsageTracker>();
builder.Services.AddSingleton<LlmService>();
builder.Services.AddSingleton<NotebookBuilder>();
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins("http://localhost:5173").AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

var app = builder.Build();

// At startup, recover orphaned in-flight runs (from a previous process that was killed
// mid-build by an App Service restart) so the UI doesn't show "Generating..." forever.
try
{
    var store = app.Services.GetRequiredService<IRunStore>();
    var n = await store.MarkAbandonedAsFailedAsync();
    if (n > 0) app.Logger.LogWarning("Recovered {n} orphan run(s) on startup (marked Failed so they can be resumed)", n);
}
catch (Exception ex) { app.Logger.LogWarning(ex, "orphan-run recovery skipped"); }

app.UseCors();

// Prevent browsers from caching HTML entry points so deployed bundle-hash
// changes are picked up on the next page load.
app.Use(async (ctx, next) =>
{
    var p = ctx.Request.Path.Value ?? string.Empty;
    var isHtmlEntry = p == "/" || p == "/workload" || p == "/workload/"
        || p.EndsWith("/index.html", StringComparison.OrdinalIgnoreCase);
    await next();
    if (isHtmlEntry && ctx.Response.StatusCode == 200)
    {
        ctx.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
        ctx.Response.Headers["Pragma"] = "no-cache";
        ctx.Response.Headers["Expires"] = "0";
    }
});

// Serve workload root paths explicitly (before static-file middleware) so the
// workload SPA's index.html is returned for /workload and /workload/ without
// colliding with the standalone app's SPA fallback below.
app.Use(async (ctx, next) =>
{
    var p = ctx.Request.Path.Value;
    if (p == "/workload" || p == "/workload/")
    {
        ctx.Response.ContentType = "text/html";
        ctx.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
        await ctx.Response.SendFileAsync(Path.Combine(app.Environment.WebRootPath, "workload", "index.html"));
        return;
    }
    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseSwagger();
app.UseSwaggerUI();

app.MapApiEndpoints();

// SPA fallback for the standalone app
app.MapFallbackToFile("index.html");

app.Run();
