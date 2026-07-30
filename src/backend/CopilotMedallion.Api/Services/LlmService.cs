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
    private readonly string _reasoningEffort;
    private readonly TimeSpan _timeout;
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
        // Reasoning models default to a very high internal reasoning budget. On the gpt-5.6 family
        // that makes a single large generation call (notably the gold layer) run for >10 minutes,
        // which blows the HTTP timeout below and surfaces as "LLM returned no cells". Pin an
        // explicit, moderate effort unless an operator overrides it.
        _reasoningEffort = (cfg["OpenAI:ReasoningEffort"] ?? "medium").Trim().ToLowerInvariant();
        var timeoutMin = int.TryParse(cfg["OpenAI:TimeoutMinutes"], out var tm) && tm > 0 ? tm : 10;
        _timeout = TimeSpan.FromMinutes(timeoutMin);
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
        c.Timeout = _timeout;
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var url = $"{_endpoint}/openai/deployments/{deployment}/chat/completions?api-version={_apiVersion}";

        object body;
        if (IsReasoningModel(deployment))
        {
            // gpt-5 / o-series: max_completion_tokens, no temperature override (must be default 1).
            // reasoning_effort is REQUIRED here in practice: left unset, gpt-5.6-* burns an
            // unbounded reasoning budget and a large prompt can take >10 minutes to return.
            body = new
            {
                messages = new object[] {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt }
                },
                max_completion_tokens = Math.Max(maxTokens, 8000),
                reasoning_effort = _reasoningEffort
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

        HttpResponseMessage resp;
        try
        {
            resp = await c.PostAsync(url, new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));
        }
        catch (TaskCanceledException)
        {
            throw new TimeoutException(
                $"LLM call timed out after {_timeout.TotalMinutes:0} min (model={deployment}, reasoning_effort={_reasoningEffort}). " +
                "Lower OpenAI__ReasoningEffort, raise OpenAI__TimeoutMinutes, or pick a faster model.");
        }
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

        var choice = doc.RootElement.GetProperty("choices")[0];
        var content = choice.GetProperty("message").GetProperty("content").GetString() ?? "";
        var finish = choice.TryGetProperty("finish_reason", out var fr) ? fr.GetString() : null;
        if (string.IsNullOrWhiteSpace(content))
        {
            // Most commonly the whole max_completion_tokens budget was consumed by reasoning
            // tokens, leaving nothing for the visible answer.
            throw new Exception(
                $"LLM returned an empty response (model={deployment}, finish_reason={finish ?? "?"}, " +
                $"reasoning_effort={_reasoningEffort}, max_completion_tokens={Math.Max(maxTokens, 8000)}). " +
                "Lower OpenAI__ReasoningEffort or raise the token budget.");
        }
        return content;
    }
}
