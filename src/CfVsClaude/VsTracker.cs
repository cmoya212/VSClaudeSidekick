using System.Diagnostics;

namespace CfVsClaude;

/// <summary>
/// Tracks the Visual Studio main window the overlay belongs to, using out-of-context WinEvent hooks.
/// Must be created on a thread with a message loop (the WinForms UI thread); callbacks arrive there.
/// </summary>
internal sealed class VsTracker : IDisposable
{
    private readonly Native.WinEventDelegate _callback; // held to keep the delegate alive for native code
    private readonly IntPtr _foregroundHook;
    private readonly List<IntPtr> _processHooks = [];
    private readonly uint _ownPid = (uint)Environment.ProcessId;
    private readonly System.Windows.Forms.Timer _watchdog;

    public IntPtr Target { get; private set; }
    public uint TargetPid { get; private set; }

    /// <summary>True while a window belonging to the target VS process is in the foreground.</summary>
    public bool IsActive { get; private set; }

    /// <summary>Raised when the target, its bounds, title, or activation state changes.</summary>
    public event Action? Changed;

    public VsTracker()
    {
        _callback = OnWinEvent;
        _foregroundHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _callback, 0, 0, Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);

        _watchdog = new System.Windows.Forms.Timer { Interval = 1000 };
        _watchdog.Tick += (_, _) => Validate();
        _watchdog.Start();

        Validate();
        OnForeground(Native.GetForegroundWindow());
    }

    public string SolutionName => Target == IntPtr.Zero ? string.Empty : ParseSolutionName(Native.GetWindowText(Target));

    /// <summary>"MySolution - File.cs - Microsoft Visual Studio" / "MySolution - Microsoft Visual Studio (Administrator)"</summary>
    internal static string ParseSolutionName(string title)
    {
        int i = title.IndexOf(" - ", StringComparison.Ordinal);
        string name = i > 0 ? title[..i] : title;
        return name.StartsWith("Microsoft Visual Studio", StringComparison.Ordinal) ? string.Empty : name.Trim();
    }

    public bool IsTargetDisplayable =>
        Target != IntPtr.Zero
        && Native.IsWindow(Target)
        && Native.IsWindowVisible(Target)
        && !Native.IsIconic(Target)
        && !Native.IsCloaked(Target); // cloaked = on another virtual desktop

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        switch (eventType)
        {
            case Native.EVENT_SYSTEM_FOREGROUND:
                OnForeground(hwnd);
                break;

            case Native.EVENT_OBJECT_LOCATIONCHANGE:
            case Native.EVENT_OBJECT_NAMECHANGE:
            case Native.EVENT_OBJECT_SHOW:
            case Native.EVENT_OBJECT_HIDE:
            case Native.EVENT_SYSTEM_MINIMIZESTART:
            case Native.EVENT_SYSTEM_MINIMIZEEND:
                if (hwnd == Target && idObject == Native.OBJID_WINDOW)
                    Changed?.Invoke();
                break;

            case Native.EVENT_OBJECT_DESTROY:
                if (hwnd == Target && idObject == Native.OBJID_WINDOW)
                {
                    SetTarget(IntPtr.Zero, 0);
                    Validate();
                }
                break;
        }
    }

    private void OnForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        Native.GetWindowThreadProcessId(hwnd, out uint pid);

        // Our own menus/dialogs taking focus should not hide the overlay.
        if (pid == _ownPid) return;

        bool active = IsVisualStudio(pid);
        if (active)
        {
            // Dialogs and floating tool windows are owned by the VS main window.
            IntPtr root = Native.GetAncestor(hwnd, Native.GA_ROOTOWNER);
            SetTarget(root != IntPtr.Zero ? root : hwnd, pid);
        }

        if (active != IsActive)
        {
            IsActive = active;
            Changed?.Invoke();
        }
    }

    /// <summary>Drops a dead target and adopts any running VS instance if none is tracked.</summary>
    private void Validate()
    {
        if (Target != IntPtr.Zero && !Native.IsWindow(Target))
            SetTarget(IntPtr.Zero, 0);

        if (Target == IntPtr.Zero)
        {
            foreach (var p in Process.GetProcessesByName("devenv"))
            {
                using (p)
                {
                    if (p.MainWindowHandle != IntPtr.Zero)
                    {
                        SetTarget(p.MainWindowHandle, (uint)p.Id);
                        IsActive = Native.GetForegroundWindow() == p.MainWindowHandle;
                        break;
                    }
                }
            }
        }

        // Catch states the hooks can miss (e.g. virtual-desktop switches).
        Changed?.Invoke();
    }

    private void SetTarget(IntPtr hwnd, uint pid)
    {
        if (hwnd == Target) return;

        if (pid != TargetPid)
        {
            UnhookProcess();
            if (pid != 0) HookProcess(pid);
        }

        Target = hwnd;
        TargetPid = pid;
        Changed?.Invoke();
    }

    private void HookProcess(uint pid)
    {
        uint[] events =
        [
            Native.EVENT_OBJECT_LOCATIONCHANGE, Native.EVENT_OBJECT_NAMECHANGE,
            Native.EVENT_OBJECT_DESTROY, Native.EVENT_OBJECT_SHOW, Native.EVENT_OBJECT_HIDE,
        ];
        foreach (uint e in events)
            _processHooks.Add(Native.SetWinEventHook(e, e, IntPtr.Zero, _callback, pid, 0, Native.WINEVENT_OUTOFCONTEXT));

        _processHooks.Add(Native.SetWinEventHook(Native.EVENT_SYSTEM_MINIMIZESTART, Native.EVENT_SYSTEM_MINIMIZEEND,
            IntPtr.Zero, _callback, pid, 0, Native.WINEVENT_OUTOFCONTEXT));
    }

    private void UnhookProcess()
    {
        foreach (var h in _processHooks)
            if (h != IntPtr.Zero) Native.UnhookWinEvent(h);
        _processHooks.Clear();
    }

    private static bool IsVisualStudio(uint pid)
    {
        // Foreground changes are infrequent, so no caching (which would also have to cope with PID reuse).
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return string.Equals(p.ProcessName, "devenv", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _watchdog.Dispose();
        UnhookProcess();
        if (_foregroundHook != IntPtr.Zero) Native.UnhookWinEvent(_foregroundHook);
    }
}
