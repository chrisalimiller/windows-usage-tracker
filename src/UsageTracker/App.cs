using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using UsageTracker.Core;
using UsageTracker.Native;
using static UsageTracker.Native.Win32;

namespace UsageTracker;

/// <summary>
/// Owns a hidden top-level window that receives Explorer's TaskbarCreated broadcast,
/// display and theme changes, timers and poll results, and keeps the taskbar widget
/// attached through all of them.
/// </summary>
internal sealed class App : IDisposable
{
    private const string ControllerClass = "WindowsUsageTracker.Controller";
    private const uint DataMessage = WM_APP + 1;
    private static readonly UIntPtr LayoutTimer = new(1);
    private const uint LayoutIntervalMs = 1000;

    private const int CmdRefresh = 1;
    private const int CmdStartup = 2;
    private const int CmdOpenLog = 3;
    private const int CmdExit = 4;

    // Delegates must outlive the native window classes that point at them.
    private readonly WndProc _controllerProc;
    private readonly WndProc _bandProc;
    private readonly IntPtr _instance;
    private readonly uint _taskbarCreatedMessage;
    private readonly HttpClient _http;
    private readonly ProviderPoller[] _pollers;

    private IntPtr _hwnd;
    private TaskbarBand? _band;
    private TrayFallback? _tray;
    private bool _lightTaskbar;
    private int _attachFailures;
    private DateTime _nextAttachAttempt = DateTime.MinValue;
    private string? _trayError;
    private int _ticks;

    public App()
    {
        _instance = GetModuleHandleW(null);
        _controllerProc = ControllerWndProc;
        _bandProc = BandWndProc;
        _taskbarCreatedMessage = RegisterWindowMessageW("TaskbarCreated");
        _http = CreateHttpClient();
        _pollers = new[]
        {
            new ProviderPoller(new ClaudeUsageProvider(_http, CredentialLocator.Claude)),
            new ProviderPoller(new CodexUsageProvider(_http, CredentialLocator.Codex)),
        };
    }

    public static HttpClient CreateHttpClient()
    {
        var http = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        })
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        var version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WindowsUsageTracker", version));
        return http;
    }

    public int Run()
    {
        var icc = new INITCOMMONCONTROLSEX { dwSize = (uint)Marshal.SizeOf<INITCOMMONCONTROLSEX>(), dwICC = ICC_WIN95_CLASSES };
        InitCommonControlsEx(ref icc);

        if (!RegisterClass(ControllerClass, _controllerProc, 0) || !RegisterClass(TaskbarBand.WindowClass, _bandProc, CS_DBLCLKS))
        {
            return 1;
        }

        // A hidden top-level popup (not message-only) so it receives broadcasts like TaskbarCreated.
        _hwnd = CreateWindowExW(WS_EX_TOOLWINDOW, ControllerClass, "Windows Usage Tracker", WS_POPUP,
            0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, _instance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            Log.Error($"Couldn't create the controller window (error {Marshal.GetLastWin32Error()})");
            return 1;
        }

        _lightTaskbar = SystemSettings.TaskbarIsLight();
        _band = new TaskbarBand(_instance, _hwnd);
        _band.ContextMenuRequested += ShowMenu;
        _band.RefreshRequested += RefreshAll;
        _band.Lost += () => _nextAttachAttempt = DateTime.MinValue;
        _tray = new TrayFallback(_hwnd);

        PushContent();
        EnsureEmbedded();
        SetTimer(_hwnd, LayoutTimer, LayoutIntervalMs, IntPtr.Zero);

        foreach (var poller in _pollers)
        {
            poller.Updated += _ => PostMessageW(_hwnd, DataMessage, IntPtr.Zero, IntPtr.Zero);
            poller.Start();
        }

        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }

        return 0;
    }

    private bool RegisterClass(string name, WndProc proc, uint style)
    {
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            style = style,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(proc),
            hInstance = _instance,
            hCursor = LoadCursorW(IntPtr.Zero, IDC_ARROW),
            lpszClassName = name,
        };
        if (RegisterClassExW(ref wc) != 0) return true;
        Log.Error($"RegisterClassEx({name}) failed (error {Marshal.GetLastWin32Error()})");
        return false;
    }

    private IntPtr BandWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam) =>
        _band is not null ? _band.WndProc(hwnd, msg, wParam, lParam) : DefWindowProcW(hwnd, msg, wParam, lParam);

    private IntPtr ControllerWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (msg == _taskbarCreatedMessage && _taskbarCreatedMessage != 0)
            {
                Log.Info("Explorer recreated the taskbar; re-attaching");
                _band?.Destroy();
                _tray?.Readd();
                _nextAttachAttempt = DateTime.MinValue;
                _attachFailures = 0;
                EnsureEmbedded();
                return IntPtr.Zero;
            }

            switch (msg)
            {
                case WM_TIMER when wParam == (IntPtr)(long)LayoutTimer.ToUInt64():
                    EnsureEmbedded();
                    // Keep "updated N min ago" and stale dimming current between polls.
                    if (++_ticks % 30 == 0) PushContent();
                    return IntPtr.Zero;

                case DataMessage:
                    PushContent();
                    return IntPtr.Zero;

                case TrayFallback.CallbackMessage:
                    var mouse = (uint)(lParam.ToInt64() & 0xFFFF);
                    if (mouse is WM_RBUTTONUP or WM_CONTEXTMENU)
                    {
                        if (GetCursorPos(out var pt)) ShowMenu(pt);
                    }
                    else if (mouse == WM_LBUTTONDBLCLK)
                    {
                        RefreshAll();
                    }

                    return IntPtr.Zero;

                case WM_SETTINGCHANGE:
                    var area = lParam != IntPtr.Zero ? Marshal.PtrToStringUni(lParam) : null;
                    if (area == "ImmersiveColorSet")
                    {
                        _lightTaskbar = SystemSettings.TaskbarIsLight();
                        Log.Verbose($"Theme changed; light taskbar = {_lightTaskbar}");
                        PushContent();
                    }

                    _band?.UpdateLayout(force: true);
                    return IntPtr.Zero;

                case WM_DISPLAYCHANGE:
                case WM_DPICHANGED:
                    Log.Verbose("Display or DPI changed");
                    _band?.UpdateLayout(force: true);
                    return IntPtr.Zero;

                case WM_QUERYENDSESSION:
                    return new IntPtr(1);

                case WM_ENDSESSION:
                    if (wParam != IntPtr.Zero) Shutdown();
                    return IntPtr.Zero;

                case WM_CLOSE:
                    Shutdown();
                    return IntPtr.Zero;

                case WM_DESTROY:
                    PostQuitMessage(0);
                    return IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Error handling message 0x{msg:X}", ex);
        }

        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    /// <summary>Called every second: attach if needed, then follow taskbar/tray/DPI changes.</summary>
    private void EnsureEmbedded()
    {
        if (_band is null) return;

        var taskbar = FindWindowW("Shell_TrayWnd", null);
        if (taskbar != IntPtr.Zero && _band.IsAttachedTo(taskbar))
        {
            _band.UpdateLayout(force: false);
            return;
        }

        if (DateTime.UtcNow < _nextAttachAttempt) return;

        string error;
        if (taskbar == IntPtr.Zero)
        {
            error = "Couldn't find the Windows taskbar (Shell_TrayWnd).";
        }
        else
        {
            if (Log.VerboseEnabled) DumpTaskbar(taskbar);
            if (_band.TryAttach(taskbar, out var attachError))
            {
                _attachFailures = 0;
                _nextAttachAttempt = DateTime.MinValue;
                if (_tray is { Visible: true })
                {
                    Log.Info("Embedded after earlier failures; removing the fallback tray icon");
                    _tray.Hide();
                    _trayError = null;
                }

                PushContent();
                return;
            }

            error = $"Couldn't place the widget in the taskbar: {attachError}.";
        }

        _attachFailures++;
        // Retry quickly at first (Explorer may still be starting), then every 15 seconds.
        _nextAttachAttempt = DateTime.UtcNow + (_attachFailures < 5 ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(15));
        if (_attachFailures == 1 || _attachFailures % 20 == 0) Log.Error(error + $" (attempt {_attachFailures})");

        if (_attachFailures >= 5 && _tray is not null && _trayError != error)
        {
            _trayError = error;
            _tray.Show(error, Format.Summary(Snapshots()));
        }
    }

    private static void DumpTaskbar(IntPtr taskbar)
    {
        GetWindowThreadProcessId(taskbar, out var pid);
        Log.Verbose($"Taskbar 0x{taskbar.ToInt64():X} class {ClassName(taskbar)} pid {pid} rect {WindowRect(taskbar)} dpi {GetDpiForWindow(taskbar)}");
        EnumChildWindows(taskbar, (child, _) =>
        {
            Log.Verbose($"  child 0x{child.ToInt64():X} parent 0x{GetParent(child).ToInt64():X} class {ClassName(child)} rect {WindowRect(child)} visible {IsWindowVisible(child)}");
            return true;
        }, IntPtr.Zero);
    }

    private IReadOnlyList<ProviderSnapshot> Snapshots() => _pollers.Select(p => p.Snapshot).ToArray();

    /// <summary>Sends the latest readings to the widget and the fallback icon.</summary>
    private void PushContent()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshots = Snapshots();

        // Hide a provider that isn't signed in at all, unless that would leave nothing.
        var shown = snapshots.Where(s => s.Status is not (ProviderStatus.NotSignedIn or ProviderStatus.Unsupported)).ToArray();
        if (shown.Length == 0) shown = snapshots.ToArray();

        var rows = shown.Select(s => new BandRow(
            s.DisplayName,
            s.Reading?.Session,
            s.Reading?.Weekly,
            Dim: s.ReadingFetchedAt is not { } at || now - at > Format.StaleAfter,
            NeedsAttention: s.Status is ProviderStatus.TokenExpired or ProviderStatus.Unauthorized or ProviderStatus.BadResponse)).ToArray();

        var tooltip = Format.Tooltip(snapshots, now, TimeZoneInfo.Local, CultureInfo.CurrentCulture)
            + "\r\n\r\nDouble-click to refresh, right-click for options.";

        _band?.SetContent(rows, tooltip, BandPalette.For(_lightTaskbar));
        _tray?.UpdateTip(Format.Summary(snapshots));
    }

    private void RefreshAll()
    {
        Log.Verbose("Manual refresh");
        foreach (var poller in _pollers) poller.RefreshNow();
    }

    private void ShowMenu(POINT at)
    {
        var menu = CreatePopupMenu();
        try
        {
            AppendMenuW(menu, MF_STRING, (UIntPtr)CmdRefresh, "Refresh now");
            AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, null);
            AppendMenuW(menu, MF_STRING | (SystemSettings.StartsWithWindows() ? MF_CHECKED : 0), (UIntPtr)CmdStartup, "Start with Windows");
            AppendMenuW(menu, MF_STRING, (UIntPtr)CmdOpenLog, "Open log folder");
            AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, null);
            AppendMenuW(menu, MF_STRING, (UIntPtr)CmdExit, "Exit");

            // Menus need a foreground owner to close properly when you click elsewhere.
            SetForegroundWindow(_hwnd);
            // Open upward from a bottom taskbar (the default), downward from a top one.
            var taskbarAtTop = WindowRect(FindWindowW("Shell_TrayWnd", null)) is { } r && r.Top <= 0 && r.Width > r.Height;
            var flags = TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_NONOTIFY | (taskbarAtTop ? 0 : TPM_BOTTOMALIGN);
            var command = TrackPopupMenuEx(menu, flags, at.X, at.Y, _hwnd, IntPtr.Zero);
            PostMessageW(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);

            switch (command)
            {
                case CmdRefresh:
                    RefreshAll();
                    break;
                case CmdStartup:
                    SystemSettings.SetStartWithWindows(!SystemSettings.StartsWithWindows());
                    break;
                case CmdOpenLog:
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Log.Directory}\"") { UseShellExecute = true });
                    break;
                case CmdExit:
                    Shutdown();
                    break;
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private void Shutdown()
    {
        Log.Info("Exiting");
        KillTimer(_hwnd, LayoutTimer);
        _band?.Destroy();
        _tray?.Hide();
        if (_hwnd != IntPtr.Zero && IsWindow(_hwnd)) DestroyWindow(_hwnd);
    }

    public void Dispose()
    {
        foreach (var poller in _pollers) poller.Dispose();
        _band?.Dispose();
        _tray?.Dispose();
        _http.Dispose();
    }
}
