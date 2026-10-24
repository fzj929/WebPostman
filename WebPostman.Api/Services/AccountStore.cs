using System.Net.Mail;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using WebPostman.Api.Data;
using Microsoft.Extensions.Options;

namespace WebPostman.Api;

public sealed class AccountConflictException : Exception;
public sealed class LoginFailedException : Exception;

public sealed class AccountStore
{
    public const string CookieName = "webpostman-session";
    private readonly IDbContextFactory<WebPostmanDbContext> _factory;
    private readonly LegacyDataImporter _legacy;
    private readonly PasswordHasher<string> _hasher = new(Options.Create(new PasswordHasherOptions { IterationCount = 600000 }));
    private readonly string _dummyHash;

    public AccountStore(IDbContextFactory<WebPostmanDbContext> factory, LegacyDataImporter legacy)
    {
        _factory = factory; _legacy = legacy;
        _dummyHash = _hasher.HashPassword("dummy", Guid.NewGuid().ToString());
    }
    private WebPostmanDbContext Open() => _factory.CreateDbContext();
    public WorkspaceStore Workspace(string userId)
    {
        if (!Guid.TryParseExact(userId, "N", out _)) throw new ArgumentException("用户 ID 无效。");
        _legacy.EnsureWorkspace(userId);
        return new WorkspaceStore(_factory, userId);
    }
    private static string Email(string? value)
    {
        var email = value?.Trim().ToLowerInvariant() ?? "";
        if (email.Length > 254 || !MailAddress.TryCreate(email, out var address) || address.Address != email || !email.Contains('@')) throw new ArgumentException("请输入有效的邮箱地址。");
        return email;
    }
    private static void Password(string? password)
    { if (password is null || password.Length < 8 || password.Length > 128) throw new ArgumentException("密码长度应为 8～128 个字符。"); }
    public UserInfo Register(RegisterInput input)
    {
        var email = Email(input.Email); Password(input.Password);
        if (input.Password != input.ConfirmPassword) throw new ArgumentException("两次输入的密码不一致。");
        var user = new UserInfo(Guid.NewGuid().ToString("N"), email);
        var hash = _hasher.HashPassword(email, input.Password);
        using var db = Open();
        db.Users.Add(new() { Id = user.Id, Email = email, PasswordHash = hash });
        try { db.SaveChanges(); }
        catch (DbUpdateException) when (db.Users.AsNoTracking().Any(x => x.Email == email)) { throw new AccountConflictException(); }
        return user;
    }
    public UserInfo Login(LoginInput input)
    {
        var email = Email(input.Email); Password(input.Password);
        using var db = Open();
        var user = db.Users.AsNoTracking().SingleOrDefault(x => x.Email == email);
        var verified = _hasher.VerifyHashedPassword(email, user?.PasswordHash ?? _dummyHash, input.Password);
        if (user is null || verified == PasswordVerificationResult.Failed) throw new LoginFailedException();
        if (verified == PasswordVerificationResult.SuccessRehashNeeded)
            db.Users.Where(x => x.Id == user.Id).ExecuteUpdate(x => x.SetProperty(u => u.PasswordHash, _hasher.HashPassword(email, input.Password)));
        return new(user.Id, email);
    }
    private static string TokenHash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    public void StartSession(HttpContext context, UserInfo user)
    {
        RevokeSession(context);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var expires = DateTimeOffset.UtcNow.AddDays(7);
        using var db = Open();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        db.Sessions.Where(x => x.Expires <= now).ExecuteDelete();
        db.Sessions.Add(new() { TokenHash = TokenHash(token), UserId = user.Id, Expires = expires.ToUnixTimeSeconds() });
        db.SaveChanges();
        context.Response.Cookies.Append(CookieName, token, new CookieOptions { HttpOnly = true, Secure = context.Request.IsHttps, SameSite = SameSiteMode.Strict, Path = "/api", Expires = expires, IsEssential = true });
    }
    public void RevokeSession(HttpContext context)
    {
        if (context.Request.Cookies.TryGetValue(CookieName, out var token))
        { using var db = Open(); var hash = TokenHash(token); db.Sessions.Where(x => x.TokenHash == hash).ExecuteDelete(); }
        context.Response.Cookies.Delete(CookieName, new CookieOptions { Path = "/api", HttpOnly = true, Secure = context.Request.IsHttps, SameSite = SameSiteMode.Strict });
    }
    public UserInfo? Session(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length != 64) return null;
        using var db = Open(); var hash = TokenHash(token); var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return (from session in db.Sessions.AsNoTracking()
                join user in db.Users.AsNoTracking() on session.UserId equals user.Id
                where session.TokenHash == hash && session.Expires > now
                select new UserInfo(user.Id, user.Email)).SingleOrDefault();
    }
    public static string UserId(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new UnauthorizedAccessException();
}
