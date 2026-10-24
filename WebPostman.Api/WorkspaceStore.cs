using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace WebPostman.Api;

public sealed class WorkspaceStore
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public WorkspaceStore(IWebHostEnvironment env, IConfiguration configuration)
    {
        var directory = configuration["WebPostman:DataDirectory"] ?? Path.Combine(env.ContentRootPath, "App_Data");
        Directory.CreateDirectory(directory);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "workspace.db") }.ToString();
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS workspace (id INTEGER PRIMARY KEY CHECK(id = 1), json TEXT NOT NULL)";
        command.ExecuteNonQuery();
    }

    public async Task<Workspace> ReadAsync()
    {
        await _gate.WaitAsync();
        try { return await ReadUnlockedAsync(); }
        finally { _gate.Release(); }
    }

    public async Task WriteAsync(Workspace workspace)
    {
        await _gate.WaitAsync();
        try
        {
            var current = await ReadUnlockedAsync();
            await WriteUnlockedAsync(workspace with { History = current.History });
        }
        finally { _gate.Release(); }
    }

    public async Task AddHistoryAsync(HistoryEntry entry)
    {
        await _gate.WaitAsync();
        try
        {
            var workspace = await ReadUnlockedAsync();
            workspace.History.Insert(0, entry);
            if (workspace.History.Count > 100) workspace.History.RemoveRange(100, workspace.History.Count - 100);
            await WriteUnlockedAsync(workspace);
        }
        finally { _gate.Release(); }
    }

    private async Task<Workspace> ReadUnlockedAsync()
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM workspace WHERE id = 1";
        var json = (string?)await command.ExecuteScalarAsync();
        return json is null
            ? new Workspace([], [], [])
            : JsonSerializer.Deserialize<Workspace>(json, _json) ?? new Workspace([], [], []);
    }

    private async Task WriteUnlockedAsync(Workspace workspace)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO workspace (id, json) VALUES (1, $json) ON CONFLICT(id) DO UPDATE SET json = excluded.json";
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(workspace, _json));
        await command.ExecuteNonQueryAsync();
    }
}
