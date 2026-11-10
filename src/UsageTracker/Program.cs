using System.Globalization;
using System.Runtime.InteropServices;
using UsageTracker.Core;
using static UsageTracker.Native.Win32;

namespace UsageTracker;

internal static class Program
{
    private const string Usage = """
        Windows Usage Tracker shows your Claude and Codex plan limits inside the taskbar.

          --diagnose   Write detailed logs (window handles, positions, every poll) to
                       %LOCALAPPDATA%\WindowsUsageTracker\usage-tracker.log
          --check      Read both providers once, show the result in a message box, and exit
          --help       Show this help
        """;

    [STAThread]
    private static int Main(string[] args)
    {
        var diagnose = args.Any(a => Is(a, "--diagnose"));
        Log.Init(diagnose);

        if (args.Any(a => Is(a, "--help") || Is(a, "-h") || Is(a, "/?")))
        {
            MessageBoxW(IntPtr.Zero, Usage, "Windows Usage Tracker", MB_OK | MB_ICONINFORMATION);
            return 0;
        }

        CoreLog.Info = Log.Info;
        CoreLog.Verbose = Log.Verbose;

        var version = typeof(Program).Assembly.GetName().Version?.ToString(3);
        Log.Info($"Starting v{version} on {RuntimeInformation.OSDescription} ({RuntimeInformation.ProcessArchitecture}), args: {string.Join(' ', args)}");

        if (args.Any(a => Is(a, "--check"))) return Check();

        using var mutex = new Mutex(initiallyOwned: true, @"Local\WindowsUsageTracker.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            Log.Info("Another instance is already running; exiting");
            return 0;
        }

        try
        {
            using var app = new App();
            return app.Run();
        }
        catch (Exception ex)
        {
            Log.Error("Fatal error", ex);
            MessageBoxW(IntPtr.Zero, $"Windows Usage Tracker stopped: {ex.Message}\n\nDetails are in {Log.FilePath}", "Windows Usage Tracker", MB_OK | MB_ICONWARNING);
            return 1;
        }
    }

    /// <summary>Fetches both providers once and shows what came back, for checking sign-ins without the taskbar.</summary>
    private static int Check()
    {
        using var http = App.CreateHttpClient();
        IUsageProvider[] providers =
        {
            new ClaudeUsageProvider(http, CredentialLocator.Claude),
            new CodexUsageProvider(http, CredentialLocator.Codex),
        };

        var now = DateTimeOffset.UtcNow;
        var snapshots = providers.Select(p =>
        {
            var result = p.FetchAsync(CancellationToken.None).GetAwaiter().GetResult();
            Log.Info($"--check {p.Kind}: {result.Status} {result.Message}");
            return ProviderSnapshot.Initial(p.Kind).Apply(result, DateTimeOffset.UtcNow);
        }).ToArray();

        var text = Format.Tooltip(snapshots, now, TimeZoneInfo.Local, CultureInfo.CurrentCulture);
        var ok = snapshots.All(s => s.Status == ProviderStatus.Ok);
        MessageBoxW(IntPtr.Zero, text + $"\r\n\r\nLog: {Log.FilePath}", "Windows Usage Tracker check", MB_OK | (ok ? MB_ICONINFORMATION : MB_ICONWARNING));
        return ok ? 0 : 2;
    }

    private static bool Is(string arg, string name) => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase);
}
