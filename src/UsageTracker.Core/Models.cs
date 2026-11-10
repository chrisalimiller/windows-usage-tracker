namespace UsageTracker.Core;

/// <summary>Which service a reading came from.</summary>
public enum ProviderKind
{
    Claude,
    Codex,
}

/// <summary>Outcome of the last attempt to read a provider's usage.</summary>
public enum ProviderStatus
{
    /// <summary>Nothing fetched yet.</summary>
    Pending,
    Ok,
    /// <summary>No credentials file was found.</summary>
    NotSignedIn,
    /// <summary>Credentials exist but the access token has expired (the CLI refreshes it on next use).</summary>
    TokenExpired,
    /// <summary>The server rejected the token (401/403).</summary>
    Unauthorized,
    /// <summary>The server asked us to slow down (429).</summary>
    RateLimited,
    /// <summary>Network failure, timeout or a 5xx.</summary>
    NetworkError,
    /// <summary>The response could not be understood (the endpoint may have changed).</summary>
    BadResponse,
    /// <summary>The sign-in type has no plan limits to show (for example an API key).</summary>
    Unsupported,
}

/// <summary>One rolling limit window, such as the 5-hour session or the 7-day week.</summary>
/// <param name="UsedPercent">Percent of the window used, 0 to 100 (may exceed 100 briefly).</param>
/// <param name="ResetsAt">When the window resets, if the server said.</param>
public sealed record UsageWindow(double UsedPercent, DateTimeOffset? ResetsAt);

/// <summary>The two windows the tracker shows. Either may be missing.</summary>
public sealed record UsageReading(UsageWindow? Session, UsageWindow? Weekly, string? PlanLabel = null);

/// <summary>Everything the UI needs to know about one provider at a moment in time.</summary>
public sealed record ProviderSnapshot(
    ProviderKind Kind,
    ProviderStatus Status,
    UsageReading? Reading,
    DateTimeOffset? ReadingFetchedAt,
    string? Message,
    DateTimeOffset AttemptedAt,
    TimeSpan? RetryAfter = null)
{
    public static ProviderSnapshot Initial(ProviderKind kind) =>
        new(kind, ProviderStatus.Pending, null, null, null, DateTimeOffset.MinValue);

    public string DisplayName => Kind == ProviderKind.Claude ? "Claude" : "Codex";

    /// <summary>
    /// Builds the next snapshot from a fetch result, keeping the last good reading when the
    /// new attempt failed so the taskbar does not blank out on a transient error.
    /// </summary>
    public ProviderSnapshot Apply(FetchResult result, DateTimeOffset now)
    {
        if (result.Status == ProviderStatus.Ok && result.Reading is not null)
        {
            return new ProviderSnapshot(Kind, ProviderStatus.Ok, result.Reading, now, null, now);
        }

        // Signed out or unsupported: old numbers would be misleading, drop them.
        var keepOld = result.Status is ProviderStatus.RateLimited or ProviderStatus.NetworkError
            or ProviderStatus.BadResponse or ProviderStatus.TokenExpired or ProviderStatus.Unauthorized;
        return new ProviderSnapshot(
            Kind,
            result.Status,
            keepOld ? Reading : null,
            keepOld ? ReadingFetchedAt : null,
            result.Message,
            now,
            result.RetryAfter);
    }
}

/// <summary>Result of a single fetch attempt.</summary>
public sealed record FetchResult(ProviderStatus Status, UsageReading? Reading, string? Message, TimeSpan? RetryAfter = null)
{
    public static FetchResult Success(UsageReading reading) => new(ProviderStatus.Ok, reading, null);
    public static FetchResult Failure(ProviderStatus status, string message, TimeSpan? retryAfter = null) =>
        new(status, null, message, retryAfter);
}
