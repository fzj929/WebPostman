namespace WebPostman.Api;

public sealed record KeyValueRow(string Id, string Key, string Value, bool Enabled = true);
public sealed record AuthSettings(string Type = "none", string Username = "", string Password = "", string Token = "", string Key = "", string Value = "", string In = "header");
public sealed record RequestItem(
    string Id, string Name, string Method, string Url,
    List<KeyValueRow> Params, List<KeyValueRow> Headers,
    string BodyType, string Body, List<KeyValueRow> Form,
    AuthSettings Auth, string FolderId = "", List<UploadFile>? Files = null, List<KeyValueRow>? Cookies = null,
    bool AllowInsecureSsl = false);
public sealed record UploadFile(string Key, string FileName, string ContentType, string Base64);
public sealed record FolderItem(string Id, string Name, AuthSettings Auth);
public sealed record CollectionItem(string Id, string Name, AuthSettings Auth, List<FolderItem> Folders, List<RequestItem> Requests);
public sealed record EnvironmentItem(string Id, string Name, List<KeyValueRow> Variables);
public sealed record HistoryEntry(string Id, DateTimeOffset At, string Method, string Url, int Status, long DurationMs,
    RequestItem? Request = null, AuthSettings? EffectiveAuth = null, Dictionary<string, string>? Variables = null,
    string CollectionId = "");
public sealed record Workspace(List<CollectionItem> Collections, List<EnvironmentItem> Environments, List<HistoryEntry> History);
public sealed record ExecuteInput(RequestItem Request, Dictionary<string, string> Variables, AuthSettings EffectiveAuth, string CollectionId = "");
public sealed record CookieInfo(string Name, string Value, string Attributes, string Raw);
public sealed record TraceStage(string Name, long DurationMs, string Detail);
public sealed record RequestTrace(string Url, string Method, string HttpVersion, Dictionary<string, string> RequestHeaders,
    string RequestBody, List<TraceStage> Stages);
public sealed record ExecuteResult(int Status, string StatusText, long DurationMs, long Size, string ContentType,
    Dictionary<string, string> Headers, string Body, bool Truncated,
    List<CookieInfo> Cookies, RequestTrace Trace);
