using System.Diagnostics;
using System.Drawing;
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
/// attached and clear of the app buttons through all of them.
/// </summary>
/// <remarks>
/// Keeping clear works in two ways. "Make room" (<see cref="SpaceReserver"/>) narrows the
/// taskbar's own content so a strip is free at the right end. If that's off or doesn't work
/// on this PC, the widget "dodges": it reads where the app buttons end (<see cref="TaskbarProbe"/>)
/// and shrinks to compact rings, or steps aside to a tray icon, when they get close.
/// </remarks>
internal sealed class App : IDisposable
{
    private const string ControllerClass = "WindowsUsageTracker.Controller";
    private const uint DataMessage = WM_APP + 1;
    private const uint ProbeMessage = WM_APP + 3;
    private static readonly UIntPtr LayoutTimer = new(1);
    private const uint LayoutIntervalMs = 1000;

    private static readonly TimeSpan DodgeProbeInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SettledProbeInterval = TimeSpan.FromSeconds(10);

    private const int CmdRefresh = 1;
    private const int CmdStartup = 2;
    private const int CmdOpenLog = 3;
    private const int CmdExit = 4;
    private const int CmdMakeRoom = 5;
    private const int CmdDetails = 6;

    // Delegates must outlive the native window classes and hooks that point at them.
    private readonly WndProc _controllerProc;
    private readonly WndProc _bandProc;
    private readonly WndProc _panelProc;
    private readonly WinEventProc _winEventProc;
    private readonly IntPtr _instance;
    private readonly uint _taskbarCreatedMessage;
    private readonly HttpClient _http;
    private readonly ProviderPoller[] _pollers;
    private readonly Dictionary<ProviderKind, UsageHistory> _history = new()
    {
        [ProviderKind.Claude] = new UsageHistory(),
        [ProviderKind.Codex] = new UsageHistory(),
    };

    private readonly SpaceReserver _reserver = new();
    private readonly TaskbarProbe _probe = new();

    private IntPtr _hwnd;
    private TaskbarBand? _band;
    private DetailsPanel? _panel;
    private TrayFallback? _tray;
    private bool _lightTaskbar;
    private bool _makeRoom;
    private int _attachFailures;
    private DateTime _nextAttachAttempt = DateTime.MinValue;
    private string? _trayError;
    private int _ticks;

    // Placement state.
    private bool _arranging;
    private BandFit _fit = BandFit.Full;
    private string? _reportedFailure;
    private IntPtr _winEventHook;
    private uint _hookedPid;
    private DateTime _stripSince = DateTime.MinValue;
    private DateTime _lastSample = DateTime.MinValue;
    private int[]? _backing;
    private int[]? _candidate;

    public App()
    {
        _instance = GetModuleHandleW(null);
        _controllerProc = ControllerWndProc;
        _bandProc = BandWndProc;
        _panelProc = PanelWndProc;
        _winEventProc = OnWinEvent;
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
        var version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.2.0";
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WindowsUsageTracker", version));
        return http;
    }

    public int Run()
    {
        var icc = new INITCOMMONCONTROLSEX { dwSize = (uint)Marshal.SizeOf<INITCOMMONCONTROLSEX>(), dwICC = ICC_WIN95_CLASSES };
        InitCommonControlsEx(ref icc);

        if (!RegisterClass(ControllerClass, _controllerProc, 0)
            || !RegisterClass(TaskbarBand.WindowClass, _bandProc, 0)
            || !RegisterClass(DetailsPanel.WindowClass, _panelProc, 0))
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

        // Never leave the taskbar narrowed if we stop unexpectedly.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => _reserver.Restore();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => _reserver.Restore();

        _lightTaskbar = SystemSettings.TaskbarIsLight();
        _makeRoom = SystemSettings.MakeRoom();
        _band = new TaskbarBand(_instance, _hwnd);
        _band.ContextMenuRequested += ShowMenu;
        _band.Clicked += () => TogglePanel(null);
        _band.Lost += () => _nextAttachAttempt = DateTime.MinValue;
        _panel = new DetailsPanel(_instance, _hwnd);
        _tray = new TrayFallback(_hwnd);

        _probe.SetInterval(_makeRoom ? SettledProbeInterval : DodgeProbeInterval);
        _probe.Updated += () => PostMessageW(_hwnd, ProbeMessage, IntPtr.Zero, IntPtr.Zero);
        _probe.Start();

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

    private IntPtr PanelWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam) =>
        _panel is not null ? _panel.WndProc(hwnd, msg, wParam, lParam) : DefWindowProcW(hwnd, msg, wParam, lParam);

    private IntPtr ControllerWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (msg == _taskbarCreatedMessage && _taskbarCreatedMessage != 0)
            {
                Log.Info("Explorer recreated the taskbar; re-attaching");
                _band?.Destroy();
                _panel?.Hide();
                _tray?.Readd();
                _reserver.Reset();
                _reportedFailure = null;
                ResetBacking();
                Unhook();
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

                case ProbeMessage:
                    Arrange();
                    return IntPtr.Zero;

                case TrayFallback.CallbackMessage:
                    var mouse = (uint)(lParam.ToInt64() & 0xFFFF);
                    if (mouse is WM_RBUTTONUP or WM_CONTEXTMENU)
                    {
                        if (GetCursorPos(out var pt)) ShowMenu(pt);
                    }
                    else if (mouse == WM_LBUTTONUP && GetCursorPos(out var at))
                    {
                        TogglePanel(new RECT { Left = at.X, Top = at.Y, Right = at.X + 1, Bottom = at.Y + 1 });
                    }

                    return IntPtr.Zero;

                case WM_SETTINGCHANGE:
                    var area = lParam != IntPtr.Zero ? Marshal.PtrToStringUni(lParam) : null;
                    if (area == "ImmersiveColorSet")
                    {
                        _lightTaskbar = SystemSettings.TaskbarIsLight();
                        Log.Verbose($"Theme changed; light taskbar = {_lightTaskbar}");
                        ResetBacking();
                        PushContent();
                    }

                    _band?.UpdateLayout(force: true);
                    return IntPtr.Zero;

                case WM_DISPLAYCHANGE:
                case WM_DPICHANGED:
                    Log.Verbose("Display or DPI changed");
                    _band?.UpdateLayout(force: true);
                    Arrange();
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
            Arrange();
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
                if (_tray is { Visible: true, ShownForSpace: false })
                {
                    Log.Info("Embedded after earlier failures; removing the fallback tray icon");
                    _tray.Hide();
                    _trayError = null;
                }

                Hook(taskbar);
                PushContent();
                Arrange();
                _probe.ProbeSoon();
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

    /// <summary>Decides where the widget goes: a freed strip ("make room") or next to the tray ("dodge").</summary>
    private void Arrange()
    {
        if (_arranging || _band is null || !IsWindow(_band.Handle) || TaskbarGeometry.Read(_band.Taskbar) is not { } g) return;
        _arranging = true;
        try
        {
            var (full, _) = _band.Widths(g);

            if (_makeRoom && _reserver.Failure is null)
            {
                var pad = (int)Math.Round(8 * g.Scale);
                if (_reserver.Apply(g, full + 2 * pad) is { } strip)
                {
                    if (_probe.Latest is { } probe) _reserver.Verify(probe, strip, g.Scale);
                    if (_reserver.Failure is null)
                    {
                        if (_stripSince == DateTime.MinValue) _stripSince = DateTime.UtcNow;
                        SampleBacking(g, strip);
                        _fit = BandFit.Full;
                        HideSpaceTray();
                        _band.SetPlacement(strip, BandFit.Full, _backing);
                        _band.UpdateLayout(force: false);
                        return;
                    }
                }
            }

            _reserver.Restore();
            _stripSince = DateTime.MinValue;
            if (_makeRoom && _reserver.Failure is { } failure && failure != _reportedFailure)
            {
                _reportedFailure = failure;
                _probe.SetInterval(DodgeProbeInterval);
                PushContent();
            }

            Dodge(g);
        }
        finally
        {
            _arranging = false;
        }
    }

    private void Dodge(TaskbarGeometry g)
    {
        if (_band is null) return;
        var (full, compact) = _band.Widths(g);
        var fit = BandFit.Full;
        if (g.Horizontal && _probe.Latest is { Ok: true } probe && DateTime.UtcNow - probe.TakenAt < TimeSpan.FromSeconds(30))
        {
            var gap = (int)Math.Round(4 * g.Scale);
            var margin = (int)Math.Round(6 * g.Scale);
            var appsRight = probe.RightmostEndingBefore(g.TrayRect.Left + 1) ?? g.TaskbarRect.Left;
            var available = g.TrayRect.Left - gap - appsRight - margin;
            fit = UsageTracker.Core.Dodge.Decide(available, full, compact, _fit, (int)Math.Round(16 * g.Scale));
            if (fit != _fit) Log.Info($"Dodge: {available}px free between the app buttons and the tray; showing the widget {fit.ToString().ToLowerInvariant()}");
        }

        _fit = fit;
        if (fit == BandFit.Hidden)
        {
            _panel?.Hide();
            _tray?.ShowForSpace(Format.Summary(Snapshots()));
        }
        else
        {
            HideSpaceTray();
        }

        _band.SetPlacement(null, fit, null);
        _band.UpdateLayout(force: false);
    }

    private void HideSpaceTray()
    {
        if (_tray is { ShownForSpace: true }) _tray.Hide();
    }

    /// <summary>
    /// Windows 11 draws the taskbar's background (its tinted, blurred material and the thin line
    /// along the top) inside the content we narrowed, so the freed strip doesn't show it. Copy the
    /// taskbar's background from just left of the strip, one row at a time, and paint it behind
    /// the widget so the bar carries on without a seam. Re-sampled every couple of seconds so it
    /// follows theme, accent and wallpaper changes.
    /// </summary>
    private void SampleBacking(TaskbarGeometry g, RECT strip)
    {
        var now = DateTime.UtcNow;
        if (now - _stripSince < TimeSpan.FromSeconds(1) || now - _lastSample < TimeSpan.FromSeconds(2)) return;
        _lastSample = now;

        // The last few pixels of the taskbar content, past the clock: nothing but background there.
        var s = g.Scale;
        var width = Math.Max(2, (int)Math.Round(3 * s));
        var x = strip.Left - (int)Math.Round(2 * s) - width;
        var tb = g.TaskbarRect;
        if (ScreenSampler.Rows(x, tb.Top, width, tb.Height) is not { } rows) return;

        if (_backing is not null && ScreenSampler.Distance(rows, _backing) <= 2)
        {
            _candidate = null;
            return;
        }

        // Only switch after seeing the same thing twice, so a tooltip or hover highlight passing
        // over the sampled pixels doesn't get copied.
        if (_candidate is null || ScreenSampler.Distance(rows, _candidate) > 6)
        {
            _candidate = rows;
            return;
        }

        if (_backing is null)
        {
            Log.Info($"Make room: copying the taskbar's background behind the widget (top {Hex(rows[0])}, middle {Hex(rows[rows.Length / 2])}, bottom {Hex(rows[^1])})");
        }
        else
        {
            Log.Verbose($"Make room: taskbar background changed (middle {Hex(rows[rows.Length / 2])})");
        }

        _backing = rows;
        _candidate = null;
    }

    private void ResetBacking()
    {
        _backing = null;
        _candidate = null;
        _stripSince = DateTime.MinValue;
    }

    private static string Hex(int argb) => Hex(Color.FromArgb(argb));

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>Re-arranges the moment Explorer resizes its taskbar content, rather than up to a second later.</summary>
    private void Hook(IntPtr taskbar)
    {
        GetWindowThreadProcessId(taskbar, out var pid);
        if (pid == 0 || (pid == _hookedPid && _winEventHook != IntPtr.Zero)) return;
        Unhook();
        _winEventHook = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero, _winEventProc,
            pid, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        _hookedPid = _winEventHook != IntPtr.Zero ? pid : 0;
        if (_winEventHook == IntPtr.Zero) Log.Verbose("Couldn't watch the taskbar for resizes; relying on the 1-second check");
    }

    private void Unhook()
    {
        if (_winEventHook != IntPtr.Zero) UnhookWinEvent(_winEventHook);
        _winEventHook = IntPtr.Zero;
        _hookedPid = 0;
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            if (idObject != OBJID_WINDOW || idChild != 0 || hwnd == IntPtr.Zero) return;
            if (hwnd == _reserver.Content || (_band is not null && hwnd == _band.Taskbar)) Arrange();
        }
        catch (Exception ex)
        {
            Log.Error("Error handling a taskbar resize", ex);
        }
    }

    private void TogglePanel(RECT? anchor)
    {
        if (_panel is null || _band is null) return;
        var at = anchor ?? _band.ScreenRect;
        if (at.Width <= 0) return;
        PushContent();
        _panel.Toggle(at, _band.Taskbar != IntPtr.Zero ? _band.Taskbar : FindWindowW("Shell_TrayWnd", null));
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

    /// <summary>Sends the latest readings to the widget, the details panel and the tray icon.</summary>
    private void PushContent()
    {
        var now = DateTimeOffset.UtcNow;
        var zone = TimeZoneInfo.Local;
        var culture = CultureInfo.CurrentCulture;
        var snapshots = Snapshots();

        foreach (var s in snapshots)
        {
            if (s.Reading?.Session is { } session && s.ReadingFetchedAt is { } fetched) _history[s.Kind].Add(fetched, session.UsedPercent);
        }

        // Hide a provider that isn't signed in at all, unless that would leave nothing.
        var shown = snapshots.Where(s => s.Status is not (ProviderStatus.NotSignedIn or ProviderStatus.Unsupported)).ToArray();
        if (shown.Length == 0) shown = snapshots.ToArray();

        bool Stale(ProviderSnapshot s) => s.ReadingFetchedAt is not { } at || now - at > Format.StaleAfter;
        bool Attention(ProviderSnapshot s) => s.Status is ProviderStatus.TokenExpired or ProviderStatus.Unauthorized or ProviderStatus.BadResponse;

        var rows = shown.Select(s => new BandRow(s.Kind, s.DisplayName, s.Reading?.Session, s.Reading?.Weekly, Stale(s), Attention(s))).ToArray();

        var tooltip = Format.Tooltip(snapshots, now, zone, culture);
        if (_makeRoom && _reserver.Failure is not null)
        {
            tooltip += "\r\n\r\nCouldn't make room in the taskbar here, so the widget shrinks when apps need the space.";
        }

        tooltip += "\r\n\r\nClick for details, right-click for options.";
        _band?.SetContent(rows, tooltip, BandPalette.For(_lightTaskbar));

        var panel = shown.Select(s =>
        {
            var r = s.Reading;
            var detail = string.Join(" · ", new[]
            {
                r?.PlanLabel,
                s.ReadingFetchedAt is { } f ? "updated " + Format.Age(now - f) : null,
            }.Where(x => !string.IsNullOrEmpty(x)));
            string Text(UsageWindow? w) => w is null ? (r is null ? "no reading yet" : "not reported") : Format.Reset(w.ResetsAt, now, zone, culture);
            var message = s.Status == ProviderStatus.Pending ? "Loading…" : s.Status != ProviderStatus.Ok ? s.Message : null;
            return new PanelProvider(s.Kind, s.DisplayName, detail.Length == 0 ? null : detail,
                r?.Session, r?.Weekly,
                Pace.ElapsedFraction(r?.Session, Pace.SessionLength, now),
                Pace.ElapsedFraction(r?.Weekly, Pace.WeeklyLength, now),
                Text(r?.Session), Text(r?.Weekly),
                _history[s.Kind].Recent(now).ToArray(), now, message, Stale(s));
        }).ToArray();
        _panel?.SetContent(panel, _lightTaskbar);

        _tray?.UpdateTip(Format.Summary(snapshots));

        // The widget's width may have changed (e.g. a provider signed in); re-fit it.
        Arrange();
    }

    private void RefreshAll()
    {
        Log.Verbose("Manual refresh");
        foreach (var poller in _pollers) poller.RefreshNow();
    }

    private void ShowMenu(POINT at)
    {
        _panel?.Hide();
        var menu = CreatePopupMenu();
        try
        {
            var makeRoomLabel = _makeRoom && _reserver.Failure is not null
                ? "Make room in the taskbar (not working here)"
                : "Make room in the taskbar";

            AppendMenuW(menu, MF_STRING, (UIntPtr)CmdDetails, "Show details");
            AppendMenuW(menu, MF_STRING, (UIntPtr)CmdRefresh, "Refresh now");
            AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, null);
            AppendMenuW(menu, MF_STRING | (_makeRoom ? MF_CHECKED : 0), (UIntPtr)CmdMakeRoom, makeRoomLabel);
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
                case CmdDetails:
                    TogglePanel(_band is { IsShown: true } ? null : new RECT { Left = at.X, Top = at.Y, Right = at.X + 1, Bottom = at.Y + 1 });
                    break;
                case CmdRefresh:
                    RefreshAll();
                    break;
                case CmdMakeRoom:
                    SetMakeRoom(!_makeRoom);
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

    /// <summary>Turns "make room" on (retrying even after an earlier failure) or off.</summary>
    private void SetMakeRoom(bool enabled)
    {
        _makeRoom = enabled;
        SystemSettings.SetMakeRoom(enabled);
        _reserver.Reset();
        _reportedFailure = null;
        ResetBacking();
        _fit = BandFit.Full;
        _probe.SetInterval(enabled ? SettledProbeInterval : DodgeProbeInterval);
        _probe.ProbeSoon();
        PushContent();
        Arrange();
    }

    private void Shutdown()
    {
        Log.Info("Exiting");
        KillTimer(_hwnd, LayoutTimer);
        Unhook();
        _reserver.Restore();
        _panel?.Dispose();
        _band?.Destroy();
        _tray?.Hide();
        if (_hwnd != IntPtr.Zero && IsWindow(_hwnd)) DestroyWindow(_hwnd);
    }

    public void Dispose()
    {
        _probe.Dispose();
        foreach (var poller in _pollers) poller.Dispose();
        _reserver.Restore();
        _band?.Dispose();
        _panel?.Dispose();
        _tray?.Dispose();
        _http.Dispose();
    }
}
