using System.Collections.Concurrent;
using System.Net.Mail;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace WebPostman.Api;

public sealed class AccountConflictException : Exception;
public sealed class LoginFailedException : Exception;

public sealed class AccountStore
{
    public const string CookieName = "webpostman-session";
    private readonly string _connectionString;
    public string DirectoryPath { get; }
    private readonly PasswordHasher<string> _hasher = new(Options.Create(new PasswordHasherOptions { IterationCount = 600000 }));
    private readonly string _dummyHash;
    private readonly ConcurrentDictionary<string, WorkspaceStore> _workspaces = new();

    public AccountStore(IWebHostEnvironment env, IConfiguration config)
    {
        DirectoryPath = config["WebPostman:DataDirectory"] ?? Path.Combine(env.ContentRootPath, "App_Data");
        Directory.CreateDirectory(DirectoryPath);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(DirectoryPath, "accounts.db") }.ToString();
        using var db = Open(); using var c = db.CreateCommand();
        c.CommandText = "CREATE TABLE IF NOT EXISTS users(id TEXT PRIMARY KEY,email TEXT NOT NULL UNIQUE,password_hash TEXT NOT NULL); CREATE TABLE IF NOT EXISTS sessions(token_hash TEXT PRIMARY KEY,user_id TEXT NOT NULL,expires INTEGER NOT NULL); CREATE INDEX IF NOT EXISTS sessions_expiry ON sessions(expires); CREATE TABLE IF NOT EXISTS shares(id TEXT PRIMARY KEY,owner_id TEXT NOT NULL,kind TEXT NOT NULL,title TEXT NOT NULL,description TEXT NOT NULL,created_at TEXT NOT NULL,json TEXT NOT NULL); CREATE INDEX IF NOT EXISTS shares_created ON shares(created_at DESC,id DESC);";
        c.ExecuteNonQuery(); _dummyHash = _hasher.HashPassword("dummy", Guid.NewGuid().ToString());
    }
    public SqliteConnection Open() { var db = new SqliteConnection(_connectionString); db.Open(); return db; }
    public static SqliteCommand Command(SqliteConnection db, string sql, params (string, object)[] parameters)
    {
        var c = db.CreateCommand(); c.CommandText = sql;
        foreach (var (key, value) in parameters) c.Parameters.AddWithValue(key, value);
        return c;
    }
    public WorkspaceStore Workspace(string userId)
    {
        if (!Guid.TryParseExact(userId, "N", out _)) throw new ArgumentException("用户 ID 无效。");
        return _workspaces.GetOrAdd(userId, id => new WorkspaceStore(Path.Combine(DirectoryPath, "users", id)));
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
        using var db = Open(); using var c = Command(db, "INSERT INTO users VALUES($id,$email,$hash)", ("$id", user.Id), ("$email", email), ("$hash", hash));
        try { c.ExecuteNonQuery(); } catch (SqliteException ex) when (ex.SqliteErrorCode == 19) { throw new AccountConflictException(); }
        return user;
    }
    public UserInfo Login(LoginInput input)
    {
        var email = Email(input.Email); Password(input.Password);
        using var db = Open();
        string? id = null, hash = null;
        using (var c = Command(db, "SELECT id,password_hash FROM users WHERE email=$email", ("$email", email)))
        using (var r = c.ExecuteReader()) if (r.Read()) { id = r.GetString(0); hash = r.GetString(1); }
        var verified = _hasher.VerifyHashedPassword(email, hash ?? _dummyHash, input.Password);
        if (id is null || verified == PasswordVerificationResult.Failed) throw new LoginFailedException();
        return new(id, email);
    }
    private static string TokenHash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    public void StartSession(HttpContext context, UserInfo user)
    {
        RevokeSession(context);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var expires = DateTimeOffset.UtcNow.AddDays(7);
        using var db = Open();
        using (var cleanup = Command(db, "DELETE FROM sessions WHERE expires <= $now", ("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds()))) cleanup.ExecuteNonQuery();
        using (var c = Command(db, "INSERT INTO sessions VALUES($hash,$id,$expires)", ("$hash", TokenHash(token)), ("$id", user.Id), ("$expires", expires.ToUnixTimeSeconds()))) c.ExecuteNonQuery();
        context.Response.Cookies.Append(CookieName, token, new CookieOptions { HttpOnly = true, Secure = context.Request.IsHttps, SameSite = SameSiteMode.Strict, Path = "/api", Expires = expires, IsEssential = true });
    }
    public void RevokeSession(HttpContext context)
    {
        if (context.Request.Cookies.TryGetValue(CookieName, out var token)) { using var db = Open(); using var c = Command(db, "DELETE FROM sessions WHERE token_hash=$hash", ("$hash", TokenHash(token))); c.ExecuteNonQuery(); }
        context.Response.Cookies.Delete(CookieName, new CookieOptions { Path = "/api", HttpOnly = true, Secure = context.Request.IsHttps, SameSite = SameSiteMode.Strict });
    }
    public UserInfo? Session(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length != 64) return null;
        using var db = Open(); using var c = Command(db, "SELECT users.id,email FROM sessions JOIN users ON user_id=users.id WHERE token_hash=$hash AND expires>$now", ("$hash", TokenHash(token)), ("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        using var r = c.ExecuteReader(); return r.Read() ? new(r.GetString(0), r.GetString(1)) : null;
    }
    public static string UserId(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new UnauthorizedAccessException();
}

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
