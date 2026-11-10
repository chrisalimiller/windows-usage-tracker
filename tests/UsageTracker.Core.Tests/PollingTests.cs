namespace UsageTracker.Core.Tests;

public class PollingTests
{
    [Fact]
    public void Success_polls_at_the_base_interval()
    {
        Assert.Equal(Backoff.BaseInterval, Backoff.NextDelay(ProviderStatus.Ok, 0, null));
    }

    [Theory]
    [InlineData(1, 180)]
    [InlineData(2, 360)]
    [InlineData(3, 720)]
    [InlineData(4, 900)]
    [InlineData(10, 900)]
    public void Failures_back_off_exponentially_up_to_fifteen_minutes(int failures, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), Backoff.NextDelay(ProviderStatus.NetworkError, failures, null));
    }

    [Fact]
    public void Retry_after_is_respected_when_longer()
    {
        Assert.Equal(TimeSpan.FromMinutes(20), Backoff.NextDelay(ProviderStatus.RateLimited, 1, TimeSpan.FromMinutes(20)));
        Assert.Equal(TimeSpan.FromHours(1), Backoff.NextDelay(ProviderStatus.RateLimited, 1, TimeSpan.FromHours(5)));
        Assert.Equal(TimeSpan.FromSeconds(180), Backoff.NextDelay(ProviderStatus.RateLimited, 1, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Signed_out_checks_the_files_every_two_minutes()
    {
        Assert.Equal(Backoff.SignedOutInterval, Backoff.NextDelay(ProviderStatus.TokenExpired, 0, null));
        Assert.Equal(Backoff.SignedOutInterval, Backoff.NextDelay(ProviderStatus.NotSignedIn, 0, null));
    }

    [Fact]
    public void A_failed_poll_keeps_the_last_good_reading()
    {
        var t0 = new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);
        var reading = new UsageReading(new UsageWindow(10, null), new UsageWindow(20, null));
        var ok = ProviderSnapshot.Initial(ProviderKind.Claude).Apply(FetchResult.Success(reading), t0);

        var failed = ok.Apply(FetchResult.Failure(ProviderStatus.NetworkError, "offline"), t0.AddMinutes(2));

        Assert.Equal(ProviderStatus.NetworkError, failed.Status);
        Assert.Same(reading, failed.Reading);
        Assert.Equal(t0, failed.ReadingFetchedAt);
        Assert.Equal("offline", failed.Message);
    }

    [Fact]
    public void Signing_out_drops_the_old_reading()
    {
        var t0 = new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);
        var ok = ProviderSnapshot.Initial(ProviderKind.Codex).Apply(FetchResult.Success(new UsageReading(new UsageWindow(10, null), null)), t0);

        var signedOut = ok.Apply(FetchResult.Failure(ProviderStatus.NotSignedIn, "gone"), t0);

        Assert.Null(signedOut.Reading);
        Assert.Null(signedOut.ReadingFetchedAt);
    }

    [Fact]
    public async Task Poller_reports_results_and_refreshes_on_demand()
    {
        var provider = new CountingProvider();
        using var poller = new ProviderPoller(provider);
        var updates = new System.Collections.Concurrent.BlockingCollection<ProviderSnapshot>();
        poller.Updated += updates.Add;

        poller.Start();
        Assert.True(updates.TryTake(out var first, TimeSpan.FromSeconds(5)));
        Assert.Equal(ProviderStatus.Ok, first!.Status);
        Assert.Equal(1, provider.Calls);

        // A refresh right away is held back by the minimum gap, so no second call yet.
        poller.RefreshNow();
        await Task.Delay(300);
        Assert.Equal(1, provider.Calls);
    }

    private sealed class CountingProvider : IUsageProvider
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public ProviderKind Kind => ProviderKind.Claude;

        public Task<FetchResult> FetchAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(FetchResult.Success(new UsageReading(new UsageWindow(1, null), null)));
        }
    }
}
