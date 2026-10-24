namespace WebPostman.Api.Middleware;

public sealed class SameOriginMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        if (request.Path.StartsWithSegments("/api") && request.Method is not ("GET" or "HEAD" or "OPTIONS") && request.Headers.Origin.Count > 0)
        {
            if (!Uri.TryCreate(request.Headers.Origin.ToString(), UriKind.Absolute, out var origin) || origin.Scheme != request.Scheme || origin.Authority != request.Host.Value)
            { context.Response.StatusCode = 403; await context.Response.WriteAsJsonAsync(new { error = "不允许跨站操作。" }); return; }
        }
        await next(context);

    }
}
