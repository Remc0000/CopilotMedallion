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
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins("http://localhost:5173").AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

var app = builder.Build();

app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseSwagger();
app.UseSwaggerUI();

app.MapApiEndpoints();

// SPA fallback - everything not matched and not an API path returns index.html
app.MapFallbackToFile("index.html");

app.Run();
