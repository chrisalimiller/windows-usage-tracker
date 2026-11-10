using System.Runtime.InteropServices;
using UsageTracker.Native;
using static UsageTracker.Native.Win32;

namespace UsageTracker;

/// <summary>
/// A notification-area icon shown only when the widget can't be in the taskbar: either
/// embedding failed (so the failure is visible instead of silent), or the app buttons left
/// no room and the widget stepped aside. Its tooltip carries the reason and the usage.
/// </summary>
internal sealed class TrayFallback : IDisposable
{
    public const uint CallbackMessage = WM_APP + 2;
    private const uint IconId = 1;

    private readonly IntPtr _owner;
    private bool _visible;
    private bool _forSpace;
    private string _tip = string.Empty;

    public TrayFallback(IntPtr owner)
    {
        _owner = owner;
    }

    public bool Visible => _visible;

    /// <summary>True when the icon stands in for a widget that stepped aside for app buttons.</summary>
    public bool ShownForSpace => _visible && _forSpace;

    /// <summary>Shows the icon quietly (no balloon) while the app buttons leave no room for the widget.</summary>
    public void ShowForSpace(string summary)
    {
        if (_visible) return;
        _forSpace = true;
        _tip = Tip(summary);
        var data = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP);
        _visible = Shell_NotifyIconW(NIM_ADD, ref data);
        if (!_visible) Log.Error($"Couldn't add the tray icon (error {Marshal.GetLastWin32Error()})");
    }

    /// <summary>Shows the icon (once) with a balloon explaining the error.</summary>
    public void Show(string error, string summary)
    {
        if (_visible && _forSpace) Hide();
        _forSpace = false;
        _tip = Tip(summary);
        var data = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_INFO);
        data.szInfoTitle = "Usage Tracker isn't in the taskbar";
        data.szInfo = Truncate(error + " Right-click this icon for options; details are in the log.", 255);
        data.dwInfoFlags = NIIF_WARNING;

        if (_visible)
        {
            Shell_NotifyIconW(NIM_MODIFY, ref data);
            return;
        }

        _visible = Shell_NotifyIconW(NIM_ADD, ref data);
        if (!_visible) Log.Error($"Couldn't add the fallback tray icon (error {Marshal.GetLastWin32Error()})");
    }

    public void UpdateTip(string summary)
    {
        if (!_visible) return;
        var tip = Tip(summary);
        if (tip == _tip) return;
        _tip = tip;
        var data = Data(NIF_TIP);
        Shell_NotifyIconW(NIM_MODIFY, ref data);
    }

    /// <summary>Explorer forgets tray icons when it restarts; add ours again.</summary>
    public void Readd()
    {
        if (!_visible) return;
        var data = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP);
        _visible = Shell_NotifyIconW(NIM_ADD, ref data);
    }

    public void Hide()
    {
        if (!_visible) return;
        var data = Data(0);
        Shell_NotifyIconW(NIM_DELETE, ref data);
        _visible = false;
    }

    private NOTIFYICONDATA Data(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _owner,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = LoadIconW(IntPtr.Zero, _forSpace ? IDI_INFORMATION : IDI_WARNING),
        szTip = _tip,
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    private string Tip(string summary) => Truncate((_forSpace ? "Usage Tracker (no room in taskbar) - " : "Usage Tracker (not embedded) - ") + summary, 127);

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    public void Dispose() => Hide();
}
