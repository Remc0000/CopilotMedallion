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
builder.Services.AddSingleton<LlmService>();
builder.Services.AddSingleton<NotebookBuilder>();
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins("http://localhost:5173").AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

var app = builder.Build();

app.UseCors();

// Serve workload root paths explicitly (before static-file middleware) so the
// workload SPA's index.html is returned for /workload and /workload/ without
// colliding with the standalone app's SPA fallback below.
app.Use(async (ctx, next) =>
{
    var p = ctx.Request.Path.Value;
    if (p == "/workload" || p == "/workload/")
    {
        ctx.Response.ContentType = "text/html";
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
