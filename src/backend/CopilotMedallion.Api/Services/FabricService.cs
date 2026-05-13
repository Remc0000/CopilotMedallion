using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using CopilotMedallion.Api.Models;

namespace CopilotMedallion.Api.Services;

/// <summary>
/// Thin Fabric REST helper. Uses the caller-supplied user access token
/// (audience = api.fabric.microsoft.com). The frontend acquires this via MSAL
/// and passes it as Authorization: Bearer ... to our /api/*.
/// </summary>
public class FabricService
{
    private readonly IHttpClientFactory _http;
    private readonly string _baseUrl;
    private readonly string _workspaceId;
    private readonly ILogger<FabricService> _log;
    private readonly DefaultAzureCredential _miCred = new DefaultAzureCredential();
    private (string Token, DateTimeOffset ExpiresOn)? _cachedStorageToken;

    public FabricService(IHttpClientFactory http, IConfiguration cfg, ILogger<FabricService> log)
    {
        _http = http;
        _baseUrl = cfg["Fabric:BaseUrl"] ?? "https://api.fabric.microsoft.com/v1";
        _workspaceId = cfg["Fabric:WorkspaceId"] ?? throw new InvalidOperationException("Fabric:WorkspaceId not set");
        _log = log;
    }

    private async Task<string> GetServiceStorageTokenAsync()
    {
        if (_cachedStorageToken is { } c && c.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(5))
            return c.Token;
        var tok = await _miCred.GetTokenAsync(new TokenRequestContext(new[] { "https://storage.azure.com/.default" }));
        _cachedStorageToken = (tok.Token, tok.ExpiresOn);
        return tok.Token;
    }

    public string WorkspaceId => _workspaceId;

    private HttpClient Client(string userToken)
    {
        var c = _http.CreateClient();
        c.BaseAddress = new Uri(_baseUrl.TrimEnd('/') + "/");
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", userToken);
        return c;
    }

    public async Task<List<LakehouseInfo>> ListLakehousesAsync(string userToken)
    {
        var c = Client(userToken);
        var resp = await c.GetAsync($"workspaces/{_workspaceId}/lakehouses");
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var result = new List<LakehouseInfo>();
        if (doc.RootElement.TryGetProperty("value", out var arr))
        {
            foreach (var e in arr.EnumerateArray())
            {
                result.Add(new LakehouseInfo(
                    e.GetProperty("id").GetString()!,
                    e.GetProperty("displayName").GetString()!,
                    _workspaceId,
                    e.TryGetProperty("description", out var d) ? d.GetString() : null));
            }
        }
        return result;
    }

    public async Task<List<SourceTable>> ListTablesAsync(string userToken, string lakehouseId, string? onelakeToken = null)
    {
        var c = Client(userToken);
        var resp = await c.GetAsync($"workspaces/{_workspaceId}/lakehouses/{lakehouseId}/tables");
        var json = await resp.Content.ReadAsStringAsync();

        if (resp.IsSuccessStatusCode)
        {
            using var doc = JsonDocument.Parse(json);
            var result = new List<SourceTable>();
            if (doc.RootElement.TryGetProperty("data", out var arr))
            {
                foreach (var e in arr.EnumerateArray())
                {
                    result.Add(new SourceTable(
                        e.GetProperty("name").GetString()!,
                        e.TryGetProperty("location", out var loc) ? (loc.GetString() ?? "") : ""));
                }
            }
            return result;
        }

        // Schema-enabled lakehouse fallback: list via OneLake DFS.
        // Prefer caller-supplied user token; fall back to App Service MI (workspace Viewer).
        if (json.Contains("UnsupportedOperationForSchemasEnabledLakehouse", StringComparison.OrdinalIgnoreCase))
        {
            var tokenForOnelake = !string.IsNullOrEmpty(onelakeToken)
                ? onelakeToken
                : await GetServiceStorageTokenAsync();
            return await ListTablesViaOnelakeAsync(tokenForOnelake, lakehouseId);
        }

        throw new Exception($"ListTables failed {resp.StatusCode}: {json}");
    }

    /// <summary>
    /// OneLake DFS listing. Discovers tables by finding directories that contain a `_delta_log` child.
    /// Returns table names as their path under `Tables/` (e.g., `SalesLT/Address`).
    /// </summary>
    public async Task<List<SourceTable>> ListTablesViaOnelakeAsync(string onelakeToken, string lakehouseId)
    {
        var hc = _http.CreateClient();
        hc.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", onelakeToken);
        var url = $"https://onelake.dfs.fabric.microsoft.com/{_workspaceId}/{lakehouseId}/?resource=filesystem&recursive=true&directory=Tables";
        var resp = await hc.GetAsync(url);
        var raw = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new Exception($"OneLake list failed {resp.StatusCode}: {raw}");
        using var doc = JsonDocument.Parse(raw);
        var paths = doc.RootElement.GetProperty("paths");
        var allPaths = new List<string>();
        foreach (var p in paths.EnumerateArray())
            allPaths.Add(p.GetProperty("name").GetString() ?? "");
        // Tables are directories whose immediate child is `_delta_log`.
        // OneLake DFS listing for schema-enabled lakehouses sometimes prepends an extra
        // `Tables/` to paths, so strip up to two leading `Tables/` segments to get the
        // path Spark actually reads (e.g. real: `Tables/SalesLT/Address`, listed: `Tables/Tables/SalesLT/Address`).
        var deltaLogs = allPaths
            .Where(p => p.EndsWith("/_delta_log"))
            .Select(p => p[..^"/_delta_log".Length])
            .Select(p => p.StartsWith("Tables/Tables/") ? p["Tables/Tables/".Length..]
                       : p.StartsWith("Tables/") ? p["Tables/".Length..]
                       : p)
            .Distinct()
            .OrderBy(p => p)
            .ToList();
        return deltaLogs.Select(n => new SourceTable(n, $"Tables/{n}")).ToList();
    }

    public async Task<LakehouseInfo> CreateLakehouseAsync(string userToken, string displayName, string? description = null)
    {
        var c = Client(userToken);
        // Retry with -2, -3 suffixes on conflict (display name already in use).
        for (int attempt = 1; attempt <= 4; attempt++)
        {
            var name = attempt == 1 ? displayName : $"{displayName}_{attempt}";
            var body = new { displayName = name, description = description ?? "" };
            var resp = await c.PostAsync($"workspaces/{_workspaceId}/lakehouses",
                new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));
            var raw = await resp.Content.ReadAsStringAsync();
            if (resp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(raw);
                var e = doc.RootElement;
                return new LakehouseInfo(e.GetProperty("id").GetString()!,
                                         e.GetProperty("displayName").GetString()!,
                                         _workspaceId,
                                         e.TryGetProperty("description", out var d) ? d.GetString() : null);
            }
            if (resp.StatusCode == System.Net.HttpStatusCode.Conflict && raw.Contains("ItemDisplayNameAlreadyInUse"))
            {
                _log.LogInformation("Lakehouse '{name}' already exists, retrying with suffix.", name);
                continue;
            }
            throw new Exception($"CreateLakehouse failed {resp.StatusCode}: {raw}");
        }
        throw new Exception("CreateLakehouse failed after retries.");
    }

    /// <summary>
    /// Creates (or updates) a notebook item from a local .ipynb file, parameterised
    /// by replacing __PARAMS_JSON__ token in the first cell with the actual params.
    /// Returns the created item id.
    /// </summary>
    public async Task<string> CreateNotebookAsync(string userToken, string displayName, string ipynbContent)
    {
        var c = Client(userToken);
        var ipynbB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(ipynbContent));
        var platformPayload = new
        {
            displayName,
            description = "Copilot Medallion orchestrator",
            definition = new
            {
                format = "ipynb",
                parts = new[] {
                    new {
                        path = "notebook-content.ipynb",
                        payload = ipynbB64,
                        payloadType = "InlineBase64"
                    }
                }
            }
        };
        var resp = await c.PostAsync($"workspaces/{_workspaceId}/notebooks",
            new StringContent(JsonSerializer.Serialize(platformPayload), Encoding.UTF8, "application/json"));
        var raw = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new Exception($"CreateNotebook failed {resp.StatusCode}: {raw}");
        // Fabric may return 202 Location header for long-running; handle both.
        if (resp.StatusCode == System.Net.HttpStatusCode.Accepted)
        {
            var loc = resp.Headers.Location?.ToString();
            // poll up to 5 minutes
            for (int i = 0; i < 60; i++)
            {
                await Task.Delay(5000);
                var st = await c.GetAsync(loc);
                var sj = await st.Content.ReadAsStringAsync();
                if (!st.IsSuccessStatusCode) continue;
                using var sd = JsonDocument.Parse(sj);
                var status = sd.RootElement.TryGetProperty("status", out var s) ? s.GetString() : null;
                if (status == "Succeeded")
                {
                    // Result item id may be at /response/id or returned via separate /result endpoint
                    if (sd.RootElement.TryGetProperty("response", out var r) && r.TryGetProperty("id", out var id))
                        return id.GetString()!;
                    // Try result endpoint
                    var resultUri = loc!.TrimEnd('/') + "/result";
                    var rr = await c.GetAsync(resultUri);
                    var rjson = await rr.Content.ReadAsStringAsync();
                    using var rd = JsonDocument.Parse(rjson);
                    if (rd.RootElement.TryGetProperty("id", out var rid))
                        return rid.GetString()!;
                    throw new Exception($"CreateNotebook succeeded but couldn't extract id. Body: {sj}");
                }
                if (status == "Failed" || status == "Cancelled")
                    throw new Exception($"CreateNotebook {status}: {sj}");
            }
            throw new Exception("CreateNotebook polling timed out after 5 minutes.");
        }
        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.GetProperty("id").GetString()!;
    }

    public async Task<string> RunNotebookAsync(string userToken, string notebookId, object parameters)
    {
        var c = Client(userToken);
        var body = new
        {
            executionData = new
            {
                parameters = parameters
            }
        };
        var resp = await c.PostAsync(
            $"workspaces/{_workspaceId}/items/{notebookId}/jobs/instances?jobType=RunNotebook",
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));
        var raw = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new Exception($"RunNotebook failed {resp.StatusCode}: {raw}");
        var loc = resp.Headers.Location?.ToString() ?? "";
        // Location format: /v1/workspaces/{ws}/items/{id}/jobs/instances/{instanceId}
        var instanceId = loc.Split('/').LastOrDefault() ?? "";
        return instanceId;
    }

    public async Task<(string Status, string? FailureReason)> GetJobStatusAsync(string userToken, string notebookId, string instanceId)
    {
        var c = Client(userToken);
        var resp = await c.GetAsync($"workspaces/{_workspaceId}/items/{notebookId}/jobs/instances/{instanceId}");
        var raw = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) return ("Unknown", raw);
        using var doc = JsonDocument.Parse(raw);
        var status = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() ?? "Unknown" : "Unknown";
        string? fail = null;
        if (doc.RootElement.TryGetProperty("failureReason", out var fr) && fr.ValueKind == JsonValueKind.Object)
            fail = fr.ToString();
        return (status, fail);
    }
}
