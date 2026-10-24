using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace WebPostman.Api;

public sealed class ShareStore(AccountStore accounts)
{
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private static bool Sensitive(string key) => Regex.IsMatch(key, "authorization|cookie|token|secret|password|passwd|key|credential|session|signature|jwt", RegexOptions.IgnoreCase);
    private static string ScrubUrl(string value, string authKey)
    {
        var fragment = value.IndexOf('#'); if (fragment >= 0) value = value[..fragment];
        value = Regex.Replace(value, @"(https?://)[^/@\s]+@", "$1", RegexOptions.IgnoreCase);
        return Regex.Replace(value, @"([?&])([^=&#]+)=([^&#]*)", match =>
        {
            string key; try { key = Uri.UnescapeDataString(match.Groups[2].Value); } catch { return match.Groups[1].Value + match.Groups[2].Value + "=REDACTED"; }
            return Sensitive(key) || key.Equals(authKey, StringComparison.OrdinalIgnoreCase) ? match.Groups[1].Value + match.Groups[2].Value + "=REDACTED" : match.Value;
        });
    }
    private static string ScrubBody(RequestItem request)
    {
        if (string.IsNullOrWhiteSpace(request.Body)) return "";
        if (request.BodyType != "json") return "";
        try
        {
            var node = JsonNode.Parse(request.Body);
            void Visit(JsonNode? current)
            {
                if (current is JsonObject obj) foreach (var key in obj.Select(x => x.Key).ToArray()) { if (Sensitive(key)) obj[key] = "REDACTED"; else Visit(obj[key]); }
                if (current is JsonArray array) foreach (var item in array) Visit(item);
            }
            Visit(node); return node?.ToJsonString() ?? "";
        }
        catch (JsonException) { return ""; }
    }
    private static AuthSettings ScrubAuth(AuthSettings auth) => auth.Type switch
    {
        "inherit" => new("inherit"),
        "basic" => new("basic", Username: "{{username}}", Password: "{{password}}"),
        "bearer" => new("bearer", Token: "{{token}}"),
        "apiKey" => new("apiKey", Key: auth.Key, Value: "{{api_key}}", In: auth.In),
        _ => new()
    };
    private static AuthSettings EffectiveAuth(CollectionItem collection, RequestItem request)
    {
        if (request.Auth.Type != "inherit") return request.Auth;
        var folder = collection.Folders.Find(x => x.Id == request.FolderId); var seen = new HashSet<string>();
        while (folder is not null && seen.Add(folder.Id))
        {
            if (folder.Auth.Type != "inherit") return folder.Auth;
            folder = collection.Folders.Find(x => x.Id == folder.ParentId);
        }
        return collection.Auth;
    }
    private static RequestItem Scrub(RequestItem request, AuthSettings effectiveAuth) => request with
    {
        Url = ScrubUrl(request.Url, effectiveAuth.Key), Auth = ScrubAuth(request.Auth), Cookies = [], Files = null, ShowSensitiveTrace = false,
        Headers = request.Headers.Select(x => Sensitive(x.Key) || (!string.IsNullOrEmpty(effectiveAuth.Key) && x.Key.Equals(effectiveAuth.Key, StringComparison.OrdinalIgnoreCase)) ? x with { Value = "REDACTED" } : x).ToList(),
        Params = request.Params.Select(x => Sensitive(x.Key) || (!string.IsNullOrEmpty(effectiveAuth.Key) && x.Key.Equals(effectiveAuth.Key, StringComparison.OrdinalIgnoreCase)) ? x with { Value = "REDACTED" } : x).ToList(),
        Form = request.Form.Select(x => Sensitive(x.Key) ? x with { Value = "REDACTED" } : x).ToList(),
        Body = ScrubBody(request), FileFields = request.FileFields?.Select(x => x with { FileName = Path.GetFileName(x.FileName.Replace('\\', '/')) }).ToList()
    };
    public async Task<(CollectionItem Collection, long Revision)> PreviewAsync(string userId, ShareInput input)
    {
        if (input.Kind is not ("request" or "collection") || string.IsNullOrWhiteSpace(input.Title) || input.Title.Length > 100 || input.Description is null || input.Description.Length > 1000) throw new ArgumentException("请选择分享类型，标题为 1～100 字，描述最多 1000 字。");
        var workspace = await accounts.Workspace(userId).ReadAsync();
        var collection = workspace.Collections.Find(x => x.Id == input.CollectionId) ?? throw new KeyNotFoundException();
        if (input.Kind == "request")
        {
            var request = collection.Requests.Find(x => x.Id == input.RequestId) ?? throw new KeyNotFoundException();
            var effective = EffectiveAuth(collection, request);
            collection = new(collection.Id, input.Title.Trim(), new(), [], [Scrub(request, effective) with { Name = input.Title.Trim(), FolderId = "", Auth = ScrubAuth(effective) }]);
        }
        else collection = collection with { Name = input.Title.Trim(), Auth = ScrubAuth(collection.Auth), Folders = collection.Folders.Select(x => x with { Auth = ScrubAuth(x.Auth) }).ToList(), Requests = collection.Requests.Select(x => Scrub(x, EffectiveAuth(collection, x))).ToList() };
        if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(collection, _json)) > 2 * 1024 * 1024) throw new ArgumentException("分享内容不能超过 2 MB，请拆分集合。");
        return (collection, workspace.Revision);
    }
    public async Task<SharedItem> PublishAsync(string userId, ShareInput input)
    {
        var preview = await PreviewAsync(userId, input);
        if (input.Revision != preview.Revision) throw new RevisionConflictException();
        var item = new SharedItem(Guid.NewGuid().ToString("N"), userId, "用户 " + userId[..6], input.Kind, input.Title.Trim(), input.Description.Trim(), DateTimeOffset.UtcNow, preview.Collection);
        using var db = accounts.Open(); using var c = AccountStore.Command(db, "INSERT INTO shares SELECT $id,$owner,$kind,$title,$description,$at,$json WHERE (SELECT COUNT(*) FROM shares WHERE owner_id=$owner)<200", ("$id", item.Id), ("$owner", userId), ("$kind", item.Kind), ("$title", item.Title), ("$description", item.Description), ("$at", item.CreatedAt.ToString("O")), ("$json", JsonSerializer.Serialize(item, _json)));
        if (c.ExecuteNonQuery() == 0) throw new ArgumentException("每个账户最多保留 200 条分享，请先撤回旧分享。");
        return item;
    }
    public SharePage List(string? query, string? kind, string? owner, int offset, int limit)
    {
        var where = "WHERE ($query='' OR instr(lower(title),lower($query))>0 OR instr(lower(description),lower($query))>0) AND ($kind='' OR kind=$kind) AND ($owner='' OR owner_id=$owner)";
        using var db = accounts.Open();
        object Param(string? value) => value ?? "";
        using var count = AccountStore.Command(db, "SELECT COUNT(*) FROM shares " + where, ("$query", Param(query)), ("$kind", Param(kind)), ("$owner", Param(owner)));
        var total = Convert.ToInt32(count.ExecuteScalar());
        using var c = AccountStore.Command(db, "SELECT json FROM shares " + where + " ORDER BY created_at DESC,id DESC LIMIT $limit OFFSET $offset", ("$query", Param(query)), ("$kind", Param(kind)), ("$owner", Param(owner)), ("$limit", limit), ("$offset", offset));
        using var reader = c.ExecuteReader(); var items = new List<ShareSummary>();
        while (reader.Read()) { var x = JsonSerializer.Deserialize<SharedItem>(reader.GetString(0), _json)!; items.Add(new(x.Id, x.OwnerId, x.Author, x.Kind, x.Title, x.Description, x.CreatedAt, x.Collection.Requests.Count)); }
        return new(items, total);
    }
    public SharedItem Get(string id)
    {
        using var db = accounts.Open(); using var c = AccountStore.Command(db, "SELECT json FROM shares WHERE id=$id", ("$id", id));
        return c.ExecuteScalar() is string json ? JsonSerializer.Deserialize<SharedItem>(json, _json)! : throw new KeyNotFoundException();
    }
    public void Delete(string id, string owner)
    {
        using var db = accounts.Open(); using var c = AccountStore.Command(db, "DELETE FROM shares WHERE id=$id AND owner_id=$owner", ("$id", id), ("$owner", owner));
        if (c.ExecuteNonQuery() == 0) throw new KeyNotFoundException();
    }
}
