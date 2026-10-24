using System.Text.Json;
using WebPostman.Api;
using Microsoft.AspNetCore.Authentication;
using System.Security.Claims;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService(options => options.ServiceName = "WebPostman");
builder.Host.UseSystemd();
builder.WebHost.UseUrls(builder.Configuration["WebPostman:ListenUrl"] ?? "http://127.0.0.1:5078");
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 40 * 1024 * 1024);
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
builder.Services.AddSingleton<AccountStore>();
builder.Services.AddSingleton<ShareStore>();
builder.Services.AddAuthentication("session").AddScheme<AuthenticationSchemeOptions, SessionAuthenticationHandler>("session", null);
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.OnRejected = async (context, token) => await context.HttpContext.Response.WriteAsJsonAsync(new { error = "操作过于频繁或并发请求过多，请稍后重试。" }, token);
    options.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "local", _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.AddPolicy("execute", context => RateLimitPartition.GetConcurrencyLimiter(context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.Connection.RemoteIpAddress?.ToString() ?? "local", _ => new ConcurrencyLimiterOptions { PermitLimit = 4, QueueLimit = 0 }));
});
builder.Services.AddHttpClient("requests", client => client.Timeout = Timeout.InfiniteTimeSpan).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    UseCookies = false,
    AllowAutoRedirect = false,
    AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate | System.Net.DecompressionMethods.Brotli
});
builder.Services.AddHttpClient("insecure-requests", client => client.Timeout = Timeout.InfiniteTimeSpan).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    UseCookies = false,
    AllowAutoRedirect = false,
    AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate | System.Net.DecompressionMethods.Brotli,
    ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
});
builder.Services.AddSingleton<RequestExecutor>();

var app = builder.Build();
app.UseForwardedHeaders(new Microsoft.AspNetCore.Builder.ForwardedHeadersOptions
{
    ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto
});
app.Use(async (context, next) =>
{
    try { await next(context); }
    catch (RevisionConflictException)
    { context.Response.StatusCode = 409; await context.Response.WriteAsJsonAsync(new { error = "工作区已被其他页面修改。请导出未保存数据，再重新载入最新工作区。" }); }
    catch (ArgumentException ex)
    { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = ex.Message }); }
    catch (AccountConflictException)
    { context.Response.StatusCode = 409; await context.Response.WriteAsJsonAsync(new { error = "该邮箱已注册，请直接登录。" }); }
    catch (LoginFailedException)
    { context.Response.StatusCode = 401; await context.Response.WriteAsJsonAsync(new { error = "邮箱或密码错误。" }); }
    catch (UnauthorizedAccessException)
    { context.Response.StatusCode = 401; await context.Response.WriteAsJsonAsync(new { error = "登录已失效，请重新登录。" }); }
    catch (KeyNotFoundException)
    { context.Response.StatusCode = 404; await context.Response.WriteAsJsonAsync(new { error = "内容不存在或无权操作。" }); }
});
app.Use(async (context, next) =>
{
    var request = context.Request;
    if (request.Path.StartsWithSegments("/api") && request.Method is not ("GET" or "HEAD" or "OPTIONS") && request.Headers.Origin.Count > 0)
    {
        if (!Uri.TryCreate(request.Headers.Origin.ToString(), UriKind.Absolute, out var origin) || origin.Scheme != request.Scheme || origin.Authority != request.Host.Value)
        { context.Response.StatusCode = 403; await context.Response.WriteAsJsonAsync(new { error = "不允许跨站操作。" }); return; }
    }
    await next(context);
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.Use(async (context, next) =>
{
    var expected = context.Request.Headers["X-WebPostman-User"].ToString();
    var actual = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "guest";
    if (!string.IsNullOrEmpty(expected) && expected != actual && !context.Request.Path.StartsWithSegments("/api/auth"))
    { context.Response.StatusCode = 409; await context.Response.WriteAsJsonAsync(new { error = "其他页面已切换账户，请先导出当前草稿，再重新载入。", code = "account_changed" }); return; }
    await next(context);
});
app.UseAuthorization();
app.UseRateLimiter();

app.MapGet("/api/auth/me", (HttpContext context) => Results.Ok(new { user = context.User.Identity?.IsAuthenticated == true ? new UserInfo(AccountStore.UserId(context.User), context.User.FindFirstValue(ClaimTypes.Email)!) : null }));
app.MapPost("/api/auth/register", (RegisterInput input, HttpContext context, AccountStore accounts) =>
{ var user = accounts.Register(input); accounts.StartSession(context, user); return Results.Ok(new { user }); }).RequireRateLimiting("auth");
app.MapPost("/api/auth/login", (LoginInput input, HttpContext context, AccountStore accounts) =>
{ var user = accounts.Login(input); accounts.StartSession(context, user); return Results.Ok(new { user }); }).RequireRateLimiting("auth");
app.MapPost("/api/auth/logout", (HttpContext context, AccountStore accounts) =>
{ accounts.RevokeSession(context); return Results.Ok(); });

app.MapGet("/api/workspace", async (HttpContext context, AccountStore accounts) => await accounts.Workspace(AccountStore.UserId(context.User)).ReadAsync()).RequireAuthorization();
app.MapGet("/api/history", async (int? offset, int? limit, HttpContext context, AccountStore accounts) =>
    await accounts.Workspace(AccountStore.UserId(context.User)).ReadHistoryAsync(Math.Max(0, offset ?? 0), Math.Clamp(limit ?? 50, 1, 100))).RequireAuthorization();
app.MapPut("/api/workspace", async (Workspace workspace, HttpContext context, AccountStore accounts) =>
{
    InputValidation.Workspace(workspace);
    return Results.Ok(new { revision = await accounts.Workspace(AccountStore.UserId(context.User)).WriteAsync(workspace) });
}).RequireAuthorization();
app.MapPatch("/api/workspace", async (WorkspaceChanges changes, HttpContext context, AccountStore accounts) =>
{
    InputValidation.Changes(changes);
    return Results.Ok(new { revision = await accounts.Workspace(AccountStore.UserId(context.User)).PatchAsync(changes) });
}).RequireAuthorization();
app.MapGet("/api/shares", (string? query, string? kind, bool? mine, int? offset, int? limit, HttpContext context, ShareStore shares) => shares.List(query?.Length > 200 ? query[..200] : query, kind, mine == true ? AccountStore.UserId(context.User) : null, Math.Max(offset ?? 0, 0), Math.Clamp(limit ?? 30, 1, 50)));
app.MapGet("/api/shares/{id}", (string id, ShareStore shares) => shares.Get(id));
app.MapPost("/api/shares/preview", async (ShareInput input, HttpContext context, ShareStore shares) =>
{ var preview = await shares.PreviewAsync(AccountStore.UserId(context.User), input); return Results.Ok(new { collection = preview.Collection, revision = preview.Revision }); }).RequireAuthorization();
app.MapPost("/api/shares", async (ShareInput input, HttpContext context, ShareStore shares) => await shares.PublishAsync(AccountStore.UserId(context.User), input)).RequireAuthorization();
app.MapDelete("/api/shares/{id}", (string id, HttpContext context, ShareStore shares) =>
{ shares.Delete(id, AccountStore.UserId(context.User)); return Results.NoContent(); }).RequireAuthorization();
app.MapPost("/api/execute", async (ExecuteInput input, RequestExecutor executor, AccountStore accounts, HttpContext context, CancellationToken cancellationToken) =>
{
    try
    {
        InputValidation.Execute(input);
        var result = await executor.ExecuteAsync(input, cancellationToken);
        var snapshot = input.Request with { Auth = input.EffectiveAuth, Files = null };
        var history = new HistoryEntry(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow,
            input.Request.Method, RequestExecutor.SafeDisplayUrl(input.Request.Url), result.Status, result.DurationMs,
            snapshot, input.EffectiveAuth, input.Variables, input.CollectionId);
        if (context.User.Identity?.IsAuthenticated == true) await accounts.Workspace(AccountStore.UserId(context.User)).AddHistoryAsync(history);
        return Results.Ok(result with { History = history });
    }
    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
        return Results.BadRequest(new { error = "请求超时。" });
    }
    catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or UriFormatException or ArgumentException or FormatException)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).RequireRateLimiting("execute");
app.MapFallbackToFile("index.html");
app.Run();
