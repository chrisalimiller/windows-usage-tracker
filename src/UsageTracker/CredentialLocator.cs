using Microsoft.Win32;
using UsageTracker.Core;

namespace UsageTracker;

/// <summary>
/// Lists where the Claude Code and Codex CLIs keep their sign-ins on this PC: Windows first,
/// then each installed WSL distro. WSL paths are only probed when the Windows file is
/// missing or expired (the providers enumerate lazily), because touching \\wsl.localhost
/// can start a distro.
/// </summary>
internal static class CredentialLocator
{
    private static readonly TimeSpan WslCacheLifetime = TimeSpan.FromMinutes(30);
    private static readonly object WslGate = new();
    private static List<string>? _wslHomes;
    private static DateTime _wslHomesAt;

    public static IEnumerable<CredentialLocation> Claude()
    {
        var configDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(configDir))
        {
            yield return new CredentialLocation(Path.Combine(configDir, ".credentials.json"), "Windows (CLAUDE_CONFIG_DIR)");
        }

        yield return new CredentialLocation(Path.Combine(UserProfile, ".claude", ".credentials.json"), "Windows");

        foreach (var home in WslHomes())
        {
            yield return new CredentialLocation(Path.Combine(home, ".claude", ".credentials.json"), WslLabel(home));
        }
    }

    public static IEnumerable<CredentialLocation> Codex()
    {
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(codexHome))
        {
            yield return new CredentialLocation(Path.Combine(codexHome, "auth.json"), "Windows (CODEX_HOME)");
        }
        else
        {
            yield return new CredentialLocation(Path.Combine(UserProfile, ".codex", "auth.json"), "Windows");
        }

        foreach (var home in WslHomes())
        {
            yield return new CredentialLocation(Path.Combine(home, ".codex", "auth.json"), WslLabel(home));
        }
    }

    public static string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string WslLabel(string home)
    {
        // \\wsl.localhost\Ubuntu\home\chris -> "WSL Ubuntu (~chris)"
        var parts = home.TrimStart('\\').Split('\\');
        var distro = parts.Length > 1 ? parts[1] : "?";
        var user = parts.Length > 0 ? parts[^1] : "?";
        return $"WSL {distro} (~{user})";
    }

    /// <summary>Home directories inside each WSL distro (/home/* and /root), cached for 30 minutes.</summary>
    private static List<string> WslHomes()
    {
        lock (WslGate)
        {
            if (_wslHomes is not null && DateTime.UtcNow - _wslHomesAt < WslCacheLifetime) return _wslHomes;
        }

        var homes = new List<string>();
        foreach (var distro in WslDistros())
        {
            foreach (var prefix in new[] { @"\\wsl.localhost\", @"\\wsl$\" })
            {
                var root = prefix + distro;
                try
                {
                    var homeDir = Path.Combine(root, "home");
                    if (!Directory.Exists(homeDir)) continue;
                    homes.AddRange(Directory.EnumerateDirectories(homeDir));
                    homes.Add(Path.Combine(root, "root"));
                    Log.Verbose($"WSL: found distro {distro} at {root}");
                    break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Verbose($"WSL: couldn't read {root}: {ex.Message}");
                }
            }
        }

        lock (WslGate)
        {
            _wslHomes = homes;
            _wslHomesAt = DateTime.UtcNow;
        }

        return homes;
    }

    private static IEnumerable<string> WslDistros()
    {
        var names = new List<string>();
        try
        {
            using var lxss = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss");
            if (lxss is null) return names;
            foreach (var id in lxss.GetSubKeyNames())
            {
                using var key = lxss.OpenSubKey(id);
                if (key?.GetValue("DistributionName") is string name
                    && !string.IsNullOrWhiteSpace(name)
                    && !name.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase))
                {
                    names.Add(name);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Verbose($"WSL: couldn't list distros: {ex.Message}");
        }

        return names;
    }
}
