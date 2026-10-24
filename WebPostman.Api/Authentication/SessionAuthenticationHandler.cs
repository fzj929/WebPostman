using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace WebPostman.Api;

public sealed class SessionAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, AccountStore accounts)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var user = accounts.Session(Request.Cookies[AccountStore.CookieName]);
        if (user is null) return Task.FromResult(AuthenticateResult.NoResult());
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, user.Id), new(ClaimTypes.Email, user.Email)], Scheme.Name));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    { Response.StatusCode = 401; return Response.WriteAsJsonAsync(new { error = "请先登录。" }); }
    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    { Response.StatusCode = 403; return Response.WriteAsJsonAsync(new { error = "无权操作此内容。" }); }
}
