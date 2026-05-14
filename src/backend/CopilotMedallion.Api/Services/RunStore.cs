using Microsoft.Data.Sqlite;
using CopilotMedallion.Api.Models;

namespace CopilotMedallion.Api.Services;

public interface IRunStore
{
    Task<RunInfo> CreateAsync(string runId, string branch, string specUrl,
                              string workspaceId, string sourceLakehouseId, string tablesCsv, string targetLakehouseName,
                              string? itemId);
    Task<RunInfo?> GetAsync(string runId);
    Task<RunInfo?> GetLatestByItemAsync(string itemId);
    Task UpdateStatusAsync(string runId, string status, string? message = null);
    Task UpdateBuildAsync(string runId, string targetLakehouseId, string? notebookId, string? jobInstanceId);
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
    }

    public async Task<RunInfo> CreateAsync(string runId, string branch, string specUrl,
                                            string workspaceId, string sourceLakehouseId, string tablesCsv, string targetLakehouseName,
                                            string? itemId)
    {
        var now = DateTime.UtcNow.ToString("o");
        await using var c = new SqliteConnection(_connStr); await c.OpenAsync();
        var cmd = c.CreateCommand();
        cmd.CommandText = @"INSERT INTO runs(run_id,status,branch,spec_url,workspace_id,source_lakehouse_id,tables_csv,target_lakehouse_name,item_id,created_at,updated_at)
                            VALUES($id,'SpecsReady',$br,$su,$ws,$sl,$tc,$tl,$it,$ts,$ts)";
        cmd.Parameters.AddWithValue("$id", runId);
        cmd.Parameters.AddWithValue("$br", branch);
        cmd.Parameters.AddWithValue("$su", specUrl);
        cmd.Parameters.AddWithValue("$ws", workspaceId);
        cmd.Parameters.AddWithValue("$sl", sourceLakehouseId);
        cmd.Parameters.AddWithValue("$tc", tablesCsv);
        cmd.Parameters.AddWithValue("$tl", targetLakehouseName);
        cmd.Parameters.AddWithValue("$it", (object?)itemId ?? DBNull.Value);
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
        cmd.CommandText = "UPDATE runs SET target_lakehouse_id=$t, notebook_id=$n, job_instance_id=$j, updated_at=$ts WHERE run_id=$id";
        cmd.Parameters.AddWithValue("$t", (object?)targetLakehouseId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$n", (object?)notebookId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$j", (object?)jobInstanceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$id", runId);
        await cmd.ExecuteNonQueryAsync();
    }

    private const string SelectSql = @"SELECT run_id,status,branch,spec_url,workspace_id,source_lakehouse_id,tables_csv,
                                              target_lakehouse_id,target_lakehouse_name,notebook_id,job_instance_id,message,
                                              created_at,updated_at FROM runs";

    private static RunInfo Read(SqliteDataReader r)
    {
        string? s(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        return new RunInfo(
            r.GetString(0), r.GetString(1), s(2), s(3), s(4), s(5), s(6),
            s(7), s(8), s(9), s(10), s(11),
            DateTime.Parse(r.GetString(12)), DateTime.Parse(r.GetString(13)));
    }
}
