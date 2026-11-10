namespace UsageTracker.Core;

/// <summary>When to poll next, given how the last attempts went.</summary>
public static class Backoff
{
    public static readonly TimeSpan BaseInterval = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan MaxInterval = TimeSpan.FromMinutes(15);

    /// <summary>Waiting for the user to sign in again: check the files now and then, no network.</summary>
    public static readonly TimeSpan SignedOutInterval = TimeSpan.FromMinutes(2);

    /// <summary>Minimum gap between requests, even for a manual refresh.</summary>
    public static readonly TimeSpan MinimumGap = TimeSpan.FromSeconds(15);

    public static TimeSpan NextDelay(ProviderStatus status, int consecutiveFailures, TimeSpan? retryAfter)
    {
        switch (status)
        {
            case ProviderStatus.Ok:
            case ProviderStatus.Pending:
                return BaseInterval;
            case ProviderStatus.NotSignedIn:
            case ProviderStatus.TokenExpired:
            case ProviderStatus.Unsupported:
                return SignedOutInterval;
        }

        // Doubles per failure: 3, 6, 12 min, capped at 15.
        var exponent = Math.Clamp(consecutiveFailures, 1, 6);
        var delay = TimeSpan.FromTicks(BaseInterval.Ticks * (1L << exponent));
        if (delay > MaxInterval) delay = MaxInterval;
        if (retryAfter is { } r && r > delay) delay = r > TimeSpan.FromHours(1) ? TimeSpan.FromHours(1) : r;
        return delay;
    }
}

/// <summary>
/// Polls one provider on a background task, backing off on failure.
/// Results are reported through <see cref="Updated"/> on a thread-pool thread.
/// </summary>
public sealed class ProviderPoller : IDisposable
{
    private readonly IUsageProvider _provider;
    private readonly Func<DateTimeOffset> _clock;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private CancellationTokenSource _wake = new();
    private Task? _loop;
    private ProviderSnapshot _snapshot;
    private DateTimeOffset _notBefore = DateTimeOffset.MinValue;

    public ProviderPoller(IUsageProvider provider, Func<DateTimeOffset>? clock = null)
    {
        _provider = provider;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _snapshot = ProviderSnapshot.Initial(provider.Kind);
    }

    public event Action<ProviderSnapshot>? Updated;

    public ProviderSnapshot Snapshot
    {
        get { lock (_gate) return _snapshot; }
    }

    public void Start() => _loop ??= Task.Run(RunAsync);

    /// <summary>Polls as soon as allowed (respecting rate limits and a minimum gap).</summary>
    public void RefreshNow()
    {
        lock (_gate)
        {
            _wake.Cancel();
        }
    }

    private async Task RunAsync()
    {
        var failures = 0;
        while (!_stop.IsCancellationRequested)
        {
            var wait = _notBefore - _clock();
            if (wait > TimeSpan.Zero)
            {
                await SleepAsync(wait, wakeable: false).ConfigureAwait(false);
                continue;
            }

            FetchResult result;
            try
            {
                result = await _provider.FetchAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                CoreLog.Info?.Invoke($"{_provider.Kind}: unexpected error: {ex}");
                result = FetchResult.Failure(ProviderStatus.NetworkError, $"Unexpected error reading {_provider.Kind} usage.");
            }

            var now = _clock();
            failures = result.Status is ProviderStatus.Ok or ProviderStatus.NotSignedIn or ProviderStatus.TokenExpired or ProviderStatus.Unsupported
                ? 0
                : failures + 1;

            ProviderSnapshot snapshot;
            lock (_gate)
            {
                _snapshot = _snapshot.Apply(result, now);
                snapshot = _snapshot;
            }

            CoreLog.Verbose?.Invoke($"{_provider.Kind}: {result.Status}" + (result.Message is { } m ? $" ({m})" : string.Empty));
            Updated?.Invoke(snapshot);

            var delay = Backoff.NextDelay(result.Status, failures, result.RetryAfter);
            var gap = result.RetryAfter is { } ra && ra > Backoff.MinimumGap ? ra : Backoff.MinimumGap;
            _notBefore = now + gap;
            await SleepAsync(delay, wakeable: true).ConfigureAwait(false);
        }
    }

    private async Task SleepAsync(TimeSpan delay, bool wakeable)
    {
        CancellationTokenSource linked;
        lock (_gate)
        {
            if (wakeable && _wake.IsCancellationRequested)
            {
                // A refresh was asked for while we were fetching: go again right away
                // (the minimum gap still applies at the top of the loop).
                _wake.Dispose();
                _wake = new CancellationTokenSource();
                return;
            }

            linked = wakeable
                ? CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, _wake.Token)
                : CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        }

        using (linked)
        {
            try
            {
                await Task.Delay(delay, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Woken for a manual refresh, or stopping.
            }
        }

        if (wakeable)
        {
            lock (_gate)
            {
                if (_wake.IsCancellationRequested)
                {
                    _wake.Dispose();
                    _wake = new CancellationTokenSource();
                }
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        _stop.Dispose();
        lock (_gate) _wake.Dispose();
    }
}
