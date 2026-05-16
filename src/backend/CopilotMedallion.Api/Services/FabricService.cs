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

    public string DefaultWorkspaceId => _workspaceId;
    public string ResolveWorkspaceId(string? requested) => string.IsNullOrWhiteSpace(requested) ? _workspaceId : requested;

    private HttpClient Client(string userToken)
    {
        var c = _http.CreateClient();
        c.BaseAddress = new Uri(_baseUrl.TrimEnd('/') + "/");
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", userToken);
        return c;
    }

    public async Task<List<LakehouseInfo>> ListLakehousesAsync(string userToken, string? workspaceId = null)
    {
        var ws = ResolveWorkspaceId(workspaceId);
        var c = Client(userToken);
        var resp = await c.GetAsync($"workspaces/{ws}/lakehouses");
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
                    ws,
                    e.TryGetProperty("description", out var d) ? d.GetString() : null));
            }
        }
        return result;
    }

    public async Task<(string Id, string DisplayName)?> GetWorkspaceAsync(string userToken, string workspaceId)
    {
        try
        {
            var c = Client(userToken);
            var resp = await c.GetAsync($"workspaces/{workspaceId}");
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var e = doc.RootElement;
            var id = e.TryGetProperty("id", out var i) ? i.GetString() ?? workspaceId : workspaceId;
            var name = e.TryGetProperty("displayName", out var d) ? d.GetString() ?? "" : "";
            return (id, name);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "GetWorkspaceAsync({ws}) threw", workspaceId);
            return null;
        }
    }


    public async Task<List<SourceTable>> ListTablesAsync(string userToken, string lakehouseId, string? onelakeToken = null, string? workspaceId = null)
    {
        var ws = ResolveWorkspaceId(workspaceId);
        var c = Client(userToken);
        var resp = await c.GetAsync($"workspaces/{ws}/lakehouses/{lakehouseId}/tables");
        var json = await resp.Content.ReadAsStringAsync();

        if (resp.IsSuccessStatusCode)
        {
            using var doc = JsonDocument.Parse(json);
            var result = new List<SourceTable>();
            if (doc.RootElement.TryGetProperty("data", out var arr))
            {
                foreach (var e in arr.EnumerateArray())
                {
                    result.Add(new SourceTable(e.GetProperty("name").GetString()!));
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
            return await ListTablesViaOnelakeAsync(tokenForOnelake, lakehouseId, ws);
        }

        throw new Exception($"ListTables failed {resp.StatusCode}: {json}");
    }

    public async Task<List<SourceTable>> ListTablesViaOnelakeAsync(string onelakeToken, string lakehouseId, string? workspaceId = null)
    {
        var ws = ResolveWorkspaceId(workspaceId);
        var hc = _http.CreateClient();
        hc.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", onelakeToken);
        var url = $"https://onelake.dfs.fabric.microsoft.com/{ws}/{lakehouseId}/?resource=filesystem&recursive=true&directory=Tables";
        var resp = await hc.GetAsync(url);
        var raw = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new Exception($"OneLake list failed {resp.StatusCode}: {raw}");
        using var doc = JsonDocument.Parse(raw);
        var paths = doc.RootElement.GetProperty("paths");
        var allPaths = new List<string>();
        foreach (var p in paths.EnumerateArray())
            allPaths.Add(p.GetProperty("name").GetString() ?? "");
        var deltaLogs = allPaths
            .Where(p => p.EndsWith("/_delta_log"))
            .Select(p => p[..^"/_delta_log".Length])
            .Select(p => p.StartsWith("Tables/Tables/") ? p["Tables/Tables/".Length..]
                       : p.StartsWith("Tables/") ? p["Tables/".Length..]
                       : p)
            .Distinct()
            .OrderBy(p => p)
            .ToList();
        return deltaLogs.Select(n => new SourceTable(n)).ToList();
    }

    /// <summary>
    /// Fetches a small text file from a target lakehouse's Files/ area via the OneLake DFS data plane.
    /// Used to surface the notebook's persisted error trace back to the web UI.
    /// </summary>
    public async Task<string?> TryDownloadTextFromLakehouseAsync(string onelakeToken, string workspaceId, string lakehouseId, string relativePath)
    {
        try
        {
            var hc = _http.CreateClient();
            hc.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", onelakeToken);
            var clean = relativePath.TrimStart('/');
            var url = $"https://onelake.dfs.fabric.microsoft.com/{workspaceId}/{lakehouseId}/{clean}";
            var resp = await hc.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return null;
            var bytes = await resp.Content.ReadAsByteArrayAsync();
            if (bytes.Length > 256 * 1024) bytes = bytes[..(256 * 1024)];
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "TryDownloadTextFromLakehouseAsync failed for {ws}/{lh}/{p}", workspaceId, lakehouseId, relativePath);
            return null;
        }
    }

    /// <summary>
    /// Reads a Delta table's schema from its _delta_log/00000000000000000000.json metaData action.
    /// Prefers a caller-supplied storage token (the user's, which usually has access to any workspace
    /// they're a member of); falls back to the App Service MI token.
    /// </summary>
    public async Task<string?> GetTableSchemaSummaryAsync(string workspaceId, string lakehouseId, string tableRelativePath, string? userStorageToken = null)
    {
        try
        {
            var tok = !string.IsNullOrEmpty(userStorageToken)
                ? userStorageToken
                : await GetServiceStorageTokenAsync();
            var hc = _http.CreateClient();
            hc.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tok);
            var url = $"https://onelake.dfs.fabric.microsoft.com/{workspaceId}/{lakehouseId}/Tables/{tableRelativePath}/_delta_log/00000000000000000000.json";
            var resp = await hc.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return null;
            var raw = await resp.Content.ReadAsStringAsync();
            string? schemaJson = null;
            foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("metaData", out var md)
                        && md.TryGetProperty("schemaString", out var ss))
                    {
                        schemaJson = ss.GetString();
                        break;
                    }
                }
                catch { /* skip invalid lines */ }
            }
            if (string.IsNullOrEmpty(schemaJson)) return null;
            using var sdoc = JsonDocument.Parse(schemaJson);
            if (!sdoc.RootElement.TryGetProperty("fields", out var fields)) return null;
            var parts = new List<string>();
            foreach (var f in fields.EnumerateArray())
            {
                var name = f.TryGetProperty("name", out var n) ? n.GetString() : "?";
                var type = f.TryGetProperty("type", out var t)
                    ? (t.ValueKind == JsonValueKind.String ? t.GetString() : t.ToString())
                    : "?";
                parts.Add($"{name}:{type}");
            }
            return string.Join(", ", parts);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "GetTableSchemaSummaryAsync failed for {ws}/{lh}/{t}", workspaceId, lakehouseId, tableRelativePath);
            return null;
        }
    }

    /// <summary>
    /// Best-effort: add the App Service's Managed Identity as a Contributor of the given workspace,
    /// so subsequent OneLake DFS reads/writes via the MI token succeed. Requires the calling user to
    /// be a workspace Admin. No-op if it fails.
    /// </summary>
    public async Task EnsureMiHasWorkspaceAccessAsync(string userToken, string workspaceId)
    {
        try
        {
            var miOid = Environment.GetEnvironmentVariable("WEBSITE_OWNER_NAME") != null
                ? (await GetMiObjectIdAsync()) : null;
            if (string.IsNullOrEmpty(miOid))
            {
                _log.LogInformation("MI object id not resolved; skipping workspace role assignment");
                return;
            }
            var c = Client(userToken);
            // Check if already assigned.
            var listResp = await c.GetAsync($"workspaces/{workspaceId}/roleAssignments");
            if (listResp.IsSuccessStatusCode)
            {
                var listJson = await listResp.Content.ReadAsStringAsync();
                if (listJson.Contains(miOid!, StringComparison.OrdinalIgnoreCase))
                {
                    _log.LogInformation("MI {oid} already has workspace {ws} access", miOid, workspaceId);
                    return;
                }
            }
            var body = new
            {
                principal = new { id = miOid, type = "ServicePrincipal" },
                role = "Contributor"
            };
            var resp = await c.PostAsync($"workspaces/{workspaceId}/roleAssignments",
                new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));
            var raw = await resp.Content.ReadAsStringAsync();
            if (resp.IsSuccessStatusCode)
                _log.LogInformation("Added MI {oid} as Contributor of workspace {ws}", miOid, workspaceId);
            else
                _log.LogWarning("Could not add MI to workspace {ws} ({s}): {b}", workspaceId, resp.StatusCode, raw);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "EnsureMiHasWorkspaceAccessAsync failed for {ws}", workspaceId);
        }
    }

    private string? _cachedMiOid;
    private async Task<string?> GetMiObjectIdAsync()
    {
        if (_cachedMiOid is not null) return _cachedMiOid;
        try
        {
            // Acquire an ARM token using the MI; introspect via /me-like call via Graph instead.
            var tok = await _miCred.GetTokenAsync(new TokenRequestContext(new[] { "https://graph.microsoft.com/.default" }));
            var hc = _http.CreateClient();
            hc.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tok.Token);
            var r = await hc.GetAsync("https://graph.microsoft.com/v1.0/servicePrincipals?$filter=displayName eq 'copilot-roesli'&$select=id,displayName");
            if (r.IsSuccessStatusCode)
            {
                var json = await r.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("value", out var arr) && arr.GetArrayLength() > 0)
                {
                    _cachedMiOid = arr[0].GetProperty("id").GetString();
                    return _cachedMiOid;
                }
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "GetMiObjectIdAsync failed"); }
        return null;
    }

    /// <summary>
    /// Same as above but uses the App Service's Managed Identity token instead of a user token.
    /// </summary>
    public async Task<string?> TryDownloadTextWithServiceTokenAsync(string workspaceId, string lakehouseId, string relativePath)
    {
        try
        {
            var tok = await GetServiceStorageTokenAsync();
            return await TryDownloadTextFromLakehouseAsync(tok, workspaceId, lakehouseId, relativePath);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "TryDownloadTextWithServiceTokenAsync failed for {ws}/{lh}/{p}", workspaceId, lakehouseId, relativePath);
            return null;
        }
    }

    /// <summary>
    /// Writes a small text file into a lakehouse's Files/ area using the OneLake DFS data plane
    /// (three-step create+append+flush). Uses the service MI token.
    /// </summary>
    public async Task UploadFileToLakehouseAsync(string workspaceId, string lakehouseId, string relativePath, string content, string contentType = "text/plain; charset=utf-8")
    {
        var tok = await GetServiceStorageTokenAsync();
        var hc = _http.CreateClient();
        hc.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tok);
        var clean = relativePath.TrimStart('/');
        var baseUrl = $"https://onelake.dfs.fabric.microsoft.com/{workspaceId}/{lakehouseId}/{clean}";
        var bytes = Encoding.UTF8.GetBytes(content);

        // 1) Create (or overwrite) the file.
        var createReq = new HttpRequestMessage(HttpMethod.Put, $"{baseUrl}?resource=file");
        createReq.Headers.TryAddWithoutValidation("x-ms-content-type", contentType);
        createReq.Content = new ByteArrayContent(Array.Empty<byte>());
        var createResp = await hc.SendAsync(createReq);
        if (!createResp.IsSuccessStatusCode)
        {
            var raw = await createResp.Content.ReadAsStringAsync();
            throw new Exception($"OneLake create failed {createResp.StatusCode}: {raw}");
        }

        // 2) Append bytes at offset 0.
        var appendReq = new HttpRequestMessage(HttpMethod.Patch, $"{baseUrl}?action=append&position=0");
        appendReq.Content = new ByteArrayContent(bytes);
        appendReq.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        var appendResp = await hc.SendAsync(appendReq);
        if (!appendResp.IsSuccessStatusCode)
        {
            var raw = await appendResp.Content.ReadAsStringAsync();
            throw new Exception($"OneLake append failed {appendResp.StatusCode}: {raw}");
        }

        // 3) Flush at total length to commit.
        var flushReq = new HttpRequestMessage(HttpMethod.Patch, $"{baseUrl}?action=flush&position={bytes.Length}");
        flushReq.Content = new ByteArrayContent(Array.Empty<byte>());
        var flushResp = await hc.SendAsync(flushReq);
        if (!flushResp.IsSuccessStatusCode)
        {
            var raw = await flushResp.Content.ReadAsStringAsync();
            throw new Exception($"OneLake flush failed {flushResp.StatusCode}: {raw}");
        }
    }

    public async Task<LakehouseInfo> CreateLakehouseAsync(string userToken, string displayName, string? description = null, string? workspaceId = null)
    {
        var ws = ResolveWorkspaceId(workspaceId);
        var c = Client(userToken);
        var sanitized = SanitizeLakehouseName(displayName);
        for (int attempt = 1; attempt <= 4; attempt++)
        {
            var name = attempt == 1 ? sanitized : $"{sanitized}_{attempt}";
            var body = new {
                displayName = name,
                description = description ?? "",
                creationPayload = new { enableSchemas = true }
            };
            var resp = await c.PostAsync($"workspaces/{ws}/lakehouses",
                new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));
            var raw = await resp.Content.ReadAsStringAsync();
            if (resp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(raw);
                var e = doc.RootElement;
                return new LakehouseInfo(e.GetProperty("id").GetString()!,
                                         e.GetProperty("displayName").GetString()!,
                                         ws,
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
    /// Fabric Lakehouse display names allow letters, digits, and underscore only.
    /// Replace anything else (dots, spaces, dashes, etc.) with underscore, collapse,
    /// trim, and ensure the result starts with a letter.
    /// </summary>
    private static string SanitizeLakehouseName(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return $"CopilotMedallion_{DateTime.UtcNow:yyyyMMddHHmmss}";
        var sb = new StringBuilder(input.Length);
        foreach (var ch in input)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_') sb.Append(ch);
            else sb.Append('_');
        }
        var s = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), "_+", "_").Trim('_');
        if (s.Length == 0) s = $"CopilotMedallion_{DateTime.UtcNow:yyyyMMddHHmmss}";
        if (!char.IsLetter(s[0])) s = "lh_" + s;
        if (s.Length > 256) s = s.Substring(0, 256).TrimEnd('_');
        return s;
    }

    public async Task<string> CreateNotebookAsync(string userToken, string displayName, string ipynbContent, string? workspaceId = null)
    {
        var ws = ResolveWorkspaceId(workspaceId);
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
                    new { path = "notebook-content.ipynb", payload = ipynbB64, payloadType = "InlineBase64" }
                }
            }
        };
        var resp = await c.PostAsync($"workspaces/{ws}/notebooks",
            new StringContent(JsonSerializer.Serialize(platformPayload), Encoding.UTF8, "application/json"));
        return await ReadItemIdOrPollLroAsync(c, resp);
    }

    /// <summary>
    /// Updates an existing notebook's definition in place. Returns when LRO completes.
    /// </summary>
    public async Task UpdateNotebookDefinitionAsync(string userToken, string notebookId, string ipynbContent, string? workspaceId = null)
    {
        var ws = ResolveWorkspaceId(workspaceId);
        var c = Client(userToken);
        var ipynbB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(ipynbContent));
        var payload = new
        {
            definition = new
            {
                format = "ipynb",
                parts = new[] {
                    new { path = "notebook-content.ipynb", payload = ipynbB64, payloadType = "InlineBase64" }
                }
            }
        };
        var resp = await c.PostAsync($"workspaces/{ws}/notebooks/{notebookId}/updateDefinition?updateMetadata=false",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
        var raw = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new Exception($"UpdateNotebookDefinition failed {resp.StatusCode}: {raw}");
        if (resp.StatusCode == System.Net.HttpStatusCode.Accepted)
        {
            var loc = resp.Headers.Location?.ToString();
            for (int i = 0; i < 60; i++)
            {
                await Task.Delay(5000);
                var st = await c.GetAsync(loc);
                var sj = await st.Content.ReadAsStringAsync();
                if (!st.IsSuccessStatusCode) continue;
                using var sd = JsonDocument.Parse(sj);
                var status = sd.RootElement.TryGetProperty("status", out var s) ? s.GetString() : null;
                if (status == "Succeeded") return;
                if (status == "Failed" || status == "Cancelled")
                    throw new Exception($"UpdateNotebookDefinition {status}: {sj}");
            }
            throw new Exception("UpdateNotebookDefinition polling timed out.");
        }
    }

    /// <summary>
    /// Try to find a notebook by display name in the workspace. Returns null if not found.
    /// </summary>
    public async Task<string?> FindNotebookIdAsync(string userToken, string displayName, string? workspaceId = null)
    {
        var ws = ResolveWorkspaceId(workspaceId);
        var c = Client(userToken);
        var resp = await c.GetAsync($"workspaces/{ws}/notebooks");
        if (!resp.IsSuccessStatusCode) return null;
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("value", out var arr))
            foreach (var e in arr.EnumerateArray())
                if (string.Equals(e.GetProperty("displayName").GetString(), displayName, StringComparison.OrdinalIgnoreCase))
                    return e.GetProperty("id").GetString();
        return null;
    }

    public async Task<LakehouseInfo?> FindLakehouseByIdAsync(string userToken, string lakehouseId, string? workspaceId = null)
    {
        var ws = ResolveWorkspaceId(workspaceId);
        var c = Client(userToken);
        var resp = await c.GetAsync($"workspaces/{ws}/lakehouses/{lakehouseId}");
        if (!resp.IsSuccessStatusCode) return null;
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var e = doc.RootElement;
        return new LakehouseInfo(e.GetProperty("id").GetString()!,
                                 e.GetProperty("displayName").GetString()!,
                                 ws,
                                 e.TryGetProperty("description", out var d) ? d.GetString() : null);
    }

    public async Task<LakehouseInfo?> FindLakehouseByNameAsync(string userToken, string displayName, string? workspaceId = null)
    {
        try
        {
            var all = await ListLakehousesAsync(userToken, workspaceId);
            return all.FirstOrDefault(l => string.Equals(l.DisplayName, displayName, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "FindLakehouseByNameAsync failed for '{n}'", displayName);
            return null;
        }
    }

    /// <summary>
    /// Deletes everything under the Tables/ directory of a lakehouse (preserves Files/).
    /// Uses OneLake DFS path delete with recursive=true via the App Service MI token.
    /// </summary>
    public async Task ClearLakehouseTablesAsync(string workspaceId, string lakehouseId)
    {
        var tok = await GetServiceStorageTokenAsync();
        var hc = _http.CreateClient();
        hc.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tok);
        // First list Tables/ subdirectories (schemas for schema-enabled lakehouses, or just tables).
        var listUrl = $"https://onelake.dfs.fabric.microsoft.com/{workspaceId}/{lakehouseId}/?resource=filesystem&directory=Tables&recursive=false";
        var listResp = await hc.GetAsync(listUrl);
        if (!listResp.IsSuccessStatusCode)
        {
            _log.LogInformation("ClearLakehouseTablesAsync: list returned {s}, treating Tables/ as empty", listResp.StatusCode);
            return;
        }
        var raw = await listResp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(raw);
        if (!doc.RootElement.TryGetProperty("paths", out var paths)) return;
        var entries = paths.EnumerateArray()
            .Select(p => new
            {
                Name = p.GetProperty("name").GetString() ?? "",
                IsDir = p.TryGetProperty("isDirectory", out var d) && d.GetString() == "true"
            })
            .Where(p => !string.IsNullOrEmpty(p.Name))
            .ToList();
        foreach (var entry in entries)
        {
            var delUrl = $"https://onelake.dfs.fabric.microsoft.com/{workspaceId}/{lakehouseId}/{entry.Name}?recursive=true";
            try
            {
                var delResp = await hc.DeleteAsync(delUrl);
                if (!delResp.IsSuccessStatusCode)
                {
                    var body = await delResp.Content.ReadAsStringAsync();
                    _log.LogWarning("ClearLakehouseTables: delete {p} returned {s}: {b}", entry.Name, delResp.StatusCode, body);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "ClearLakehouseTables: failed to delete {p}", entry.Name);
            }
        }
    }

    /// <summary>
    /// Renames a Fabric item via PATCH. Used to suffix `_old` to artifacts being replaced.
    /// </summary>
    public async Task RenameItemAsync(string userToken, string itemId, string newDisplayName, string? workspaceId = null)
    {
        var ws = ResolveWorkspaceId(workspaceId);
        var c = Client(userToken);
        var resp = await c.PatchAsync($"workspaces/{ws}/items/{itemId}",
            new StringContent(JsonSerializer.Serialize(new { displayName = newDisplayName }), Encoding.UTF8, "application/json"));
        if (!resp.IsSuccessStatusCode)
        {
            var raw = await resp.Content.ReadAsStringAsync();
            throw new Exception($"RenameItem failed {resp.StatusCode}: {raw}");
        }
    }

    private static async Task<string> ReadItemIdOrPollLroAsync(HttpClient c, HttpResponseMessage resp)
    {
        var raw = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new Exception($"CreateNotebook failed {resp.StatusCode}: {raw}");
        if (resp.StatusCode == System.Net.HttpStatusCode.Accepted)
        {
            var loc = resp.Headers.Location?.ToString();
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
                    if (sd.RootElement.TryGetProperty("response", out var r) && r.TryGetProperty("id", out var id))
                        return id.GetString()!;
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

    public async Task<string> RunNotebookAsync(string userToken, string notebookId, object parameters, string? workspaceId = null,
        string? defaultLakehouseId = null, string? defaultLakehouseName = null)
    {
        var ws = ResolveWorkspaceId(workspaceId);
        var c = Client(userToken);
        object executionData;
        if (!string.IsNullOrEmpty(defaultLakehouseId) && !string.IsNullOrEmpty(defaultLakehouseName))
        {
            executionData = new
            {
                parameters,
                configuration = new
                {
                    defaultLakehouse = new
                    {
                        name = defaultLakehouseName,
                        id = defaultLakehouseId,
                        workspaceId = ws
                    }
                }
            };
        }
        else
        {
            executionData = new { parameters };
        }
        var body = new { executionData };
        var json = JsonSerializer.Serialize(body);
        _log.LogDebug("RunNotebook POST body: {body}", json);
        var resp = await c.PostAsync(
            $"workspaces/{ws}/items/{notebookId}/jobs/instances?jobType=RunNotebook",
            new StringContent(json, Encoding.UTF8, "application/json"));
        var raw = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new Exception($"RunNotebook failed {resp.StatusCode}: {raw}");
        var loc = resp.Headers.Location?.ToString() ?? "";
        return loc.Split('/').LastOrDefault() ?? "";
    }

    public async Task<(string Status, string? FailureReason)> GetJobStatusAsync(string userToken, string notebookId, string instanceId, string? workspaceId = null)
    {
        var ws = ResolveWorkspaceId(workspaceId);
        var c = Client(userToken);
        var resp = await c.GetAsync($"workspaces/{ws}/items/{notebookId}/jobs/instances/{instanceId}");
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
