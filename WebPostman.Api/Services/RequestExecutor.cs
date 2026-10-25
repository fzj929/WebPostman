using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace WebPostman.Api;

public sealed class RequestExecutor(IHttpClientFactory factory)
{
    private static readonly Regex VariablePattern = new(@"\{\{\s*([^{}]+?)\s*\}\}", RegexOptions.Compiled);
    private const int MaxResponseBytes = 2 * 1024 * 1024;
    private static bool Sensitive(string name) => Regex.IsMatch(name, "authorization|cookie|token|secret|password|passwd|key|credential", RegexOptions.IgnoreCase);
    private static string DisplayUrl(Uri uri, bool reveal, string authKey)
    {
        if (reveal) return uri.ToString();
        var builder = new UriBuilder(uri) { UserName = "", Password = "" };
        builder.Query = string.Join("&", builder.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(part =>
        {
            var pair = part.Split('=', 2); var key = Uri.UnescapeDataString(pair[0]);
            return Sensitive(key) || key.Equals(authKey, StringComparison.OrdinalIgnoreCase) ? pair[0] + "=REDACTED" : part;
        }));
        return builder.Uri.ToString();
    }

    public static string SafeDisplayUrl(string raw)
    {
        var cut = raw.IndexOf('?');
        return cut < 0 ? raw : raw[..cut];
    }

    public async Task<ExecuteResult> ExecuteAsync(ExecuteInput input, CancellationToken cancellationToken)
    {
        var overall = Stopwatch.StartNew();
        string Expand(string value) => VariablePattern.Replace(value ?? "", match =>
            input.Variables.TryGetValue(match.Groups[1].Value, out var replacement) ? replacement : match.Value);

        var spec = input.Request;
        var method = (spec.Method ?? "GET").ToUpperInvariant();
        if (!new[] { "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS" }.Contains(method))
            throw new ArgumentException("不支持的 HTTP 方法。");

        var url = Expand(spec.Url).Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            throw new ArgumentException("请输入有效的 HTTP 或 HTTPS 地址。");

        var query = spec.Params.Where(x => x.Enabled && !string.IsNullOrWhiteSpace(x.Key))
            .Select(x => (Key: Expand(x.Key), Value: Expand(x.Value))).ToList();
        var auth = input.EffectiveAuth;
        if (auth.Type == "apiKey" && auth.In == "query" && !string.IsNullOrWhiteSpace(auth.Key))
            query.Add((Expand(auth.Key), Expand(auth.Value)));
        if (query.Count > 0)
        {
            var builder = new UriBuilder(uri);
            var extra = string.Join("&", query.Select(x => $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value)}"));
            builder.Query = string.IsNullOrEmpty(builder.Query) ? extra : builder.Query.TrimStart('?') + "&" + extra;
            uri = builder.Uri;
        }

        using var request = new HttpRequestMessage(new HttpMethod(method), uri);
        request.Content = BuildContent(spec with { Method = method }, Expand);
        foreach (var row in spec.Headers.Where(x => x.Enabled && !string.IsNullOrWhiteSpace(x.Key)))
        {
            var key = Expand(row.Key).Trim();
            var value = Expand(row.Value);
            if (key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) && auth.Type != "none") continue;
            if (!request.Headers.TryAddWithoutValidation(key, value))
            {
                if (request.Content is null) request.Content = new ByteArrayContent([]);
                request.Content.Headers.Remove(key);
                if (!request.Content.Headers.TryAddWithoutValidation(key, value))
                    throw new ArgumentException($"无效的请求头：{key}");
            }
        }
        ApplyAuth(request, auth, Expand);
        var cookies = (spec.Cookies ?? []).Where(x => x.Enabled && !string.IsNullOrWhiteSpace(x.Key))
            .Select(x => $"{Expand(x.Key)}={Expand(x.Value)}").ToList();
        if (cookies.Count > 0)
        {
            var existing = request.Headers.TryGetValues("Cookie", out var current) ? string.Join("; ", current) : "";
            request.Headers.Remove("Cookie");
            if (!request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", new[] { existing }.Where(x => x.Length > 0).Concat(cookies))))
                throw new ArgumentException("Cookie 设置无效。");
        }
        if (!request.Headers.Contains("Accept")) request.Headers.TryAddWithoutValidation("Accept", "*/*");
        if (!request.Headers.Contains("User-Agent")) request.Headers.TryAddWithoutValidation("User-Agent", "WebPostman/1.0");
        if (!request.Headers.Contains("Accept-Encoding")) request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate, br");
        var prepareMs = overall.ElapsedMilliseconds;
        Dictionary<string, string> TraceHeaders(HttpRequestMessage sent) => sent.Headers.Concat(sent.Content?.Headers.AsEnumerable() ?? [])
            .ToDictionary(x => x.Key, x => !spec.ShowSensitiveTrace && (Sensitive(x.Key) || x.Key.Equals(Expand(auth.Key), StringComparison.OrdinalIgnoreCase))
                ? "••••••" : string.Join(", ", x.Value), StringComparer.OrdinalIgnoreCase);
        var bodyPreview = spec.BodyType is "json" or "text" ? Expand(spec.Body) : spec.BodyType is "form" or "multipart"
            ? string.Join("&", spec.Form.Where(x => x.Enabled && !string.IsNullOrWhiteSpace(x.Key)).Select(x => $"{Expand(x.Key)}={Expand(x.Value)}"))
            : "";
        if (bodyPreview.Length > 4096) bodyPreview = bodyPreview[..4096] + "…";
        if (!spec.ShowSensitiveTrace && bodyPreview.Length > 0) bodyPreview = "[正文预览已隐藏；可在请求设置中显示敏感诊断]";

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(spec.TimeoutSeconds));
        var client = factory.CreateClient(spec.AllowInsecureSsl ? "insecure-requests" : "requests");
        var sendClock = Stopwatch.StartNew();
        using var response = await SendAsync(client, request, spec.FollowRedirects, Expand(auth.Key), timeout.Token);
        using var redirectedRequest = response.RequestMessage == request ? null : response.RequestMessage;
        var finalRequest = response.RequestMessage ?? request;
        sendClock.Stop();
        var downloadClock = Stopwatch.StartNew();
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var memory = new MemoryStream();
        var buffer = new byte[16384];
        int count;
        while ((count = await stream.ReadAsync(buffer, timeout.Token)) > 0)
        {
            var remaining = MaxResponseBytes + 1 - (int)memory.Length;
            if (remaining <= 0) break;
            memory.Write(buffer, 0, Math.Min(count, remaining));
            if (memory.Length > MaxResponseBytes) break;
        }
        downloadClock.Stop();
        overall.Stop();
        var bytes = memory.ToArray();
        var truncated = bytes.Length > MaxResponseBytes;
        if (truncated) Array.Resize(ref bytes, MaxResponseBytes);
        var charset = response.Content.Headers.ContentType?.CharSet;
        Encoding encoding;
        try { encoding = string.IsNullOrWhiteSpace(charset) ? Encoding.UTF8 : Encoding.GetEncoding(charset.Trim('"')); }
        catch (ArgumentException) { encoding = Encoding.UTF8; }
        var headers = response.Headers.Concat(response.Content.Headers)
            .ToDictionary(x => x.Key, x => string.Join(", ", x.Value), StringComparer.OrdinalIgnoreCase);
        var setCookies = response.Headers.TryGetValues("Set-Cookie", out var rawCookies) ? rawCookies : [];
        var responseCookies = setCookies.Select(raw =>
        {
            var parts = raw.Split(';', 2);
            var separator = parts[0].IndexOf('=');
            return new CookieInfo(separator < 0 ? parts[0] : parts[0][..separator],
                separator < 0 ? "" : parts[0][(separator + 1)..], parts.Length > 1 ? parts[1].Trim() : "", raw);
        }).ToList();
        var trace = new RequestTrace(DisplayUrl(finalRequest.RequestUri!, spec.ShowSensitiveTrace, Expand(auth.Key)), finalRequest.Method.Method, $"HTTP/{response.Version}", TraceHeaders(finalRequest), finalRequest.Content is null ? "" : bodyPreview,
        [
            new("准备请求", prepareMs, "变量替换、参数、授权和请求头"),
            new("等待响应头", sendClock.ElapsedMilliseconds, "包含连接建立、TLS 握手、上传及服务端处理；连接复用时部分步骤不会发生"),
            new("读取响应体", downloadClock.ElapsedMilliseconds, $"读取 {bytes.Length:N0} 字节")
        ], DisplayUrl(uri, spec.ShowSensitiveTrace, Expand(auth.Key)));
        var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
        var binary = !(mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) || mediaType.Contains("json", StringComparison.OrdinalIgnoreCase) || mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase) || mediaType.Contains("javascript", StringComparison.OrdinalIgnoreCase) || mediaType.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase));
        return new ExecuteResult((int)response.StatusCode, response.ReasonPhrase ?? "", overall.ElapsedMilliseconds,
            response.Content.Headers.ContentLength ?? bytes.LongLength, response.Content.Headers.ContentType?.MediaType ?? "",
            headers, binary ? "[二进制响应，请下载查看]" : encoding.GetString(bytes), truncated, responseCookies, trace,
            binary ? Convert.ToBase64String(bytes) : null, Path.GetFileName(response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"') ?? (binary ? "response.bin" : "response.txt")), RecordedBodyBase64: Convert.ToBase64String(bytes));
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage original, bool follow, string authKey, CancellationToken token)
    {
        var current = original;
        try
        {
            if (follow && current.Content is not null) await current.Content.ReadAsByteArrayAsync(token);
            for (var count = 0; ; count++)
            {
                var response = await client.SendAsync(current, HttpCompletionOption.ResponseHeadersRead, token);
                var code = (int)response.StatusCode;
                if (!follow || code is not (301 or 302 or 303 or 307 or 308) || response.Headers.Location is null) return response;
                if (count >= 10) { response.Dispose(); throw new HttpRequestException("重定向次数超过 10 次。"); }
                var nextUri = new Uri(current.RequestUri!, response.Headers.Location);
                if (nextUri.Scheme is not ("http" or "https")) { response.Dispose(); throw new ArgumentException("重定向目标必须为 HTTP 或 HTTPS。"); }
                var dropBody = (code == 303 && current.Method != HttpMethod.Head) || ((code is 301 or 302) && current.Method == HttpMethod.Post);
                var next = new HttpRequestMessage(dropBody ? HttpMethod.Get : current.Method, nextUri);
                var sameOrigin = nextUri.Scheme == current.RequestUri!.Scheme && nextUri.Authority == current.RequestUri.Authority;
                foreach (var header in current.Headers)
                    if (!header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase) && (sameOrigin || (!Sensitive(header.Key) && !header.Key.Equals(authKey, StringComparison.OrdinalIgnoreCase)))) next.Headers.TryAddWithoutValidation(header.Key, header.Value);
                if (!dropBody && current.Content is not null)
                {
                    next.Content = new ByteArrayContent(await current.Content.ReadAsByteArrayAsync(token));
                    foreach (var header in current.Content.Headers) next.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
                response.Dispose(); if (current != original) current.Dispose(); current = next;
            }
        }
        catch { if (current != original) current.Dispose(); throw; }
    }

    private static HttpContent? BuildContent(RequestItem spec, Func<string, string> expand)
    {
        if (spec.BodyType == "none" || spec.Method is "GET" or "HEAD") return null;
        if (spec.BodyType == "form")
            return new FormUrlEncodedContent(spec.Form.Where(x => x.Enabled && !string.IsNullOrWhiteSpace(x.Key))
                .Select(x => new KeyValuePair<string, string>(expand(x.Key), expand(x.Value))));
        if (spec.BodyType == "multipart")
        {
            var multipart = new MultipartFormDataContent();
            foreach (var row in spec.Form.Where(x => x.Enabled && !string.IsNullOrWhiteSpace(x.Key)))
                multipart.Add(new StringContent(expand(row.Value)), expand(row.Key));
            foreach (var file in spec.Files ?? [])
            {
                byte[] bytes;
                try { bytes = Convert.FromBase64String(file.Base64); }
                catch (FormatException) { throw new ArgumentException($"文件内容无效：{file.FileName}"); }
                if (bytes.Length > 10 * 1024 * 1024) throw new ArgumentException("单个文件不能超过 10 MB。");
                var content = new ByteArrayContent(bytes);
                content.Headers.ContentType = new MediaTypeHeaderValue(string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType);
                multipart.Add(content, expand(file.Key), file.FileName);
            }
            return multipart;
        }
        var mediaType = spec.BodyType == "json" ? "application/json" : "text/plain";
        return new StringContent(expand(spec.Body), Encoding.UTF8, mediaType);
    }

    private static void ApplyAuth(HttpRequestMessage request, AuthSettings auth, Func<string, string> expand)
    {
        switch (auth.Type)
        {
            case "basic":
                var raw = $"{expand(auth.Username)}:{expand(auth.Password)}";
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)));
                break;
            case "bearer":
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", expand(auth.Token));
                break;
            case "apiKey" when auth.In == "header" && !string.IsNullOrWhiteSpace(auth.Key):
                var key = expand(auth.Key);
                request.Headers.Remove(key);
                if (!request.Headers.TryAddWithoutValidation(key, expand(auth.Value)))
                    throw new ArgumentException("API Key 的请求头名称无效。");
                break;
        }
    }
}
