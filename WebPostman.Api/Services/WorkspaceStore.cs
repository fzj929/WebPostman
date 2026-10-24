using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebPostman.Api.Data;

namespace WebPostman.Api;

public sealed class RevisionConflictException : Exception;

/// <summary>Every query is scoped to the authenticated owner; contexts are never shared across requests.</summary>
public sealed class WorkspaceStore(IDbContextFactory<WebPostmanDbContext> factory, string userId)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
    private static List<T> Deserialize<T>(IEnumerable<string> rows) => rows.Select(x => JsonSerializer.Deserialize<T>(x, Json)!).ToList();

    public async Task<Workspace> ReadAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        var revision = await db.Workspaces.Where(x => x.UserId == userId).Select(x => x.Revision).SingleAsync();
        var collections = await db.Collections.AsNoTracking().Where(x => x.UserId == userId).OrderBy(x => x.Position).ThenBy(x => x.Id).Select(x => x.Json).ToListAsync();
        var environments = await db.Environments.AsNoTracking().Where(x => x.UserId == userId).OrderBy(x => x.Position).ThenBy(x => x.Id).Select(x => x.Json).ToListAsync();
        await transaction.CommitAsync();
        return new(Deserialize<CollectionItem>(collections), Deserialize<EnvironmentItem>(environments), [], revision);
    }

    public Task<long> WriteAsync(Workspace workspace) => SaveAsync(workspace.Revision, async db =>
    {
        await db.Collections.Where(x => x.UserId == userId).ExecuteDeleteAsync();
        await db.Environments.Where(x => x.UserId == userId).ExecuteDeleteAsync();
        db.Collections.AddRange(workspace.Collections.Select((x, i) => new CollectionEntity { UserId = userId, Id = x.Id, Json = Serialize(x), Position = i }));
        db.Environments.AddRange(workspace.Environments.Select((x, i) => new EnvironmentEntity { UserId = userId, Id = x.Id, Json = Serialize(x), Position = i }));
    });

    public Task<long> PatchAsync(WorkspaceChanges changes) => SaveAsync(changes.Revision, async db =>
    {
        await db.Collections.Where(x => x.UserId == userId && changes.DeletedCollections.Contains(x.Id)).ExecuteDeleteAsync();
        await db.Environments.Where(x => x.UserId == userId && changes.DeletedEnvironments.Contains(x.Id)).ExecuteDeleteAsync();
        var collections = await db.Collections.Where(x => x.UserId == userId).ToListAsync();
        var environments = await db.Environments.Where(x => x.UserId == userId).ToListAsync();
        var nextCollection = collections.Select(x => x.Position).DefaultIfEmpty(-1).Max() + 1;
        var nextEnvironment = environments.Select(x => x.Position).DefaultIfEmpty(-1).Max() + 1;
        foreach (var item in changes.Collections)
        {
            var row = collections.Find(x => x.Id == item.Id);
            if (row is null) { row = new() { UserId = userId, Id = item.Id, Position = nextCollection++ }; collections.Add(row); db.Collections.Add(row); }
            row.Json = Serialize(item);
        }
        foreach (var item in changes.Environments)
        {
            var row = environments.Find(x => x.Id == item.Id);
            if (row is null) { row = new() { UserId = userId, Id = item.Id, Position = nextEnvironment++ }; environments.Add(row); db.Environments.Add(row); }
            row.Json = Serialize(item);
        }
        if (collections.Count > 500 || environments.Count > 100) throw new ArgumentException("工作区数据超过限制。");
    });

    private async Task<long> SaveAsync(long revision, Func<WebPostmanDbContext, Task> write)
    {
        await using var db = await factory.CreateDbContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        // A database compare-and-swap also protects writes from other application processes.
        var updated = await db.Workspaces.Where(x => x.UserId == userId && x.Revision == revision)
            .ExecuteUpdateAsync(x => x.SetProperty(w => w.Revision, w => w.Revision + 1));
        if (updated == 0) throw new RevisionConflictException();
        await write(db); await db.SaveChangesAsync(); await transaction.CommitAsync();
        return revision + 1;
    }

    public async Task AddHistoryAsync(HistoryEntry entry)
    {
        await using var db = await factory.CreateDbContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        // Serialize retention per user without changing the workspace edit revision.
        await db.Workspaces.Where(x => x.UserId == userId).ExecuteUpdateAsync(x => x.SetProperty(w => w.Revision, w => w.Revision));
        if (!await db.History.AnyAsync(x => x.UserId == userId && x.Id == entry.Id))
            db.History.Add(new() { UserId = userId, Id = entry.Id, At = entry.At.ToUnixTimeMilliseconds(), Json = Serialize(entry) });
        await db.SaveChangesAsync();
        var expired = await db.History.Where(x => x.UserId == userId).OrderByDescending(x => x.At).ThenByDescending(x => x.Id).Skip(1000).Select(x => x.Id).ToListAsync();
        if (expired.Count > 0) await db.History.Where(x => x.UserId == userId && expired.Contains(x.Id)).ExecuteDeleteAsync();
        await transaction.CommitAsync();
    }

    public async Task<HistoryPage> ReadHistoryAsync(int offset, int limit)
    {
        await using var db = await factory.CreateDbContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        var query = db.History.AsNoTracking().Where(x => x.UserId == userId);
        var total = await query.CountAsync();
        var rows = await query.OrderByDescending(x => x.At).ThenByDescending(x => x.Id).Skip(offset).Take(limit).Select(x => x.Json).ToListAsync();
        await transaction.CommitAsync(); return new(Deserialize<HistoryEntry>(rows), total);
    }
}
