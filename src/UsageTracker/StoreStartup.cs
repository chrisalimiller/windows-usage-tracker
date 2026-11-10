namespace UsageTracker;

/// <summary>
/// "Start with Windows" for the Microsoft Store version. A packaged app can't use the Run key
/// (its registry writes are private to the package), so it uses the startup task declared in
/// Package.appxmanifest, which also shows up in Settings > Apps > Startup.
/// Only the Store build (-p:StoreBuild=true) can reach the Windows Runtime API for it.
/// </summary>
internal static class StoreStartup
{
    public const string TaskId = "WindowsUsageTrackerStartup";

#if STORE
    public static bool IsEnabled()
    {
        try
        {
            var task = Run(() => Windows.ApplicationModel.StartupTask.GetAsync(TaskId).AsTask());
            return task?.State is Windows.ApplicationModel.StartupTaskState.Enabled or Windows.ApplicationModel.StartupTaskState.EnabledByPolicy;
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't read the startup task", ex);
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            var task = Run(() => Windows.ApplicationModel.StartupTask.GetAsync(TaskId).AsTask());
            if (task is null) return;
            if (!enabled)
            {
                task.Disable();
                Log.Info("Start with Windows disabled");
                return;
            }

            var state = Run(() => task.RequestEnableAsync().AsTask());
            Log.Info($"Start with Windows: startup task is now {state}");
            if (state is Windows.ApplicationModel.StartupTaskState.DisabledByUser)
            {
                // Windows won't let an app turn this back on once the user switched it off in Settings.
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:startupapps") { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't change the startup task", ex);
        }
    }

    // WinRT calls are awaited off the UI thread so they can't deadlock its message loop.
    private static T? Run<T>(Func<Task<T>> call)
    {
        var task = Task.Run(call);
        return task.Wait(TimeSpan.FromSeconds(10)) ? task.Result : default;
    }
#else
    public static bool IsEnabled()
    {
        Log.Info("This build can't use the package's startup task; build with -p:StoreBuild=true for the Store package");
        return false;
    }

    public static void SetEnabled(bool enabled) => IsEnabled();
#endif
}
