namespace WebPostman.Api;

public static class InputValidation
{
    private static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition, string message) { if (!condition) throw new ArgumentException(message); }
    private static void Text(string? value, int limit = 1000000) => Require(value is not null && value.Length <= limit, "字段缺失或长度超过限制。");
    private static void Id(string? id) => Require(!string.IsNullOrWhiteSpace(id) && id.Length <= 200, "ID 无效。");
    private static void Rows(List<KeyValueRow>? rows)
    {
        Require(rows is not null && rows.Count <= 1000, "参数列表缺失或超过限制。");
        foreach (var row in rows!) { Require(row is not null, "参数项不能为空。"); Id(row!.Id); Text(row.Key, 8192); Text(row.Value); }
    }
    private static void Auth(AuthSettings? auth, bool inherit = true)
    {
        Require(auth is not null, "缺少授权配置。");
        Require(new[] { "none", "basic", "bearer", "apiKey" }.Contains(auth!.Type) || (inherit && auth.Type == "inherit"), "授权类型无效。");
        Require(auth.In is "header" or "query", "API Key 位置无效。");
        Text(auth.Username); Text(auth.Password); Text(auth.Token); Text(auth.Key, 8192); Text(auth.Value);
    }
    public static void Request(RequestItem? request)
    {
        Require(request is not null, "缺少请求配置。"); var r = request!;
        Id(r.Id); Text(r.Name, 1000); Text(r.Url, 32768); Text(r.Method, 20); Text(r.FolderId, 200);
        Require(new[] { "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS" }.Contains(r.Method.ToUpperInvariant()), "不支持的 HTTP 方法。");
        Require(new[] { "none", "json", "text", "form", "multipart" }.Contains(r.BodyType), "正文格式无效。");
        Require(r.TimeoutSeconds is >= 1 and <= 600, "超时应在 1～600 秒之间。");
        Rows(r.Params); Rows(r.Headers); Rows(r.Form); if (r.Cookies is not null) Rows(r.Cookies); Auth(r.Auth); Text(r.Body, 10 * 1024 * 1024);
        Require((r.Files?.Count ?? 0) <= 10, "最多上传 10 个文件。");
        Require((r.FileFields?.Count ?? 0) <= 100, "文件字段超过限制。");
        foreach (var file in r.FileFields ?? []) { Require(file is not null, "文件字段无效。"); Text(file.Key, 8192); Text(file.FileName, 1000); }
        long total = 0;
        foreach (var file in r.Files ?? [])
        {
            Require(file is not null, "文件项无效。"); Text(file!.Key, 8192); Text(file.FileName, 1000); Text(file.ContentType, 200);
            Require(file.Base64 is not null && file.Base64.Length <= 14 * 1024 * 1024, "单个文件不能超过 10 MB。");
            total += file.Base64!.Length;
        }
        Require(total <= 28 * 1024 * 1024, "文件总大小不能超过约 20 MB。");
    }
    public static void Execute(ExecuteInput input)
    {
        Request(input.Request); Auth(input.EffectiveAuth, false);
        Require(input.Variables is not null && input.Variables.Count <= 1000, "变量配置无效。");
        foreach (var pair in input.Variables!) { Text(pair.Key, 1000); Text(pair.Value); }
    }
    public static void Workspace(Workspace workspace) => Entities(workspace.Collections, workspace.Environments);
    public static void Changes(WorkspaceChanges changes)
    {
        Entities(changes.Collections, changes.Environments);
        Require(changes.DeletedCollections is not null && changes.DeletedEnvironments is not null, "缺少删除列表。");
        Require(changes.DeletedCollections!.Count <= 500 && changes.DeletedEnvironments!.Count <= 100, "删除项超过限制。");
        foreach (var id in changes.DeletedCollections.Concat(changes.DeletedEnvironments!)) Id(id);
    }
    private static void Entities(List<CollectionItem>? collections, List<EnvironmentItem>? environments)
    {
        Require(collections is not null && collections.Count <= 500 && environments is not null && environments.Count <= 100, "工作区结构无效或超过限制。");
        Require(collections!.Select(x => x?.Id).Distinct().Count() == collections.Count && environments!.Select(x => x?.Id).Distinct().Count() == environments.Count, "存在重复 ID。");
        foreach (var c in collections)
        {
            Require(c is not null, "集合不能为空。"); Id(c!.Id); Text(c.Name, 1000); Auth(c.Auth, false);
            Require(c.Folders is not null && c.Requests is not null && c.Folders.Count <= 1000 && c.Requests.Count <= 10000, "集合内容缺失或超过限制。");
            Require(c.Folders!.Select(x => x?.Id).Distinct().Count() == c.Folders.Count && c.Requests!.Select(x => x?.Id).Distinct().Count() == c.Requests.Count, "集合存在重复 ID。");
            foreach (var f in c.Folders)
            {
                Require(f is not null, "文件夹不能为空。"); Id(f!.Id); Text(f.Name, 1000); Auth(f.Auth);
                var visited = new HashSet<string> { f.Id }; var parent = f.ParentId;
                while (!string.IsNullOrEmpty(parent))
                {
                    Require(visited.Add(parent), "文件夹层级存在循环。");
                    Require(visited.Count <= 30, "文件夹层级超过 30 层。");
                    var ancestor = c.Folders.Find(x => x.Id == parent); Require(ancestor is not null, "上级文件夹不存在。"); parent = ancestor!.ParentId;
                }
            }
            foreach (var r in c.Requests!) { Request(r); Require(string.IsNullOrEmpty(r.FolderId) || c.Folders.Any(f => f.Id == r.FolderId), "请求所在文件夹不存在。"); }
        }
        foreach (var e in environments!) { Require(e is not null, "环境不能为空。"); Id(e!.Id); Text(e.Name, 1000); Rows(e.Variables); }
    }
}
