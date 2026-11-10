using System.Net;
using System.Net.Http.Headers;
using UsageTracker.Core.Claude;
using UsageTracker.Core.Codex;

namespace UsageTracker.Core;

/// <summary>Where Core writes log lines. The app points these at its log file.</summary>
public static class CoreLog
{
    public static Action<string>? Info { get; set; }
    public static Action<string>? Verbose { get; set; }
}

public interface IUsageProvider
{
    ProviderKind Kind { get; }

    Task<FetchResult> FetchAsync(CancellationToken cancellationToken);
}

/// <summary>Shared HTTP handling for the two undocumented usage endpoints.</summary>
public abstract class UsageProviderBase : IUsageProvider
{
    protected UsageProviderBase(HttpClient http, Func<DateTimeOffset>? clock)
    {
        Http = http;
        Clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    protected HttpClient Http { get; }
    protected Func<DateTimeOffset> Clock { get; }

    public abstract ProviderKind Kind { get; }

    public abstract Task<FetchResult> FetchAsync(CancellationToken cancellationToken);

    /// <summary>Sends the request and maps transport and HTTP failures to a status.</summary>
    protected async Task<(string? Body, FetchResult? Failure)> SendAsync(HttpRequestMessage request, string refreshHint, CancellationToken cancellationToken)
    {
        var name = Kind.ToString();
        try
        {
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
            var code = (int)response.StatusCode;
            CoreLog.Verbose?.Invoke($"{name}: GET {request.RequestUri?.Host}{request.RequestUri?.AbsolutePath} -> HTTP {code}");

            if (response.IsSuccessStatusCode)
            {
                return (await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false), null);
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return (null, FetchResult.Failure(ProviderStatus.Unauthorized, $"{name} rejected the sign-in (HTTP {code}). {refreshHint}"));
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retry = RetryAfter(response.Headers.RetryAfter, Clock());
                var when = retry is { } r ? $" Trying again in {Format.Duration(r)}." : string.Empty;
                return (null, FetchResult.Failure(ProviderStatus.RateLimited, $"{name} usage endpoint is rate limiting requests.{when}", retry));
            }

            if (code >= 500)
            {
                return (null, FetchResult.Failure(ProviderStatus.NetworkError, $"{name} usage endpoint had a server error (HTTP {code})."));
            }

            return (null, FetchResult.Failure(ProviderStatus.BadResponse, $"{name} usage endpoint answered HTTP {code}; it may have changed."));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, FetchResult.Failure(ProviderStatus.NetworkError, $"{name} usage request timed out."));
        }
        catch (HttpRequestException ex)
        {
            CoreLog.Verbose?.Invoke($"{name}: request failed: {ex.Message}");
            return (null, FetchResult.Failure(ProviderStatus.NetworkError, $"Couldn't reach the {name} usage endpoint."));
        }
    }

    internal static TimeSpan? RetryAfter(RetryConditionHeaderValue? header, DateTimeOffset now)
    {
        if (header is null) return null;
        if (header.Delta is { } delta) return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        if (header.Date is { } date)
        {
            var wait = date - now;
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }

        return null;
    }

    protected static void AddCommonHeaders(HttpRequestMessage request, string token)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }
}

/// <summary>Reads Claude plan usage with the Claude Code OAuth sign-in.</summary>
public sealed class ClaudeUsageProvider : UsageProviderBase
{
    public static readonly Uri UsageUri = new("https://api.anthropic.com/api/oauth/usage");
    private const string RefreshHint = "Run Claude Code once to refresh your sign-in.";

    private readonly Func<IEnumerable<CredentialLocation>> _locations;

    /// <param name="locations">
    /// Candidate credential files in preference order. Enumerated lazily, so expensive
    /// locations (WSL) are only probed when the earlier ones are missing or expired.
    /// </param>
    public ClaudeUsageProvider(HttpClient http, Func<IEnumerable<CredentialLocation>> locations, Func<DateTimeOffset>? clock = null)
        : base(http, clock)
    {
        _locations = locations;
    }

    public override ProviderKind Kind => ProviderKind.Claude;

    public override async Task<FetchResult> FetchAsync(CancellationToken cancellationToken)
    {
        var now = Clock();
        var credentials = FindCredentials(now, out var sawExpired);
        if (credentials is null)
        {
            return sawExpired
                ? FetchResult.Failure(ProviderStatus.TokenExpired, "Your Claude Code sign-in has expired. " + RefreshHint)
                : FetchResult.Failure(ProviderStatus.NotSignedIn, "No Claude Code sign-in found. Sign in with `claude` on Windows or WSL.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, UsageUri);
        AddCommonHeaders(request, credentials.AccessToken);
        request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");

        var (body, failure) = await SendAsync(request, RefreshHint, cancellationToken).ConfigureAwait(false);
        if (failure is not null) return failure;

        if (!ClaudeUsageParser.TryParse(body!, PlanLabel(credentials.SubscriptionType), out var reading, out var error))
        {
            CoreLog.Info?.Invoke(error ?? "Claude usage response could not be parsed");
            return FetchResult.Failure(ProviderStatus.BadResponse, "Claude usage response changed shape; an update may be needed.");
        }

        return FetchResult.Success(reading);
    }

    private ClaudeCredentials? FindCredentials(DateTimeOffset now, out bool sawExpired)
    {
        sawExpired = false;
        foreach (var location in _locations())
        {
            var text = CredentialFiles.ReadShared(location.Path);
            if (text is null) continue;
            var parsed = CredentialFiles.ParseClaude(text, location.Label);
            if (parsed is null)
            {
                CoreLog.Verbose?.Invoke($"Claude: credentials at {location.Label} have no OAuth token");
                continue;
            }

            if (parsed.IsExpired(now))
            {
                CoreLog.Verbose?.Invoke($"Claude: credentials at {location.Label} expired {parsed.ExpiresAt:u}");
                sawExpired = true;
                continue;
            }

            CoreLog.Verbose?.Invoke($"Claude: using credentials from {location.Label}");
            return parsed;
        }

        return null;
    }

    private static string? PlanLabel(string? subscriptionType)
    {
        if (string.IsNullOrWhiteSpace(subscriptionType)) return null;
        var s = subscriptionType.Trim();
        return char.ToUpperInvariant(s[0]) + s[1..];
    }
}

/// <summary>Reads Codex plan usage with the Codex CLI's ChatGPT sign-in.</summary>
public sealed class CodexUsageProvider : UsageProviderBase
{
    public static readonly Uri UsageUri = new("https://chatgpt.com/backend-api/wham/usage");
    private const string RefreshHint = "Run Codex once to refresh your sign-in.";

    private readonly Func<IEnumerable<CredentialLocation>> _locations;

    public CodexUsageProvider(HttpClient http, Func<IEnumerable<CredentialLocation>> locations, Func<DateTimeOffset>? clock = null)
        : base(http, clock)
    {
        _locations = locations;
    }

    public override ProviderKind Kind => ProviderKind.Codex;

    public override async Task<FetchResult> FetchAsync(CancellationToken cancellationToken)
    {
        var now = Clock();
        CodexCredentials? credentials = null;
        var sawExpired = false;
        var sawApiKey = false;
        foreach (var location in _locations())
        {
            var text = CredentialFiles.ReadShared(location.Path);
            if (text is null) continue;
            var parsed = CredentialFiles.ParseCodex(text, location.Label, out var apiKeyOnly);
            sawApiKey |= apiKeyOnly;
            if (parsed is null) continue;
            if (parsed.IsExpired(now))
            {
                CoreLog.Verbose?.Invoke($"Codex: credentials at {location.Label} expired {parsed.ExpiresAt:u}");
                sawExpired = true;
                continue;
            }

            CoreLog.Verbose?.Invoke($"Codex: using credentials from {location.Label}");
            credentials = parsed;
            break;
        }

        if (credentials is null)
        {
            if (sawExpired) return FetchResult.Failure(ProviderStatus.TokenExpired, "Your Codex sign-in has expired. " + RefreshHint);
            if (sawApiKey) return FetchResult.Failure(ProviderStatus.Unsupported, "Codex is signed in with an API key, which has no plan limits to show.");
            return FetchResult.Failure(ProviderStatus.NotSignedIn, "No Codex sign-in found. Run `codex login` with your ChatGPT account.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, UsageUri);
        AddCommonHeaders(request, credentials.AccessToken);
        // The Codex CLI identifies itself this way; chatgpt.com may refuse unfamiliar clients.
        request.Headers.TryAddWithoutValidation("User-Agent", "codex-cli");
        if (credentials.AccountId is { } account)
        {
            request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", account);
        }

        var (body, failure) = await SendAsync(request, RefreshHint, cancellationToken).ConfigureAwait(false);
        if (failure is not null) return failure;

        if (!CodexUsageParser.TryParse(body!, Clock(), out var reading, out var error))
        {
            CoreLog.Info?.Invoke(error ?? "Codex usage response could not be parsed");
            return FetchResult.Failure(ProviderStatus.BadResponse, "Codex usage response changed shape; an update may be needed.");
        }

        return FetchResult.Success(reading);
    }
}
