using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;

namespace WebPostman.Api;

/// <summary>Independent HTTP listeners: no management endpoints, cookies, or real upstream forwarding.</summary>
public sealed class MockRuntime(IConfiguration configuration, ILogger<MockRuntime> logger) : IHostedService
{
    public SemaphoreSlim Gate { get; } = new(1, 1);
    private readonly ConcurrentDictionary<string, Listener> _listeners = new();

    private sealed class Listener(MockServer snapshot)
    {
        public MockServer Snapshot = snapshot;
        public WebApplication? App;
        public long Calls;
        public ConcurrentQueue<MockCall> Logs { get; } = new();
        public SemaphoreSlim Requests { get; } = new(64, 64);
    }
    private sealed class ListenerLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
    public bool Running(string id) => _listeners.ContainsKey(id);
    public long Calls(string id) => _listeners.TryGetValue(id, out var listener) ? Interlocked.Read(ref listener.Calls) : 0;
    public List<MockCall> Logs(string id) => _listeners.TryGetValue(id, out var listener) ? listener.Logs.Reverse().ToList() : [];
    public void Refresh(MockServer snapshot)
    { if (_listeners.TryGetValue(snapshot.Id, out var listener)) Volatile.Write(ref listener.Snapshot, snapshot); }

    // Called under Gate so start/stop/configuration changes cannot race.
    public async Task StartListenerAsync(MockServer snapshot)
    {
        if (Running(snapshot.Id)) return;
        if (_listeners.Count >= Math.Clamp(configuration.GetValue("Mock:MaxRunningServers", 16), 1, 100)) throw new MockConflictException("同时运行的模拟服务已达到上限。");
        if (_listeners.Values.Any(x => x.Snapshot.Port == snapshot.Port)) throw new MockConflictException("端口已被其他模拟服务占用，请选择其他端口。");
        var listener = new Listener(snapshot);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ApplicationName = typeof(MockRuntime).Assembly.FullName });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IHostLifetime, ListenerLifetime>();
        builder.Services.Configure<SocketTransportOptions>(options => options.CreateBoundListenSocket = endpoint =>
        {
            // Windows otherwise permits a wildcard listener to overlap an existing
            // loopback listener, making a reported "running" service unreachable.
            var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                if (endpoint.AddressFamily == AddressFamily.InterNetworkV6) socket.DualMode = true;
                if (OperatingSystem.IsWindows()) socket.ExclusiveAddressUse = true;
                socket.Bind(endpoint); return socket;
            }
            catch { socket.Dispose(); throw; }
        });
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = MockValidation.MaxBodyBytes;
            options.ListenAnyIP(snapshot.Port);
        });
        var app = builder.Build(); listener.App = app;
        app.Run(context => ReplayAsync(context, listener));
        try
        {
            await app.StartAsync();
            _listeners[snapshot.Id] = listener;
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or InvalidOperationException)
        {
            await app.DisposeAsync();
            logger.LogWarning(ex, "Cannot start mock server on port {Port}", snapshot.Port);
            throw new MockConflictException("启动失败：端口被占用或不允许监听，请更换端口并重试。");
        }
    }
    public async Task StopListenerAsync(string id)
    {
        if (!_listeners.TryRemove(id, out var listener)) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await listener.App!.StopAsync(timeout.Token); }
        finally { await listener.App!.DisposeAsync(); }
    }
    private static async Task ReplayAsync(HttpContext context, Listener listener)
    {
        var clock = Stopwatch.StartNew();
        var acquired = await listener.Requests.WaitAsync(0, context.RequestAborted);
        try
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!acquired) { context.Response.StatusCode = 429; return; }
            var snapshot = Volatile.Read(ref listener.Snapshot);
            var origin = context.Request.Headers.Origin.ToString();
            if (origin.Length > 0 && (snapshot.CorsOrigins.Contains("*") || snapshot.CorsOrigins.Contains(origin, StringComparer.Ordinal)))
            {
                context.Response.Headers.AccessControlAllowOrigin = snapshot.CorsOrigins.Contains("*") ? "*" : origin;
                context.Response.Headers.Vary = "Origin";
                context.Response.Headers.AccessControlExposeHeaders = "*";
                if (HttpMethods.IsOptions(context.Request.Method) && context.Request.Headers.ContainsKey("Access-Control-Request-Method"))
                {
                    context.Response.Headers.AccessControlAllowMethods = "GET, POST, PUT, PATCH, DELETE, HEAD, OPTIONS";
                    context.Response.Headers.AccessControlAllowHeaders = context.Request.Headers.AccessControlRequestHeaders;
                    context.Response.StatusCode = 204; return;
                }
            }
            var method = context.Request.Method;
            var query = MockValidation.Query(context.Request.QueryString.Value ?? "");
            var rules = (snapshot.Rules ?? []).Where(x => x.Enabled && x.Path == context.Request.Path.Value && (x.Query == "" || x.Query == query));
            var rule = rules.Where(x => x.Method == method).OrderByDescending(x => x.Query.Length > 0).FirstOrDefault();
            if (rule is null && HttpMethods.IsHead(method)) rule = rules.Where(x => x.Method == "GET").OrderByDescending(x => x.Query.Length > 0).FirstOrDefault();
            if (rule is null)
            {
                context.Response.StatusCode = 404;
                if (!HttpMethods.IsHead(method)) await context.Response.WriteAsJsonAsync(new { error = "没有匹配的模拟接口。" }, context.RequestAborted);
                return;
            }
            var response = rule.Response;
            if (response.DelayMs > 0) await Task.Delay(response.DelayMs, context.RequestAborted);
            context.Response.StatusCode = response.Status;
            context.Response.ContentType = response.ContentType;
            foreach (var header in response.Headers.Where(x => x.Enabled)) context.Response.Headers.Append(header.Key, header.Value);
            var body = response.BodyBase64 is not null ? Convert.FromBase64String(response.BodyBase64) : Encoding.UTF8.GetBytes(response.Body);
            if (response.Status is 204 or 205 or 304) return;
            context.Response.ContentLength = body.Length;
            if (!HttpMethods.IsHead(method)) await context.Response.Body.WriteAsync(body, context.RequestAborted);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (ArgumentException)
        { if (!context.Response.HasStarted) context.Response.StatusCode = 400; }
        finally
        {
            if (acquired) listener.Requests.Release();
            Interlocked.Increment(ref listener.Calls);
            // Query strings, headers and bodies are deliberately absent from call metadata.
            listener.Logs.Enqueue(new(DateTimeOffset.UtcNow, context.Request.Method, context.Request.Path.Value ?? "/", context.Response.StatusCode, clock.ElapsedMilliseconds));
            while (listener.Logs.Count > 50) listener.Logs.TryDequeue(out _);
        }
    }
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(CancellationToken.None);
        try { await Task.WhenAll(_listeners.Keys.Select(StopListenerAsync)); }
        finally { Gate.Release(); }
    }
}
