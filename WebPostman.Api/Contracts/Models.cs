namespace WebPostman.Api;

public sealed record KeyValueRow(string Id, string Key, string Value, bool Enabled = true);
public sealed record AuthSettings(string Type = "none", string Username = "", string Password = "", string Token = "", string Key = "", string Value = "", string In = "header");
public sealed record RequestItem(
    string Id, string Name, string Method, string Url,
    List<KeyValueRow> Params, List<KeyValueRow> Headers,
    string BodyType, string Body, List<KeyValueRow> Form,
    AuthSettings Auth, string FolderId = "", List<UploadFile>? Files = null, List<KeyValueRow>? Cookies = null,
    bool AllowInsecureSsl = false, int TimeoutSeconds = 60, bool FollowRedirects = true, bool ShowSensitiveTrace = false, List<FileReference>? FileFields = null);
public sealed record FileReference(string Key, string FileName);
public sealed record UploadFile(string Key, string FileName, string ContentType, string Base64);
public sealed record FolderItem(string Id, string Name, AuthSettings Auth, string ParentId = "");
public sealed record CollectionItem(string Id, string Name, AuthSettings Auth, List<FolderItem> Folders, List<RequestItem> Requests);
public sealed record EnvironmentItem(string Id, string Name, List<KeyValueRow> Variables);
public sealed record HistoryEntry(string Id, DateTimeOffset At, string Method, string Url, int Status, long DurationMs,
    RequestItem? Request = null, AuthSettings? EffectiveAuth = null, Dictionary<string, string>? Variables = null,
    string CollectionId = "");
public sealed record Workspace(List<CollectionItem> Collections, List<EnvironmentItem> Environments, List<HistoryEntry> History, long Revision = 0);
public sealed record WorkspaceChanges(long Revision, List<CollectionItem> Collections, List<string> DeletedCollections,
    List<EnvironmentItem> Environments, List<string> DeletedEnvironments);
public sealed record HistoryPage(List<HistoryEntry> Items, int Total);
public sealed record ExecuteInput(RequestItem Request, Dictionary<string, string> Variables, AuthSettings EffectiveAuth, string CollectionId = "");
public sealed record CookieInfo(string Name, string Value, string Attributes, string Raw);
public sealed record TraceStage(string Name, long DurationMs, string Detail);
public sealed record RequestTrace(string Url, string Method, string HttpVersion, Dictionary<string, string> RequestHeaders,
    string RequestBody, List<TraceStage> Stages, string OriginalUrl = "");
public sealed record ExecuteResult(int Status, string StatusText, long DurationMs, long Size, string ContentType,
    Dictionary<string, string> Headers, string Body, bool Truncated,
    List<CookieInfo> Cookies, RequestTrace Trace, string? BodyBase64 = null, string FileName = "response.bin", HistoryEntry? History = null,
    string? RecordedBodyBase64 = null);
public sealed record RegisterInput(string Email, string Password, string ConfirmPassword);
public sealed record LoginInput(string Email, string Password);
public sealed record UserInfo(string Id, string Email);
public sealed record ShareInput(string Kind, string CollectionId, string? RequestId, string Title, string Description = "", long Revision = -1);
public sealed record SharedItem(string Id, string OwnerId, string Author, string Kind, string Title, string Description, DateTimeOffset CreatedAt, CollectionItem Collection);
public sealed record ShareSummary(string Id, string OwnerId, string Author, string Kind, string Title, string Description, DateTimeOffset CreatedAt, int RequestCount);
public sealed record SharePage(List<ShareSummary> Items, int Total);
