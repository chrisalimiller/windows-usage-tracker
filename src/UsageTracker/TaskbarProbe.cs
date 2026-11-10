using System.Runtime.InteropServices;
using UsageTracker.Native;
using static UsageTracker.Native.Win32;

namespace UsageTracker;

/// <summary>The taskbar's buttons as UI Automation reported them at one moment.</summary>
internal sealed record ProbeResult(DateTime TakenAt, IntPtr Content, IReadOnlyList<RECT> Buttons, string? Error)
{
    public bool Ok => Error is null && Buttons.Count > 0;

    /// <summary>The right edge of the rightmost button that ends before <paramref name="limit"/>, or null.</summary>
    public int? RightmostEndingBefore(int limit)
    {
        int? best = null;
        foreach (var b in Buttons)
        {
            if (b.Right <= limit && (best is null || b.Right > best)) best = b.Right;
        }

        return best;
    }

    public int? RightmostEdge => Buttons.Count == 0 ? null : Buttons.Max(b => b.Right);
}

/// <summary>
/// Reads where the taskbar's own buttons (Start, apps, tray icons, clock) are, using UI
/// Automation on a background thread so a slow or busy Explorer never stalls the widget.
/// </summary>
internal sealed class TaskbarProbe : IDisposable
{
    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private volatile bool _stop;
    private volatile ProbeResult? _latest;
    private int _intervalMs = 2000;
    private bool _loggedFirst;

    public TaskbarProbe()
    {
        // UI Automation clients should not run on a thread that owns windows.
        _thread = new Thread(Run) { IsBackground = true, Name = "TaskbarProbe" };
        _thread.SetApartmentState(ApartmentState.MTA);
    }

    public ProbeResult? Latest => _latest;

    /// <summary>Raised on the probe thread after each reading.</summary>
    public event Action? Updated;

    public void Start() => _thread.Start();

    /// <summary>How often to look; shorter while the widget is dodging app buttons.</summary>
    public void SetInterval(TimeSpan interval) => Interlocked.Exchange(ref _intervalMs, (int)interval.TotalMilliseconds);

    public void ProbeSoon() => _wake.Set();

    private void Run()
    {
        IUIAutomation? uia = null;
        IUIAutomationCondition? buttons = null;
        while (!_stop)
        {
            try
            {
                uia ??= (IUIAutomation)new CUIAutomation();
                buttons ??= uia.CreatePropertyCondition(Uia.ControlTypePropertyId, Uia.ButtonControlTypeId);
                _latest = Probe(uia, buttons);
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidComObjectException or ArgumentException)
            {
                _latest = new ProbeResult(DateTime.UtcNow, IntPtr.Zero, Array.Empty<RECT>(), ex.Message);
                Log.Verbose($"Taskbar probe failed: {ex.GetType().Name}: {ex.Message}");
            }

            Updated?.Invoke();
            _wake.WaitOne(Volatile.Read(ref _intervalMs));
        }
    }

    private ProbeResult Probe(IUIAutomation uia, IUIAutomationCondition condition)
    {
        var taskbar = FindWindowW("Shell_TrayWnd", null);
        var content = TaskbarGeometry.FindContent(taskbar);
        var root = content != IntPtr.Zero ? content : taskbar;
        if (root == IntPtr.Zero) return new ProbeResult(DateTime.UtcNow, IntPtr.Zero, Array.Empty<RECT>(), "no taskbar");

        var rects = new List<RECT>();
        var element = uia.ElementFromHandle(root);
        IUIAutomationElementArray? found = null;
        try
        {
            found = element.FindAll(Uia.TreeScopeDescendants, condition);
            var count = found?.get_Length() ?? 0;
            for (var i = 0; i < count; i++)
            {
                var button = found!.GetElement(i);
                try
                {
                    if (button.GetCurrentPropertyValue(Uia.BoundingRectanglePropertyId) is double[] { Length: 4 } r && r[2] >= 1 && r[3] >= 1)
                    {
                        rects.Add(new RECT
                        {
                            Left = (int)Math.Round(r[0]),
                            Top = (int)Math.Round(r[1]),
                            Right = (int)Math.Round(r[0] + r[2]),
                            Bottom = (int)Math.Round(r[1] + r[3]),
                        });
                    }

                    if (!_loggedFirst && Log.VerboseEnabled)
                    {
                        var cls = button.GetCurrentPropertyValue(Uia.ClassNamePropertyId) as string;
                        Log.Verbose($"  taskbar button {cls} {(rects.Count > 0 ? rects[^1].ToString() : "?")}");
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(button);
                }
            }
        }
        finally
        {
            if (found is not null) Marshal.ReleaseComObject(found);
            Marshal.ReleaseComObject(element);
        }

        if (!_loggedFirst)
        {
            Log.Info($"Taskbar probe: found {rects.Count} buttons in 0x{root.ToInt64():X} ({(content != IntPtr.Zero ? "XAML content" : "taskbar")})");
            _loggedFirst = true;
        }

        return new ProbeResult(DateTime.UtcNow, root, rects, rects.Count == 0 ? "no buttons found" : null);
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        if (_thread.IsAlive) _thread.Join(TimeSpan.FromSeconds(2));
        _wake.Dispose();
    }
}
