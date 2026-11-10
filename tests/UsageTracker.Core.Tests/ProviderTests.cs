using System.Net;

namespace UsageTracker.Core.Tests;

public sealed class ProviderTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 2, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wut-tests-" + Guid.NewGuid());

    public ProviderTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private CredentialLocation Write(string name, string json)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, json);
        return new CredentialLocation(path, name);
    }

    private CredentialLocation Missing(string name) => new(Path.Combine(_dir, name), name);

    private static string ClaudeCreds(DateTimeOffset expires, string token = "tok") =>
        $$$"""{"claudeAiOauth":{"accessToken":"{{{token}}}","expiresAt":{{{expires.ToUnixTimeMilliseconds()}}},"subscriptionType":"pro"}}""";

    [Fact]
    public async Task Claude_success_sends_the_expected_request()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"five_hour":{"utilization":12,"resets_at":"2026-11-10T05:00:00Z"},"seven_day":{"utilization":34,"resets_at":"2026-11-14T00:00:00Z"}}""");
        var creds = Write("claude.json", ClaudeCreds(Now.AddHours(3)));
        var provider = new ClaudeUsageProvider(new HttpClient(handler), () => new[] { creds }, () => Now);

        var result = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, result.Status);
        Assert.Equal(12, result.Reading!.Session!.UsedPercent);
        Assert.Equal(34, result.Reading.Weekly!.UsedPercent);
        Assert.Equal("Pro", result.Reading.PlanLabel);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(ClaudeUsageProvider.UsageUri, request.Uri);
        Assert.Equal("Bearer tok", request.Authorization);
        Assert.Equal("oauth-2025-04-20", request.Header("anthropic-beta"));
    }

    [Fact]
    public async Task Claude_skips_an_expired_windows_sign_in_for_a_valid_wsl_one()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"five_hour":{"utilization":1},"seven_day":{"utilization":2}}""");
        var windows = Write("win.json", ClaudeCreds(Now.AddMinutes(-1), "old"));
        var wsl = Write("wsl.json", ClaudeCreds(Now.AddHours(5), "fresh"));
        var provider = new ClaudeUsageProvider(new HttpClient(handler), () => new[] { windows, wsl }, () => Now);

        var result = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, result.Status);
        Assert.Equal("Bearer fresh", Assert.Single(handler.Requests).Authorization);
    }

    [Fact]
    public async Task Claude_with_only_expired_sign_ins_makes_no_request()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{}");
        var creds = Write("claude.json", ClaudeCreds(Now.AddMinutes(-1)));
        var provider = new ClaudeUsageProvider(new HttpClient(handler), () => new[] { creds }, () => Now);

        var result = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.TokenExpired, result.Status);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Claude_without_any_sign_in_is_not_signed_in()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{}");
        var provider = new ClaudeUsageProvider(new HttpClient(handler), () => new[] { Missing("nope.json") }, () => Now);

        var result = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.NotSignedIn, result.Status);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, ProviderStatus.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, ProviderStatus.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError, ProviderStatus.NetworkError)]
    [InlineData(HttpStatusCode.NotFound, ProviderStatus.BadResponse)]
    public async Task Http_errors_map_to_statuses(HttpStatusCode code, ProviderStatus expected)
    {
        var creds = Write("claude.json", ClaudeCreds(Now.AddHours(1)));
        var provider = new ClaudeUsageProvider(new HttpClient(new StubHandler(code, "{}")), () => new[] { creds }, () => Now);

        var result = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(expected, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [Fact]
    public async Task Rate_limiting_carries_retry_after()
    {
        var handler = new StubHandler(HttpStatusCode.TooManyRequests, "{}", retryAfterSeconds: 120);
        var creds = Write("claude.json", ClaudeCreds(Now.AddHours(1)));
        var provider = new ClaudeUsageProvider(new HttpClient(handler), () => new[] { creds }, () => Now);

        var result = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.RateLimited, result.Status);
        Assert.Equal(TimeSpan.FromSeconds(120), result.RetryAfter);
    }

    [Fact]
    public async Task A_changed_response_shape_is_a_bad_response()
    {
        var creds = Write("claude.json", ClaudeCreds(Now.AddHours(1)));
        var provider = new ClaudeUsageProvider(new HttpClient(new StubHandler(HttpStatusCode.OK, """{"limits":[]}""")), () => new[] { creds }, () => Now);

        var result = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.BadResponse, result.Status);
    }

    [Fact]
    public async Task Network_failures_are_reported_not_thrown()
    {
        var creds = Write("claude.json", ClaudeCreds(Now.AddHours(1)));
        var provider = new ClaudeUsageProvider(new HttpClient(new ThrowingHandler()), () => new[] { creds }, () => Now);

        var result = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.NetworkError, result.Status);
    }

    [Fact]
    public async Task Codex_success_sends_account_header()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"plan_type":"pro","rate_limit":{"primary_window":{"used_percent":50,"limit_window_seconds":18000,"reset_at":1762750800}}}""");
        var token = CredentialTests.Jwt(Now.AddDays(5).ToUnixTimeSeconds());
        var creds = Write("auth.json", $$$"""{"tokens":{"access_token":"{{{token}}}","account_id":"acct-9"}}""");
        var provider = new CodexUsageProvider(new HttpClient(handler), () => new[] { creds }, () => Now);

        var result = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, result.Status);
        Assert.Equal(50, result.Reading!.Session!.UsedPercent);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(CodexUsageProvider.UsageUri, request.Uri);
        Assert.Equal("Bearer " + token, request.Authorization);
        Assert.Equal("acct-9", request.Header("ChatGPT-Account-Id"));
        Assert.Equal("codex-cli", request.Header("User-Agent"));
    }

    [Fact]
    public async Task Codex_expired_token_makes_no_request()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{}");
        var token = CredentialTests.Jwt(Now.AddMinutes(-5).ToUnixTimeSeconds());
        var creds = Write("auth.json", $$$"""{"tokens":{"access_token":"{{{token}}}"}}""");
        var provider = new CodexUsageProvider(new HttpClient(handler), () => new[] { creds }, () => Now);

        var result = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.TokenExpired, result.Status);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Codex_api_key_sign_in_is_unsupported()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{}");
        var creds = Write("auth.json", """{"OPENAI_API_KEY":"sk-123"}""");
        var provider = new CodexUsageProvider(new HttpClient(handler), () => new[] { creds }, () => Now);

        var result = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Unsupported, result.Status);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Later_locations_are_not_probed_once_a_valid_sign_in_is_found()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"five_hour":{"utilization":1}}""");
        var creds = Write("claude.json", ClaudeCreds(Now.AddHours(1)));
        var probedWsl = false;
        IEnumerable<CredentialLocation> Locations()
        {
            yield return creds;
            probedWsl = true;
            yield return Missing("wsl.json");
        }

        var provider = new ClaudeUsageProvider(new HttpClient(handler), Locations, () => Now);
        await provider.FetchAsync(CancellationToken.None);

        Assert.False(probedWsl);
    }

    private sealed record CapturedRequest(Uri? Uri, string? Authorization, Dictionary<string, string> Headers)
    {
        public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _code;
        private readonly string _body;
        private readonly int? _retryAfter;

        public StubHandler(HttpStatusCode code, string body, int? retryAfterSeconds = null)
        {
            _code = code;
            _body = body;
            _retryAfter = retryAfterSeconds;
        }

        public List<CapturedRequest> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(" ", h.Value), StringComparer.OrdinalIgnoreCase);
            Requests.Add(new CapturedRequest(request.RequestUri, request.Headers.Authorization?.ToString(), headers));
            var response = new HttpResponseMessage(_code) { Content = new StringContent(_body) };
            if (_retryAfter is { } s) response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(s));
            return Task.FromResult(response);
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("No such host is known.");
    }
}
