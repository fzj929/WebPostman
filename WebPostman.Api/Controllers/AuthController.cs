using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace WebPostman.Api.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(AccountStore accounts) : ControllerBase
{
    [HttpGet("me")]
    public IActionResult Me() => Ok(new { user = User.Identity?.IsAuthenticated == true ? new UserInfo(AccountStore.UserId(User), User.FindFirstValue(ClaimTypes.Email)!) : null });

    [HttpPost("register"), EnableRateLimiting("auth")]
    public IActionResult Register(RegisterInput input)
    { var user = accounts.Register(input); accounts.StartSession(HttpContext, user); return Ok(new { user }); }

    [HttpPost("login"), EnableRateLimiting("auth")]
    public IActionResult Login(LoginInput input)
    { var user = accounts.Login(input); accounts.StartSession(HttpContext, user); return Ok(new { user }); }

    [HttpPost("logout")]
    public IActionResult Logout() { accounts.RevokeSession(HttpContext); return Ok(); }
}
