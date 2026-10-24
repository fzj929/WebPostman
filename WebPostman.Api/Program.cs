using System.Text.Json;
using WebPostman.Api;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService(options => options.ServiceName = "WebPostman");
builder.Host.UseSystemd();
builder.WebHost.UseUrls(builder.Configuration["WebPostman:ListenUrl"] ?? "http://127.0.0.1:5078");
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 40 * 1024 * 1024);
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
builder.Services.AddSingleton<WorkspaceStore>();
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
app.Use(async (context, next) =>
{
    try { await next(context); }
    catch (RevisionConflictException)
    { context.Response.StatusCode = 409; await context.Response.WriteAsJsonAsync(new { error = "工作区已被其他页面修改。请导出未保存数据，再重新载入最新工作区。" }); }
    catch (ArgumentException ex)
    { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = ex.Message }); }
});
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/workspace", async (WorkspaceStore store) => await store.ReadAsync());
app.MapGet("/api/history", async (int? offset, int? limit, WorkspaceStore store) =>
    await store.ReadHistoryAsync(Math.Max(0, offset ?? 0), Math.Clamp(limit ?? 50, 1, 100)));
app.MapPut("/api/workspace", async (Workspace workspace, WorkspaceStore store) =>
{
    InputValidation.Workspace(workspace);
    return Results.Ok(new { revision = await store.WriteAsync(workspace) });
});
app.MapPatch("/api/workspace", async (WorkspaceChanges changes, WorkspaceStore store) =>
{
    InputValidation.Changes(changes);
    return Results.Ok(new { revision = await store.PatchAsync(changes) });
});
app.MapPost("/api/execute", async (ExecuteInput input, RequestExecutor executor, WorkspaceStore store, CancellationToken cancellationToken) =>
{
    try
    {
        InputValidation.Execute(input);
        var result = await executor.ExecuteAsync(input, cancellationToken);
        var snapshot = input.Request with { Auth = input.EffectiveAuth, Files = null };
        await store.AddHistoryAsync(new HistoryEntry(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow,
            input.Request.Method, RequestExecutor.SafeDisplayUrl(input.Request.Url), result.Status, result.DurationMs,
            snapshot, input.EffectiveAuth, input.Variables, input.CollectionId));
        return Results.Ok(result);
    }
    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
        return Results.BadRequest(new { error = "请求超时。" });
    }
    catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or UriFormatException or ArgumentException or FormatException)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});
app.MapFallbackToFile("index.html");
app.Run();
