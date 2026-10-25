using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;

namespace WebPostman.Api;

public static class MockValidation
{
    public const int MaxBodyBytes = 2 * 1024 * 1024;
    private static readonly HashSet<string> RemovedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Length", "Content-Encoding", "Transfer-Encoding", "Connection", "Keep-Alive", "TE", "Trailer", "Upgrade",
        "Proxy-Authenticate", "Proxy-Authorization", "Set-Cookie", "Server", "Date", "Content-Type"
    };
    public static string Name(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 100) throw new ArgumentException("名称为 1～100 个字符。");
        return value.Trim();
    }
    public static List<string> Origins(List<string>? origins)
    {
        if (origins is null || origins.Count > 20) throw new ArgumentException("跨域来源最多 20 个。");
        return origins.Select(value =>
        {
            if (value == "*") return value;
            if (value is null || value.Length > 2048 || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https") || uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0)
                throw new ArgumentException("跨域来源需为 http(s)://主机:端口，或 *；留空表示关闭跨域。");
            return uri.GetLeftPart(UriPartial.Authority);
        }).Distinct(StringComparer.Ordinal).ToList();
    }
    public static string Path(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 || !value.StartsWith('/') || value.Contains('?') || value.Contains('#') || value.Contains('\\') || value.Any(char.IsControl))
            throw new ArgumentException("路径必须以 / 开头，不包含查询字符串、片段或控制字符，最多 2048 字符。");
        var path = PathString.FromUriComponent(value).Value!;
        if (path.Any(char.IsControl) || path.Contains('\\')) throw new ArgumentException("路径包含无效字符。");
        return path;
    }
    public static string Query(string? value)
    {
        if (value is null || value.Length > 8192 || value.Any(char.IsControl) || value.Contains('#')) throw new ArgumentException("查询字符串无效或超过 8192 字符。");
        var query = string.Join("&", QueryHelpers.ParseQuery(value.TrimStart('?')).SelectMany(x => x.Value.Select(v => (Key: x.Key, Value: v ?? "")))
            .OrderBy(x => x.Key, StringComparer.Ordinal).ThenBy(x => x.Value, StringComparer.Ordinal)
            .Select(x => Uri.EscapeDataString(x.Key) + "=" + Uri.EscapeDataString(x.Value)));
        if (query.Length > 8192) throw new ArgumentException("编码后的查询字符串超过 8192 字符。");
        return query;
    }
    public static string Method(string? method) => method?.ToUpperInvariant() is "GET" or "POST" or "PUT" or "PATCH" or "DELETE" or "HEAD" or "OPTIONS"
        ? method.ToUpperInvariant() : throw new ArgumentException("不支持的请求方法。");
    public static string RouteKey(string method, string path, string query) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{method}\n{path}\n{query}")));
    public static MockResponse Response(MockResponse? response)
    {
        if (response is null || response.Status is < 200 or > 599 || response.DelayMs is < 0 or > 30000)
            throw new ArgumentException("状态码须为 200～599，延迟为 0～30000 毫秒。");
        if (response.ContentType is null || response.ContentType.Length > 200 || response.ContentType.Any(char.IsControl)
            || !MediaTypeHeaderValue.TryParse(response.ContentType, out _)) throw new ArgumentException("Content-Type 无效。");
        if (response.Body is null || Encoding.UTF8.GetByteCount(response.Body) > MaxBodyBytes) throw new ArgumentException("响应正文不能超过 2 MB。");
        if (response.Binary && response.BodyBase64 is null) throw new ArgumentException("二进制响应需要 Base64 内容。");
        if (response.BodyBase64 is not null)
        {
            if (response.BodyBase64.Length > 2800000) throw new ArgumentException("响应正文不能超过 2 MB。");
            try { if (Convert.FromBase64String(response.BodyBase64).Length > MaxBodyBytes) throw new ArgumentException("响应正文不能超过 2 MB。"); }
            catch (FormatException) { throw new ArgumentException("响应 Base64 内容无效。"); }
        }
        if (response.Headers is null || response.Headers.Count > 100) throw new ArgumentException("响应头最多 100 项。");
        var connectionHeaders = response.Headers.Where(x => x is not null && x.Key?.Equals("Connection", StringComparison.OrdinalIgnoreCase) == true)
            .SelectMany(x => (x.Value ?? "").Split(',')).Select(x => x.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var headers = new List<KeyValueRow>();
        foreach (var header in response.Headers)
        {
            if (header is null || header.Key is null || header.Value is null || header.Key.Length > 200 || header.Value.Length > 8192
                || !Regex.IsMatch(header.Key, "^[!#$%&'*+.^_`|~0-9A-Za-z-]+$") || header.Value.Any(x => (char.IsControl(x) && x != '\t') || x > 255))
                throw new ArgumentException("响应头名称或值无效。");
            if (!RemovedHeaders.Contains(header.Key) && !connectionHeaders.Contains(header.Key) && !header.Key.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase))
                headers.Add(header);
        }
        return response with { Headers = headers };
    }
}
