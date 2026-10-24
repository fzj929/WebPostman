namespace WebPostman.Api.Middleware;

public sealed class ApiExceptionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
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

    }
}
