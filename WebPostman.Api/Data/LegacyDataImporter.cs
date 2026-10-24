using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace WebPostman.Api.Data;

/// <summary>Read-only adapter for databases written before the EF Core upgrade.</summary>
public sealed class LegacyDataImporter(IDbContextFactory<WebPostmanDbContext> factory, DatabaseSettings settings)
{
    private readonly object _initializationLock = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static SqliteConnection Open(string file)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open(); return connection;
    }
    private static bool HasTable(SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name";
        command.Parameters.AddWithValue("$name", name);
        return Convert.ToInt64(command.ExecuteScalar()) != 0;
    }
    private static List<string[]> Rows(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand(); command.CommandText = sql;
        using var reader = command.ExecuteReader(); var result = new List<string[]>();
        while (reader.Read()) result.Add(Enumerable.Range(0, reader.FieldCount).Select(i => reader.GetValue(i).ToString()!).ToArray());
        return result;
    }

    public void ImportAccounts()
    {
        if (!settings.ImportLegacyData) return;
        using var db = factory.CreateDbContext();
        if (db.DataImports.Any(x => x.Id == "legacy-accounts-v1")) return;
        var file = Path.Combine(settings.DataDirectory, "accounts.db");
        // Read everything before writing: the source may be the target SQLite file.
        using var source = File.Exists(file) ? Open(file) : null;
        var users = source is not null && HasTable(source, "users") ? Rows(source, "SELECT id,email,password_hash FROM users") : [];
        var sessions = source is not null && HasTable(source, "sessions") ? Rows(source, "SELECT token_hash,user_id,expires FROM sessions") : [];
        var shares = source is not null && HasTable(source, "shares") ? Rows(source, "SELECT id,owner_id,kind,title,description,created_at,json FROM shares") : [];
        using var transaction = db.Database.BeginTransaction();
        foreach (var row in users) db.Users.Add(new() { Id = row[0], Email = row[1], PasswordHash = row[2] });
        foreach (var row in sessions) db.Sessions.Add(new() { TokenHash = row[0], UserId = row[1], Expires = long.Parse(row[2]) });
        foreach (var row in shares) db.Shares.Add(new() { Id = row[0], OwnerId = row[1], Kind = row[2], Title = row[3], Description = row[4], CreatedAt = DateTimeOffset.Parse(row[5]).ToUnixTimeMilliseconds(), Json = row[6] });
        db.DataImports.Add(new() { Id = "legacy-accounts-v1" });
        db.SaveChanges(); transaction.Commit();
    }

    public void EnsureWorkspace(string userId)
    {
        lock (_initializationLock)
        {
            using var db = factory.CreateDbContext();
            if (db.Workspaces.Any(x => x.UserId == userId)) return;
            var workspace = new Workspace([], [], []);
            var file = Path.Combine(settings.DataDirectory, "users", userId, "workspace.db");
            if (settings.ImportLegacyData && File.Exists(file))
            {
                using var source = Open(file);
                if (HasTable(source, "workspace_state") && Rows(source, "SELECT revision FROM workspace_state WHERE id=1") is { Count: > 0 } state)
                {
                    workspace = new(
                        Rows(source, "SELECT json FROM collections ORDER BY position,id").Select(x => JsonSerializer.Deserialize<CollectionItem>(x[0], Json)!).ToList(),
                        Rows(source, "SELECT json FROM environments ORDER BY position,id").Select(x => JsonSerializer.Deserialize<EnvironmentItem>(x[0], Json)!).ToList(),
                        Rows(source, "SELECT json FROM history ORDER BY at DESC,id DESC LIMIT 1000").Select(x => JsonSerializer.Deserialize<HistoryEntry>(x[0], Json)!).ToList(), long.Parse(state[0][0]));
                }
                else if (HasTable(source, "workspace") && Rows(source, "SELECT json FROM workspace WHERE id=1") is { Count: > 0 } rows)
                    workspace = JsonSerializer.Deserialize<Workspace>(rows[0][0], Json)!;
            }
            using var transaction = db.Database.BeginTransaction();
            db.Workspaces.Add(new() { UserId = userId, Revision = workspace.Revision });
            db.Collections.AddRange(workspace.Collections.Select((x, i) => new CollectionEntity { UserId = userId, Id = x.Id, Json = JsonSerializer.Serialize(x, Json), Position = i }));
            db.Environments.AddRange(workspace.Environments.Select((x, i) => new EnvironmentEntity { UserId = userId, Id = x.Id, Json = JsonSerializer.Serialize(x, Json), Position = i }));
            db.History.AddRange(workspace.History.OrderByDescending(x => x.At).Take(1000).DistinctBy(x => x.Id).Select(x => new HistoryEntity { UserId = userId, Id = x.Id, At = x.At.ToUnixTimeMilliseconds(), Json = JsonSerializer.Serialize(x, Json) }));
            try { db.SaveChanges(); transaction.Commit(); }
            catch (DbUpdateException)
            {
                transaction.Rollback();
                using var check = factory.CreateDbContext();
                // Another application process may have initialized this workspace first.
                if (!check.Workspaces.Any(x => x.UserId == userId)) throw;
            }
        }
    }
}
