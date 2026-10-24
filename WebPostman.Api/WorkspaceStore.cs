using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace WebPostman.Api;

public sealed class RevisionConflictException : Exception;

public sealed class WorkspaceStore
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public WorkspaceStore(IWebHostEnvironment env, IConfiguration configuration)
        : this(configuration["WebPostman:DataDirectory"] ?? Path.Combine(env.ContentRootPath, "App_Data")) { }

    public WorkspaceStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "workspace.db") }.ToString();
        using var db = Open(); using var tx = db.BeginTransaction();
        Run(db, tx, "CREATE TABLE IF NOT EXISTS workspace (id INTEGER PRIMARY KEY CHECK(id=1),json TEXT NOT NULL); CREATE TABLE IF NOT EXISTS workspace_state (id INTEGER PRIMARY KEY,revision INTEGER NOT NULL); CREATE TABLE IF NOT EXISTS collections (id TEXT PRIMARY KEY,json TEXT NOT NULL,position INTEGER NOT NULL); CREATE TABLE IF NOT EXISTS environments (id TEXT PRIMARY KEY,json TEXT NOT NULL,position INTEGER NOT NULL); CREATE TABLE IF NOT EXISTS history (id TEXT PRIMARY KEY,at TEXT NOT NULL,json TEXT NOT NULL); CREATE INDEX IF NOT EXISTS history_at ON history(at DESC,id DESC);");
        if (Scalar(db, tx, "SELECT revision FROM workspace_state WHERE id=1") is null)
        {
            // Keep the original JSON row intact as a migration backup.
            var legacy = Scalar(db, tx, "SELECT json FROM workspace WHERE id=1") as string;
            var old = legacy is null ? new Workspace([], [], []) : JsonSerializer.Deserialize<Workspace>(legacy, _json)!;
            foreach (var item in old.Collections) Upsert(db, tx, "collections", item.Id, item);
            foreach (var item in old.Environments) Upsert(db, tx, "environments", item.Id, item);
            foreach (var item in old.History) InsertHistory(db, tx, item);
            Run(db, tx, "INSERT INTO workspace_state VALUES(1,0)");
        }
        tx.Commit();
    }
    private SqliteConnection Open() { var db = new SqliteConnection(_connectionString); db.Open(); return db; }
    private static SqliteCommand Command(SqliteConnection db, SqliteTransaction tx, string sql, params (string, object)[] parameters)
    {
        var command = db.CreateCommand(); command.Transaction = tx; command.CommandText = sql;
        foreach (var (key, value) in parameters) command.Parameters.AddWithValue(key, value);
        return command;
    }
    private static object? Scalar(SqliteConnection db, SqliteTransaction tx, string sql) { using var c = Command(db, tx, sql); return c.ExecuteScalar(); }
    private static void Run(SqliteConnection db, SqliteTransaction tx, string sql) { using var c = Command(db, tx, sql); c.ExecuteNonQuery(); }
    private List<T> Entities<T>(SqliteConnection db, SqliteTransaction tx, string table)
    {
        using var c = Command(db, tx, $"SELECT json FROM {table} ORDER BY position,id"); using var r = c.ExecuteReader(); var items = new List<T>();
        while (r.Read()) items.Add(JsonSerializer.Deserialize<T>(r.GetString(0), _json)!);
        return items;
    }
    private void Upsert<T>(SqliteConnection db, SqliteTransaction tx, string table, string id, T value)
    {
        using var c = Command(db, tx, $"INSERT INTO {table}(id,json,position) VALUES($id,$json,(SELECT COALESCE(MAX(position),-1)+1 FROM {table})) ON CONFLICT(id) DO UPDATE SET json=excluded.json", ("$id", id), ("$json", JsonSerializer.Serialize(value, _json)));
        c.ExecuteNonQuery();
    }
    private static void Delete(SqliteConnection db, SqliteTransaction tx, string table, string id)
    { using var c = Command(db, tx, $"DELETE FROM {table} WHERE id=$id", ("$id", id)); c.ExecuteNonQuery(); }
    public async Task<Workspace> ReadAsync()
    {
        await _gate.WaitAsync();
        try
        {
            using var db = Open(); using var tx = db.BeginTransaction(deferred: true);
            var result = new Workspace(Entities<CollectionItem>(db, tx, "collections"), Entities<EnvironmentItem>(db, tx, "environments"), [], Convert.ToInt64(Scalar(db, tx, "SELECT revision FROM workspace_state WHERE id=1")));
            tx.Commit(); return result;
        }
        finally { _gate.Release(); }
    }
    public Task<long> WriteAsync(Workspace workspace) => SaveAsync(workspace.Revision, (db, tx) =>
    {
        Run(db, tx, "DELETE FROM collections; DELETE FROM environments;");
        foreach (var item in workspace.Collections) Upsert(db, tx, "collections", item.Id, item);
        foreach (var item in workspace.Environments) Upsert(db, tx, "environments", item.Id, item);
    });
    public Task<long> PatchAsync(WorkspaceChanges changes) => SaveAsync(changes.Revision, (db, tx) =>
    {
        foreach (var id in changes.DeletedCollections) Delete(db, tx, "collections", id);
        foreach (var id in changes.DeletedEnvironments) Delete(db, tx, "environments", id);
        foreach (var item in changes.Collections) Upsert(db, tx, "collections", item.Id, item);
        foreach (var item in changes.Environments) Upsert(db, tx, "environments", item.Id, item);
        if (Convert.ToInt64(Scalar(db, tx, "SELECT COUNT(*) FROM collections")) > 500 || Convert.ToInt64(Scalar(db, tx, "SELECT COUNT(*) FROM environments")) > 100) throw new ArgumentException("工作区数据超过限制。");
    });
    private async Task<long> SaveAsync(long revision, Action<SqliteConnection, SqliteTransaction> write)
    {
        await _gate.WaitAsync();
        try
        {
            using var db = Open(); using var tx = db.BeginTransaction();
            var current = Convert.ToInt64(Scalar(db, tx, "SELECT revision FROM workspace_state WHERE id=1"));
            if (current != revision) throw new RevisionConflictException();
            write(db, tx); Run(db, tx, "UPDATE workspace_state SET revision=revision+1 WHERE id=1"); tx.Commit(); return current + 1;
        }
        finally { _gate.Release(); }
    }
    private void InsertHistory(SqliteConnection db, SqliteTransaction tx, HistoryEntry entry)
    {
        using var c = Command(db, tx, "INSERT OR IGNORE INTO history VALUES($id,$at,$json)", ("$id", entry.Id), ("$at", entry.At.ToString("O")), ("$json", JsonSerializer.Serialize(entry, _json))); c.ExecuteNonQuery();
    }
    public async Task AddHistoryAsync(HistoryEntry entry)
    {
        await _gate.WaitAsync();
        try { using var db = Open(); using var tx = db.BeginTransaction(); InsertHistory(db, tx, entry); Run(db, tx, "DELETE FROM history WHERE id IN (SELECT id FROM history ORDER BY at DESC,id DESC LIMIT -1 OFFSET 1000)"); tx.Commit(); }
        finally { _gate.Release(); }
    }
    public async Task<HistoryPage> ReadHistoryAsync(int offset, int limit)
    {
        await _gate.WaitAsync();
        try
        {
            using var db = Open(); using var tx = db.BeginTransaction(deferred: true);
            var total = Convert.ToInt32(Scalar(db, tx, "SELECT COUNT(*) FROM history"));
            using var c = Command(db, tx, "SELECT json FROM history ORDER BY at DESC,id DESC LIMIT $limit OFFSET $offset", ("$limit", limit), ("$offset", offset));
            var items = new List<HistoryEntry>();
            using (var r = c.ExecuteReader()) while (r.Read()) items.Add(JsonSerializer.Deserialize<HistoryEntry>(r.GetString(0), _json)!);
            tx.Commit(); return new(items, total);
        }
        finally { _gate.Release(); }
    }
}
