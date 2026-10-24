using System.Security.Claims;

namespace WebPostman.Api.Middleware;

public sealed class AccountContextMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var expected = context.Request.Headers["X-WebPostman-User"].ToString();
        var actual = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "guest";
        if (!string.IsNullOrEmpty(expected) && expected != actual && !context.Request.Path.StartsWithSegments("/api/auth"))
        { context.Response.StatusCode = 409; await context.Response.WriteAsJsonAsync(new { error = "其他页面已切换账户，请先导出当前草稿，再重新载入。", code = "account_changed" }); return; }
        await next(context);

    }
}
