namespace WebPostman.Api;

public sealed record MockServerInput(string Name, int Port, List<string> CorsOrigins, long Revision = 0);
public sealed record MockResponse(int Status, string ContentType, List<KeyValueRow> Headers, string Body,
    string? BodyBase64 = null, bool Binary = false, int DelayMs = 0);
public sealed record MockRuleInput(string Name, string Method, string Path, string Query, bool Enabled,
    MockResponse Response, string SourceUrl = "", long Revision = 0, bool Truncated = false);
public sealed record MockRule(string Id, string Name, string Method, string Path, string Query, bool Enabled,
    MockResponse Response, MockResponse OriginalResponse, string SourceUrl, long Revision);
public sealed record MockServer(string Id, string Name, int Port, List<string> CorsOrigins, long Revision,
    bool Running, int RuleCount, long Calls, List<MockRule>? Rules = null);
public sealed record MockCall(DateTimeOffset At, string Method, string Path, int Status, long DurationMs);
public sealed class MockConflictException(string message) : Exception(message);
