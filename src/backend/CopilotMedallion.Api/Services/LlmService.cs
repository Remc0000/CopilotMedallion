using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;

namespace CopilotMedallion.Api.Services;

public class LlmService
{
    private readonly IHttpClientFactory _http;
    private readonly string _endpoint;
    private readonly string _defaultDeployment;
    private readonly string _apiVersion;
    private readonly string[] _availableModels;
    private readonly DefaultAzureCredential _cred = new DefaultAzureCredential();
    private (string Token, DateTimeOffset ExpiresOn)? _cachedToken;
    private readonly ILogger<LlmService> _log;
    private readonly RunUsageTracker _usage;

    public LlmService(IHttpClientFactory http, IConfiguration cfg, ILogger<LlmService> log, RunUsageTracker usage)
    {
        _http = http;
        _endpoint = (cfg["OpenAI:Endpoint"] ?? "").TrimEnd('/');
        _defaultDeployment = cfg["OpenAI:Deployment"] ?? "";
        _apiVersion = cfg["OpenAI:ApiVersion"] ?? "2025-04-01-preview";
        var models = cfg["OpenAI:Models"];
        _availableModels = string.IsNullOrWhiteSpace(models)
            ? new[] { _defaultDeployment }.Where(s => !string.IsNullOrEmpty(s)).ToArray()
            : models.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _log = log;
        _usage = usage;
    }

    public bool Configured => !string.IsNullOrEmpty(_endpoint) && !string.IsNullOrEmpty(_defaultDeployment);
    public string DefaultModel => _defaultDeployment;
    public IReadOnlyList<string> AvailableModels => _availableModels;

    // Models that require the newer chat-completions parameter style:
    // `max_completion_tokens` instead of `max_tokens`, and no temperature override
    // (only the default of 1 is accepted). Covers gpt-5 / o-series reasoning models
    // as well as gpt-chat-latest.
    private static bool IsReasoningModel(string deployment) =>
        deployment.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase)
        || deployment.StartsWith("gpt-chat", StringComparison.OrdinalIgnoreCase)
        || deployment.StartsWith("o1", StringComparison.OrdinalIgnoreCase)
        || deployment.StartsWith("o3", StringComparison.OrdinalIgnoreCase)
        || deployment.StartsWith("o4", StringComparison.OrdinalIgnoreCase);

    private async Task<string> GetTokenAsync()
    {
        if (_cachedToken is { } c && c.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(5))
            return c.Token;
        var tok = await _cred.GetTokenAsync(new TokenRequestContext(new[] { "https://cognitiveservices.azure.com/.default" }));
        _cachedToken = (tok.Token, tok.ExpiresOn);
        return tok.Token;
    }

    public async Task<string> ChatAsync(string systemPrompt, string userPrompt, int maxTokens = 4096, double temperature = 0.2, string? model = null, string? runId = null)
    {
        if (!Configured) throw new InvalidOperationException("Azure OpenAI not configured.");
        var deployment = string.IsNullOrWhiteSpace(model) ? _defaultDeployment : model;
        var token = await GetTokenAsync();
        var c = _http.CreateClient();
        c.Timeout = TimeSpan.FromMinutes(5);
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var url = $"{_endpoint}/openai/deployments/{deployment}/chat/completions?api-version={_apiVersion}";

        object body;
        if (IsReasoningModel(deployment))
        {
            // gpt-5 / o-series: max_completion_tokens, no temperature override (must be default 1)
            body = new
            {
                messages = new object[] {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt }
                },
                max_completion_tokens = Math.Max(maxTokens, 8000)
            };
        }
        else
        {
            body = new
            {
                messages = new object[] {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt }
                },
                max_tokens = maxTokens,
                temperature
            };
        }

        var resp = await c.PostAsync(url, new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));
        var raw = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new Exception($"LLM call failed {resp.StatusCode} (model={deployment}): {raw}");
        using var doc = JsonDocument.Parse(raw);
        // Record token usage against the run so the UI can display tokens + estimated cost.
        try
        {
            if (!string.IsNullOrEmpty(runId) && doc.RootElement.TryGetProperty("usage", out var usage))
            {
                int prompt = usage.TryGetProperty("prompt_tokens", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0;
                int completion = usage.TryGetProperty("completion_tokens", out var ct) && ct.ValueKind == JsonValueKind.Number ? ct.GetInt32() : 0;
                _usage.Record(runId, deployment, prompt, completion);
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "usage recording failed"); }
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
    }
}
