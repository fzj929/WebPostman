using System.Text.Json;
using WebPostman.Api.Data;
using WebPostman.Api.Middleware;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
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
builder.Services.AddControllers().AddJsonOptions(options => options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
builder.Services.Configure<ApiBehaviorOptions>(options =>
{
    options.InvalidModelStateResponseFactory = _ => new BadRequestObjectResult(new { error = "请求参数缺失或格式无效。" });
});
builder.Services.AddSingleton<DatabaseSettings>();
builder.Services.AddDbContextFactory<WebPostmanDbContext>((services, options) => services.GetRequiredService<DatabaseSettings>().Configure(options));
builder.Services.AddSingleton<LegacyDataImporter>();
builder.Services.AddSingleton<AccountStore>();
builder.Services.AddScoped<RequestExecutionService>();
builder.Services.AddSingleton<ShareStore>();
builder.Services.AddSingleton<MockRuntime>();
builder.Services.AddHostedService(services => services.GetRequiredService<MockRuntime>());
builder.Services.AddSingleton<MockServerService>();
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
// Apply versioned schema changes before accepting requests.
using (var scope = app.Services.CreateScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<WebPostmanDbContext>>();
    using var db = factory.CreateDbContext();
    db.Database.Migrate();
    scope.ServiceProvider.GetRequiredService<LegacyDataImporter>().ImportAccounts();
}
app.UseForwardedHeaders(new Microsoft.AspNetCore.Builder.ForwardedHeadersOptions
{
    ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto
});
app.UseMiddleware<ApiExceptionMiddleware>();
app.UseMiddleware<SameOriginMiddleware>();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseMiddleware<AccountContextMiddleware>();
app.UseAuthorization();
app.UseRateLimiter();

app.MapControllers();
app.MapFallbackToFile("index.html");
app.Run();
