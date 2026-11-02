using System.Diagnostics;
using Microsoft.Win32.SafeHandles;

namespace CfVsClaude.Services;

internal enum SessionHost
{
    /// <summary>The CLI in a console or Windows Terminal window.</summary>
    Terminal,

    /// <summary>A Code session run by the Claude desktop app.</summary>
    DesktopApp,
}

/// <summary>
/// A running Claude Code process and the top-level window to bring forward for it: its console or terminal
/// window, or the desktop app's main window (zero if that is hidden, e.g. closed to the tray).
/// </summary>
internal sealed record ClaudeSession(int ProcessId, string WorkingDirectory, SessionHost Host, IntPtr HostWindow);

/// <summary>
/// Finds Claude Code processes whose working directory is inside a repository, and the window that hosts them.
/// The working directory and command line are read from the target's PEB, since Windows has no API for them.
/// </summary>
internal sealed class ProcessDiscoveryService
{
    private const string TerminalWindowClass = "CASCADIA_HOSTING_WINDOW_CLASS";
    private const string ElectronWindowClass = "Chrome_WidgetWin_1";

    // x64 offsets: PEB.ProcessParameters, RTL_USER_PROCESS_PARAMETERS.CurrentDirectory.DosPath / .CommandLine
    private const int PebProcessParametersOffset = 0x20;
    private const int ParamsCurrentDirectoryOffset = 0x38;
    private const int ParamsCommandLineOffset = 0x70;

    // AttachConsole is per process, so console probing must be serialized.
    private static readonly Lock ConsoleLock = new();

    public IReadOnlyList<ClaudeSession> FindSessions(string repositoryRoot, string preferredTitle)
    {
        string root = NormalizeDirectory(repositoryRoot);
        var sessions = new List<ClaudeSession>();

        // Native installer: claude.exe. npm installer: node.exe running @anthropic-ai/claude-code.
        foreach (var process in Process.GetProcessesByName("claude").Concat(Process.GetProcessesByName("node")))
        {
            using (process)
            {
                var info = ReadProcessInfo(process.Id);
                if (info is null) continue; // exited, elevated, or 32-bit

                bool isNode = process.ProcessName.Equals("node", StringComparison.OrdinalIgnoreCase);
                if (isNode && !info.Value.CommandLine.Contains("claude-code", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!IsSameOrUnder(NormalizeDirectory(info.Value.CurrentDirectory), root)) continue;

                IntPtr host = FindHostWindow(process.Id, preferredTitle);
                if (host != IntPtr.Zero)
                {
                    sessions.Add(new ClaudeSession(process.Id, info.Value.CurrentDirectory, SessionHost.Terminal, host));
                }
                else if (FindAncestor(process.Id, IsDesktopAppProcess) is not null)
                {
                    sessions.Add(new ClaudeSession(process.Id, info.Value.CurrentDirectory, SessionHost.DesktopApp,
                        FindDesktopAppWindow()));
                }
                // Anything else (e.g. IDE integrations without a window) is not a session the user can switch to.
            }
        }
        return sessions;
    }

    /// <summary>The Claude desktop app's main window, or zero if it is not running or hidden.</summary>
    public static IntPtr FindDesktopAppWindow()
    {
        IntPtr found = IntPtr.Zero;
        // EnumWindows walks in z-order, so the first match is the most recently active one.
        Native.EnumWindows((hwnd, _) =>
        {
            if (!Native.IsWindowVisible(hwnd) || Native.GetWindow(hwnd, Native.GW_OWNER) != IntPtr.Zero
                || Native.GetClassName(hwnd) != ElectronWindowClass || Native.GetWindowTextLength(hwnd) == 0)
                return true;

            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            if (!IsDesktopAppProcess((int)pid)) return true;

            found = hwnd;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>True for the desktop app's Claude.exe (MSIX install, or the older per-user installer).</summary>
    private static bool IsDesktopAppProcess(int pid)
    {
        string? image = GetImagePath(pid);
        return image is not null
               && (image.Contains(@"\WindowsApps\Claude_", StringComparison.OrdinalIgnoreCase)
                   || image.Contains(@"\AnthropicClaude\", StringComparison.OrdinalIgnoreCase));
    }

    private static string? GetImagePath(int pid)
    {
        using SafeProcessHandle h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (h.IsInvalid) return null;
        var buf = new char[1024];
        int size = buf.Length;
        return Native.QueryFullProcessImageName(h, 0, buf, ref size) ? new string(buf, 0, size) : null;
    }

    // ---------------------------------------------------------------- host window

    private static IntPtr FindHostWindow(int pid, string preferredTitle)
    {
        IntPtr console = GetConsoleWindowOf(pid);
        if (console != IntPtr.Zero)
        {
            if (Native.IsWindowVisible(console)) return console; // classic console host

            // Under Windows Terminal the console window is a hidden ConPTY pseudo-window owned by the terminal window.
            IntPtr owner = Native.GetWindow(console, Native.GW_OWNER);
            if (owner != IntPtr.Zero) return owner;
        }

        // Older terminals don't set an owner: find the Windows Terminal ancestor and pick one of its windows.
        int? terminalPid = FindAncestor(pid, IsWindowsTerminalProcess);
        return terminalPid is null ? IntPtr.Zero : FindTerminalWindow((uint)terminalPid.Value, preferredTitle);
    }

    private static IntPtr GetConsoleWindowOf(int pid)
    {
        lock (ConsoleLock)
        {
            if (!Native.AttachConsole((uint)pid)) return IntPtr.Zero;
            try
            {
                // While attached, Ctrl+C in that console would otherwise terminate us.
                Native.SetConsoleCtrlHandler(IntPtr.Zero, true);
                return Native.GetConsoleWindow();
            }
            finally
            {
                Native.FreeConsole();
            }
        }
    }

    private static IntPtr FindTerminalWindow(uint terminalPid, string preferredTitle)
    {
        IntPtr first = IntPtr.Zero, titled = IntPtr.Zero;
        // EnumWindows walks in z-order, so "first" is the most recently active terminal window.
        Native.EnumWindows((hwnd, _) =>
        {
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid != terminalPid || !Native.IsWindowVisible(hwnd) || Native.GetClassName(hwnd) != TerminalWindowClass)
                return true;

            if (first == IntPtr.Zero) first = hwnd;
            if (Native.GetWindowText(hwnd).StartsWith(preferredTitle, StringComparison.Ordinal))
            {
                titled = hwnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return titled != IntPtr.Zero ? titled : first;
    }

    private static bool IsWindowsTerminalProcess(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName.Equals("WindowsTerminal", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false; // already exited
        }
    }

    private static int? FindAncestor(int pid, Func<int, bool> match)
    {
        for (int depth = 0; depth < 8; depth++)
        {
            int? parent = ReadProcessInfo(pid, readStrings: false)?.ParentId;
            if (parent is null or 0) return null;
            if (match(parent.Value)) return parent;
            pid = parent.Value;
        }
        return null;
    }

    // ---------------------------------------------------------------- PEB reading

    private readonly record struct ProcessInfo(string CurrentDirectory, string CommandLine, int ParentId);

    private static unsafe ProcessInfo? ReadProcessInfo(int pid, bool readStrings = true)
    {
        uint access = Native.PROCESS_QUERY_LIMITED_INFORMATION | (readStrings ? Native.PROCESS_VM_READ : 0);
        using SafeProcessHandle h = Native.OpenProcess(access, false, (uint)pid);
        if (h.IsInvalid) return null;

        if (Native.NtQueryInformationProcess(h, Native.ProcessBasicInformation, out var pbi,
                sizeof(Native.PROCESS_BASIC_INFORMATION), out _) != 0)
            return null;

        int parentId = (int)pbi.InheritedFromUniqueProcessId;
        if (!readStrings) return new ProcessInfo(string.Empty, string.Empty, parentId);

        // The offsets above are for 64-bit processes only.
        if (!Native.IsWow64Process(h, out bool wow64) || wow64) return null;

        if (!Read(h, pbi.PebBaseAddress + PebProcessParametersOffset, out IntPtr parameters) || parameters == IntPtr.Zero)
            return null;

        string? cwd = ReadUnicodeString(h, parameters + ParamsCurrentDirectoryOffset);
        string? commandLine = ReadUnicodeString(h, parameters + ParamsCommandLineOffset);
        return cwd is null || commandLine is null ? null : new ProcessInfo(cwd, commandLine, parentId);
    }

    private static unsafe bool Read<T>(SafeProcessHandle h, IntPtr address, out T value) where T : unmanaged
    {
        T v;
        bool ok = Native.ReadProcessMemory(h, address, &v, sizeof(T), out nint read) && read == sizeof(T);
        value = v;
        return ok;
    }

    private static unsafe string? ReadUnicodeString(SafeProcessHandle h, IntPtr address)
    {
        if (!Read(h, address, out Native.UNICODE_STRING us)) return null;
        if (us.Length == 0 || us.Buffer == IntPtr.Zero) return string.Empty;

        var chars = new char[us.Length / 2];
        fixed (char* p = chars)
        {
            if (!Native.ReadProcessMemory(h, us.Buffer, p, us.Length, out nint read) || read != us.Length) return null;
        }
        return new string(chars);
    }

    // ---------------------------------------------------------------- paths

    public static string NormalizeDirectory(string path)
    {
        try { path = Path.GetFullPath(path); } catch { /* keep as-is */ }
        string trimmed = path.TrimEnd('\\', '/');
        // A bare "C:" means the current directory on drive C, so a drive root keeps its backslash.
        return trimmed.Length == 2 && trimmed[1] == ':' ? trimmed + "\\" : trimmed;
    }

    public static bool IsSameOrUnder(string dir, string root) =>
        dir.Equals(root, StringComparison.OrdinalIgnoreCase)
        || dir.StartsWith(root.EndsWith('\\') ? root : root + "\\", StringComparison.OrdinalIgnoreCase);
}
