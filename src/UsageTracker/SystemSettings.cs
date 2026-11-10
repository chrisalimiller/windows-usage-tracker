using Microsoft.Win32;

namespace UsageTracker;

/// <summary>Taskbar theme, "start with Windows" and the app's own settings, all in the current user's registry.</summary>
internal static class SystemSettings
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "WindowsUsageTracker";
    private const string AppKey = @"Software\WindowsUsageTracker";
    private const string MakeRoomValue = "MakeRoom";

    /// <summary>Whether to narrow the taskbar's own content to free a strip for the widget (on by default).</summary>
    public static bool MakeRoom()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AppKey);
            return key?.GetValue(MakeRoomValue) is not int value || value != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return true;
        }
    }

    public static void SetMakeRoom(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(AppKey);
            key.SetValue(MakeRoomValue, enabled ? 1 : 0, RegistryValueKind.DWord);
            Log.Info($"Make room in the taskbar {(enabled ? "enabled" : "disabled")}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Error("Couldn't save the make-room setting", ex);
        }
    }

    /// <summary>
    /// The taskbar follows the Windows mode (SystemUsesLightTheme), not the app mode.
    /// Defaults to dark when the value is missing.
    /// </summary>
    public static bool TaskbarIsLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    public static bool StartsWithWindows()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is string;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    public static void SetStartWithWindows(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Unknown executable path");
                key.SetValue(RunValue, $"\"{exe}\"");
                Log.Info($"Start with Windows enabled: {exe}");
            }
            else
            {
                key.DeleteValue(RunValue, throwOnMissingValue: false);
                Log.Info("Start with Windows disabled");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException)
        {
            Log.Error("Couldn't change start with Windows", ex);
        }
    }
}
