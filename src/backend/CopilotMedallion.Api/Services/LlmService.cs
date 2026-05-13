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
    private readonly string _deployment;
    private readonly string _apiVersion;
    private readonly DefaultAzureCredential _cred = new DefaultAzureCredential();
    private (string Token, DateTimeOffset ExpiresOn)? _cachedToken;
    private readonly ILogger<LlmService> _log;

    public LlmService(IHttpClientFactory http, IConfiguration cfg, ILogger<LlmService> log)
    {
        _http = http;
        _endpoint = (cfg["OpenAI:Endpoint"] ?? "").TrimEnd('/');
        _deployment = cfg["OpenAI:Deployment"] ?? "";
        _apiVersion = cfg["OpenAI:ApiVersion"] ?? "2024-10-21";
        _log = log;
    }

    public bool Configured => !string.IsNullOrEmpty(_endpoint) && !string.IsNullOrEmpty(_deployment);

    private async Task<string> GetTokenAsync()
    {
        if (_cachedToken is { } c && c.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(5))
            return c.Token;
        var tok = await _cred.GetTokenAsync(new TokenRequestContext(new[] { "https://cognitiveservices.azure.com/.default" }));
        _cachedToken = (tok.Token, tok.ExpiresOn);
        return tok.Token;
    }

    public async Task<string> ChatAsync(string systemPrompt, string userPrompt, int maxTokens = 4096, double temperature = 0.2)
    {
        if (!Configured) throw new InvalidOperationException("Azure OpenAI not configured.");
        var token = await GetTokenAsync();
        var c = _http.CreateClient();
        c.Timeout = TimeSpan.FromMinutes(3);
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var url = $"{_endpoint}/openai/deployments/{_deployment}/chat/completions?api-version={_apiVersion}";
        var body = new
        {
            messages = new object[] {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            },
            max_tokens = maxTokens,
            temperature
        };
        var resp = await c.PostAsync(url, new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));
        var raw = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new Exception($"LLM call failed {resp.StatusCode}: {raw}");
        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
    }
}
