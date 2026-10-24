using System.Text.Json;
using WebPostman.Api;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService(options => options.ServiceName = "WebPostman");
builder.Host.UseSystemd();
builder.WebHost.UseUrls(builder.Configuration["WebPostman:ListenUrl"] ?? "http://127.0.0.1:5078");
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
builder.Services.AddSingleton<WorkspaceStore>();
builder.Services.AddHttpClient("requests").ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    UseCookies = false,
    AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate | System.Net.DecompressionMethods.Brotli
});
builder.Services.AddHttpClient("insecure-requests").ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    UseCookies = false,
    AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate | System.Net.DecompressionMethods.Brotli,
    ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
});
builder.Services.AddSingleton<RequestExecutor>();

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/workspace", async (WorkspaceStore store) => await store.ReadAsync());
app.MapPut("/api/workspace", async (Workspace workspace, WorkspaceStore store) =>
{
    if (workspace.Collections.Count > 500 || workspace.Environments.Count > 100)
        return Results.BadRequest(new { error = "工作区数据超过限制。" });
    await store.WriteAsync(workspace);
    return Results.Ok(workspace);
});
app.MapPost("/api/execute", async (ExecuteInput input, RequestExecutor executor, WorkspaceStore store, CancellationToken cancellationToken) =>
{
    try
    {
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
