using Microsoft.Data.Sqlite;
using CopilotMedallion.Api.Models;

namespace CopilotMedallion.Api.Services;

public interface IRunStore
{
    Task<RunInfo> CreateAsync(string runId, string branch, string specUrl,
                              string workspaceId, string sourceLakehouseId, string tablesCsv, string targetLakehouseName,
                              string? itemId, string? targetLakehouseId, string? targetWorkspaceId, string? sourceWorkspaceId);
    Task<RunInfo?> GetAsync(string runId);
    Task<RunInfo?> GetLatestByItemAsync(string itemId);
    Task UpdateStatusAsync(string runId, string status, string? message = null);
    Task UpdateBuildAsync(string runId, string targetLakehouseId, string? notebookId, string? jobInstanceId);
    Task UpdateLayerAsync(string runId, string layer, string? notebookId, string? jobInstanceId);
    Task UpdateSpecMarkdownAsync(string runId, string specMarkdown);
    Task SaveGuidanceSnapshotAsync(string runId, string content);
    Task<List<(int Id, DateTime CapturedAt, string RunId, string Content)>> ListGuidanceSnapshotsAsync(int limit);
}

public class SqliteRunStore : IRunStore
{
    private readonly string _connStr;

    public SqliteRunStore(IConfiguration cfg)
    {
        var dbPath = Environment.GetEnvironmentVariable("RUN_DB_PATH")
            ?? Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? AppContext.BaseDirectory, "data", "runs.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connStr = $"Data Source={dbPath}";
        using var c = new SqliteConnection(_connStr);
        c.Open();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = @"CREATE TABLE IF NOT EXISTS runs(
                run_id TEXT PRIMARY KEY, status TEXT, branch TEXT, spec_url TEXT,
                source_lakehouse_id TEXT, tables_csv TEXT,
                target_lakehouse_id TEXT, target_lakehouse_name TEXT,
                notebook_id TEXT, job_instance_id TEXT, message TEXT,
                created_at TEXT, updated_at TEXT);";
            cmd.ExecuteNonQuery();
        }
        using (var cmd = c.CreateCommand())
        {
            // Long-running learning store: every saved spec snapshots its '## Generic guidance' section
            // here so the user can build up a corpus of cross-cutting rules even as lakehouses come and go.
            cmd.CommandText = @"CREATE TABLE IF NOT EXISTS guidance_snapshots(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                captured_at TEXT NOT NULL,
                run_id TEXT,
                content TEXT NOT NULL);";
            cmd.ExecuteNonQuery();
        }
        // Forward-compatible column additions
        var existing = new List<string>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(runs)";
            using var r = cmd.ExecuteReader();
            while (r.Read()) existing.Add(r.GetString(1));
        }
        if (!existing.Contains("workspace_id"))
        {
            using var alter = c.CreateCommand();
            alter.CommandText = "ALTER TABLE runs ADD COLUMN workspace_id TEXT";
            alter.ExecuteNonQuery();
        }
        if (!existing.Contains("item_id"))
        {
            using var alter = c.CreateCommand();
            alter.CommandText = "ALTER TABLE runs ADD COLUMN item_id TEXT";
            alter.ExecuteNonQuery();
            using var idx = c.CreateCommand();
            idx.CommandText = "CREATE INDEX IF NOT EXISTS ix_runs_item_id ON runs(item_id)";
            idx.ExecuteNonQuery();
        }
        if (!existing.Contains("target_workspace_id"))
        {
            using var alter = c.CreateCommand();
            alter.CommandText = "ALTER TABLE runs ADD COLUMN target_workspace_id TEXT";
            alter.ExecuteNonQuery();
        }
        if (!existing.Contains("source_workspace_id"))
        {
            using var alter = c.CreateCommand();
            alter.CommandText = "ALTER TABLE runs ADD COLUMN source_workspace_id TEXT";
            alter.ExecuteNonQuery();
        }
        foreach (var col in new[] { "bronze_notebook_id","silver_notebook_id","gold_notebook_id",
                                     "bronze_job_id","silver_job_id","gold_job_id","current_layer",
                                     "reporting_notebook_id","reporting_job_id","spec_markdown" })
        {
            if (!existing.Contains(col))
            {
                using var alter = c.CreateCommand();
                alter.CommandText = $"ALTER TABLE runs ADD COLUMN {col} TEXT";
                alter.ExecuteNonQuery();
            }
        }
    }

    public async Task<RunInfo> CreateAsync(string runId, string branch, string specUrl,
                                            string workspaceId, string sourceLakehouseId, string tablesCsv, string targetLakehouseName,
                                            string? itemId, string? targetLakehouseId, string? targetWorkspaceId, string? sourceWorkspaceId)
    {
        var now = DateTime.UtcNow.ToString("o");
        await using var c = new SqliteConnection(_connStr); await c.OpenAsync();
        var cmd = c.CreateCommand();
        cmd.CommandText = @"INSERT INTO runs(run_id,status,branch,spec_url,workspace_id,source_lakehouse_id,tables_csv,target_lakehouse_name,item_id,target_lakehouse_id,target_workspace_id,source_workspace_id,created_at,updated_at)
                            VALUES($id,'SpecsReady',$br,$su,$ws,$sl,$tc,$tl,$it,$tlid,$tws,$sws,$ts,$ts)";
        cmd.Parameters.AddWithValue("$id", runId);
        cmd.Parameters.AddWithValue("$br", branch);
        cmd.Parameters.AddWithValue("$su", specUrl);
        cmd.Parameters.AddWithValue("$ws", workspaceId);
        cmd.Parameters.AddWithValue("$sl", sourceLakehouseId);
        cmd.Parameters.AddWithValue("$tc", tablesCsv);
        cmd.Parameters.AddWithValue("$tl", targetLakehouseName);
        cmd.Parameters.AddWithValue("$it", (object?)itemId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tlid", (object?)targetLakehouseId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tws", (object?)targetWorkspaceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sws", (object?)sourceWorkspaceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ts", now);
        await cmd.ExecuteNonQueryAsync();
        return (await GetAsync(runId))!;
    }

    public async Task<RunInfo?> GetAsync(string runId)
    {
        await using var c = new SqliteConnection(_connStr); await c.OpenAsync();
        var cmd = c.CreateCommand();
        cmd.CommandText = SelectSql + " WHERE run_id=$id";
        cmd.Parameters.AddWithValue("$id", runId);
        using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? Read(r) : null;
    }

    public async Task<RunInfo?> GetLatestByItemAsync(string itemId)
    {
        await using var c = new SqliteConnection(_connStr); await c.OpenAsync();
        var cmd = c.CreateCommand();
        cmd.CommandText = SelectSql + " WHERE item_id=$it ORDER BY created_at DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$it", itemId);
        using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? Read(r) : null;
    }

    public async Task UpdateStatusAsync(string runId, string status, string? message = null)
    {
        await using var c = new SqliteConnection(_connStr); await c.OpenAsync();
        var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE runs SET status=$s, message=$m, updated_at=$ts WHERE run_id=$id";
        cmd.Parameters.AddWithValue("$s", status);
        cmd.Parameters.AddWithValue("$m", (object?)message ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$id", runId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task UpdateBuildAsync(string runId, string targetLakehouseId, string? notebookId, string? jobInstanceId)
    {
        await using var c = new SqliteConnection(_connStr); await c.OpenAsync();
        var cmd = c.CreateCommand();
        // COALESCE so callers can pass null to leave a field unchanged (instead of nulling it out).
        cmd.CommandText = "UPDATE runs SET target_lakehouse_id=COALESCE($t, target_lakehouse_id), notebook_id=COALESCE($n, notebook_id), job_instance_id=COALESCE($j, job_instance_id), updated_at=$ts WHERE run_id=$id";
        cmd.Parameters.AddWithValue("$t", (object?)targetLakehouseId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$n", (object?)notebookId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$j", (object?)jobInstanceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$id", runId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task UpdateLayerAsync(string runId, string layer, string? notebookId, string? jobInstanceId)
    {
        var nbCol = $"{layer}_notebook_id";
        var jobCol = $"{layer}_job_id";
        await using var c = new SqliteConnection(_connStr); await c.OpenAsync();
        var cmd = c.CreateCommand();
        cmd.CommandText = $"UPDATE runs SET {nbCol}=COALESCE($n,{nbCol}), {jobCol}=COALESCE($j,{jobCol}), current_layer=$l, notebook_id=COALESCE($n,notebook_id), job_instance_id=COALESCE($j,job_instance_id), updated_at=$ts WHERE run_id=$id";
        cmd.Parameters.AddWithValue("$n", (object?)notebookId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$j", (object?)jobInstanceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$l", layer);
        cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$id", runId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task UpdateSpecMarkdownAsync(string runId, string specMarkdown)
    {
        await using var c = new SqliteConnection(_connStr); await c.OpenAsync();
        var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE runs SET spec_markdown=$s, updated_at=$ts WHERE run_id=$id";
        cmd.Parameters.AddWithValue("$s", (object?)specMarkdown ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$id", runId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task SaveGuidanceSnapshotAsync(string runId, string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return;
        await using var c = new SqliteConnection(_connStr); await c.OpenAsync();
        // Dedup: don't insert if the most recent snapshot matches.
        var checkCmd = c.CreateCommand();
        checkCmd.CommandText = "SELECT content FROM guidance_snapshots ORDER BY id DESC LIMIT 1";
        var latest = (string?)await checkCmd.ExecuteScalarAsync();
        if (string.Equals(latest, content, StringComparison.Ordinal)) return;
        var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO guidance_snapshots(captured_at, run_id, content) VALUES($ts, $r, $c)";
        cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$r", (object?)runId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$c", content);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<(int Id, DateTime CapturedAt, string RunId, string Content)>> ListGuidanceSnapshotsAsync(int limit)
    {
        await using var c = new SqliteConnection(_connStr); await c.OpenAsync();
        var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, captured_at, run_id, content FROM guidance_snapshots ORDER BY id DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$n", limit);
        var result = new List<(int, DateTime, string, string)>();
        using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            result.Add((r.GetInt32(0), DateTime.Parse(r.GetString(1)), r.IsDBNull(2) ? "" : r.GetString(2), r.GetString(3)));
        }
        return result;
    }

    private const string SelectSql = @"SELECT run_id,status,branch,spec_url,workspace_id,source_lakehouse_id,tables_csv,
                                              target_lakehouse_id,target_lakehouse_name,notebook_id,job_instance_id,message,
                                              created_at,updated_at,target_workspace_id,source_workspace_id,
                                              bronze_notebook_id,silver_notebook_id,gold_notebook_id,
                                              bronze_job_id,silver_job_id,gold_job_id,current_layer,
                                              reporting_notebook_id,reporting_job_id,spec_markdown FROM runs";

    private static RunInfo Read(SqliteDataReader r)
    {
        string? s(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        return new RunInfo(
            r.GetString(0), r.GetString(1), s(2), s(3), s(4), s(5), s(6),
            s(7), s(8), s(9), s(10), s(11),
            DateTime.Parse(r.GetString(12)), DateTime.Parse(r.GetString(13)), s(14), s(15),
            s(16), s(17), s(18), s(19), s(20), s(21), s(22),
            s(23), s(24), s(25));
    }
}
