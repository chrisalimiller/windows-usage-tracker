using System.Text;

namespace UsageTracker;

/// <summary>
/// Plain-text log in %LOCALAPPDATA%\WindowsUsageTracker (or the package's LocalState folder in
/// the Microsoft Store version, where Explorer can see it). Problems are always logged;
/// window handles, rectangles and every poll are logged only with --diagnose.
/// Tokens and response bodies are never logged.
/// </summary>
internal static class Log
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly object Gate = new();

    public static string Directory { get; } = PackageInfo.LocalStateFolder ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowsUsageTracker");

    public static string FilePath { get; } = Path.Combine(Directory, "usage-tracker.log");

    public static bool VerboseEnabled { get; private set; }

    public static void Init(bool verbose)
    {
        VerboseEnabled = verbose;
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Verbose(string message)
    {
        if (VerboseEnabled) Write("DIAG", message);
    }

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
        lock (Gate)
        {
            try
            {
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > MaxBytes)
                {
                    File.Move(FilePath, FilePath + ".old", overwrite: true);
                }

                File.AppendAllText(FilePath, line, Encoding.UTF8);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
