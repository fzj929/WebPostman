using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebPostman.Api.Data;

namespace WebPostman.Api;

public sealed class MockServerService(IDbContextFactory<WebPostmanDbContext> factory, MockRuntime runtime, IConfiguration configuration)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
    private static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Json)!;
    private async Task<T> Locked<T>(Func<WebPostmanDbContext, Task<T>> action)
    {
        await runtime.Gate.WaitAsync();
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            try { return await action(db); }
            catch (DbUpdateConcurrencyException) { throw new MockConflictException("模拟配置已被其他页面修改，请刷新后再保存。"); }
        }
        finally { runtime.Gate.Release(); }
    }
    private static async Task<MockServerEntity> Owned(WebPostmanDbContext db, string owner, string id) =>
        await db.MockServers.SingleOrDefaultAsync(x => x.Id == id && x.UserId == owner) ?? throw new KeyNotFoundException();
    private static MockRule Rule(MockRuleEntity x) => new(x.Id, x.Name, x.Method, x.Path, x.Query, x.Enabled,
        Read<MockResponse>(x.ResponseJson), Read<MockResponse>(x.OriginalJson), x.SourceUrl, x.Revision);
    private async Task<MockServer> Detail(WebPostmanDbContext db, MockServerEntity server)
    {
        var rules = await db.MockRules.AsNoTracking().Where(x => x.ServerId == server.Id).OrderBy(x => x.Name).ThenBy(x => x.Id).ToListAsync();
        return new(server.Id, server.Name, server.Port, Read<List<string>>(server.CorsJson), server.Revision,
            runtime.Running(server.Id), rules.Count, runtime.Calls(server.Id), rules.Select(Rule).ToList());
    }
    private void ServerInput(MockServerInput input)
    {
        MockValidation.Name(input.Name); MockValidation.Origins(input.CorsOrigins);
        var minimum = Math.Clamp(configuration.GetValue("Mock:MinPort", 1024), 1024, 65535);
        var maximum = Math.Clamp(configuration.GetValue("Mock:MaxPort", 65535), minimum, 65535);
        if (input.Port < minimum || input.Port > maximum) throw new ArgumentException($"模拟端口范围为 {minimum}～{maximum}。");
    }
    public Task<List<MockServer>> ListAsync(string owner) => Locked(async db =>
    {
        var servers = await db.MockServers.AsNoTracking().Where(x => x.UserId == owner).OrderBy(x => x.Name).ToListAsync();
        var counts = await db.MockRules.Where(x => db.MockServers.Any(s => s.Id == x.ServerId && s.UserId == owner))
            .GroupBy(x => x.ServerId).Select(x => new { Id = x.Key, Count = x.Count() }).ToDictionaryAsync(x => x.Id, x => x.Count);
        return servers.Select(x => new MockServer(x.Id, x.Name, x.Port, Read<List<string>>(x.CorsJson), x.Revision,
            runtime.Running(x.Id), counts.GetValueOrDefault(x.Id), runtime.Calls(x.Id))).ToList();
    });
    public Task<MockServer> GetAsync(string owner, string id) => Locked(async db => await Detail(db, await Owned(db, owner, id)));
    public Task<MockServer> CreateAsync(string owner, MockServerInput input) => Locked(async db =>
    {
        ServerInput(input);
        if (await db.MockServers.CountAsync(x => x.UserId == owner) >= 20) throw new ArgumentException("每个账户最多创建 20 个模拟服务。");
        var server = new MockServerEntity { Id = Guid.NewGuid().ToString("N"), UserId = owner, Name = MockValidation.Name(input.Name), Port = input.Port, CorsJson = Serialize(MockValidation.Origins(input.CorsOrigins)) };
        db.MockServers.Add(server); await db.SaveChangesAsync(); return await Detail(db, server);
    });
    public Task<MockServer> UpdateAsync(string owner, string id, MockServerInput input) => Locked(async db =>
    {
        var server = await Owned(db, owner, id); ServerInput(input);
        if (server.Revision != input.Revision) throw new MockConflictException("模拟配置已更新，请刷新后再保存。");
        if (runtime.Running(id) && input.Port != server.Port) throw new MockConflictException("更换端口前请先停止模拟服务。");
        server.Name = MockValidation.Name(input.Name); server.Port = input.Port;
        server.CorsJson = Serialize(MockValidation.Origins(input.CorsOrigins)); server.Revision++;
        await db.SaveChangesAsync(); var detail = await Detail(db, server); runtime.Refresh(detail); return detail;
    });
    public Task<bool> DeleteAsync(string owner, string id) => Locked(async db =>
    {
        var server = await Owned(db, owner, id); await runtime.StopListenerAsync(id);
        db.MockServers.Remove(server); await db.SaveChangesAsync(); return true;
    });
    public Task<MockServer> StartAsync(string owner, string id) => Locked(async db =>
    {
        var server = await Owned(db, owner, id); var snapshot = await Detail(db, server);
        if (!snapshot.Rules!.Any(x => x.Enabled)) throw new ArgumentException("请先保存至少一个启用的模拟接口。");
        await runtime.StartListenerAsync(snapshot); return snapshot with { Running = true };
    });
    public Task<MockServer> StopAsync(string owner, string id) => Locked(async db =>
    { var server = await Owned(db, owner, id); await runtime.StopListenerAsync(id); return await Detail(db, server); });
    public Task<List<MockCall>> LogsAsync(string owner, string id) => Locked(async db =>
    { await Owned(db, owner, id); return runtime.Logs(id); });

    public Task<MockServer> SaveRuleAsync(string owner, string serverId, string? ruleId, MockRuleInput input) => Locked(async db =>
    {
        var server = await Owned(db, owner, serverId);
        if (input.Truncated) throw new ArgumentException("响应已截断，无法录制完整回放，请换用未截断的响应。");
        var name = MockValidation.Name(input.Name); var method = MockValidation.Method(input.Method);
        var path = MockValidation.Path(input.Path); var query = MockValidation.Query(input.Query);
        var response = MockValidation.Response(input.Response);
        var key = MockValidation.RouteKey(method, path, query);
        if (await db.MockRules.AnyAsync(x => x.ServerId == serverId && x.RouteKey == key && x.Id != ruleId))
            throw new MockConflictException("该方法、路径和查询条件已存在，请编辑已有接口或更换匹配条件。");
        MockRuleEntity entity;
        if (ruleId is null)
        {
            if (await db.MockRules.CountAsync(x => x.ServerId == serverId) >= 200) throw new ArgumentException("每个模拟服务最多 200 个接口。");
            if (input.SourceUrl is null || input.SourceUrl.Length > 32768) throw new ArgumentException("来源 URL 无效。");
            var source = Uri.TryCreate(input.SourceUrl, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
                ? new UriBuilder(uri) { UserName = "", Password = "", Query = "", Fragment = "" }.Uri.ToString() : "";
            entity = new() { Id = Guid.NewGuid().ToString("N"), ServerId = serverId, OriginalJson = Serialize(response), SourceUrl = source };
            db.MockRules.Add(entity);
        }
        else
        {
            entity = await db.MockRules.SingleOrDefaultAsync(x => x.Id == ruleId && x.ServerId == serverId) ?? throw new KeyNotFoundException();
            if (entity.Revision != input.Revision) throw new MockConflictException("接口已被其他页面修改，请刷新后再编辑。");
            entity.Revision++;
        }
        entity.Name = name; entity.Method = method; entity.Path = path; entity.Query = query;
        entity.RouteKey = key; entity.Enabled = input.Enabled; entity.ResponseJson = Serialize(response); server.Revision++;
        var storedSize = await db.MockRules.Where(x => x.ServerId == serverId && x.Id != entity.Id).SumAsync(x => (long)x.ResponseJson.Length + x.OriginalJson.Length);
        if (storedSize + entity.ResponseJson.Length + entity.OriginalJson.Length > 32 * 1024 * 1024) throw new ArgumentException("此模拟服务的响应快照合计超过 32 MB，请拆分服务。");
        await db.SaveChangesAsync(); var detail = await Detail(db, server); runtime.Refresh(detail); return detail;
    });
    public Task<MockServer> DeleteRuleAsync(string owner, string serverId, string ruleId, long revision) => Locked(async db =>
    {
        var server = await Owned(db, owner, serverId);
        var entity = await db.MockRules.SingleOrDefaultAsync(x => x.Id == ruleId && x.ServerId == serverId) ?? throw new KeyNotFoundException();
        if (entity.Revision != revision) throw new MockConflictException("接口已更新，请刷新后重试。");
        db.MockRules.Remove(entity); server.Revision++;
        await db.SaveChangesAsync(); var detail = await Detail(db, server); runtime.Refresh(detail); return detail;
    });
}
