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
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseSwagger();
app.UseSwaggerUI();

app.MapApiEndpoints();

// SPA fallback for the Fabric workload bundle hosted under /workload/*
app.MapFallback("/workload/{*path}", ctx =>
{
    ctx.Response.ContentType = "text/html";
    return ctx.Response.SendFileAsync(Path.Combine(app.Environment.WebRootPath, "workload", "index.html"));
});

// Default SPA fallback for the standalone app at /
app.MapFallbackToFile("index.html");

app.Run();
