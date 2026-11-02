using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace CfVsClaude.Services;

/// <summary>
/// Brings an existing Claude Code session for the repository to the front, or starts one in the Claude desktop
/// app or a terminal, depending on <see cref="Settings.LaunchTarget"/>.
/// Every failure is reported as a toast; nothing here throws to the caller.
/// </summary>
internal sealed class ClaudeLauncherService(ProcessDiscoveryService discovery, Notifier notifier, Settings settings)
{
    /// <summary>Call on the UI thread: notifications and SetForegroundWindow need it.</summary>
    /// <param name="activeFile">The file active in VS; a new desktop session gets it prefilled as an @mention.</param>
    public async Task LaunchOrActivateAsync(string solutionDirectory, string solutionName, string? activeFile = null)
    {
        string repo = FindRepositoryRoot(solutionDirectory);
        string displayName = solutionName.Length > 0 ? solutionName : Path.GetFileName(repo);
        string title = $"Claude – {displayName}";

        LaunchTarget target = settings.LaunchTarget;
        if (target == LaunchTarget.DesktopApp && !IsDesktopAppInstalled())
        {
            notifier.Info("Claude desktop app not found", "Opening Claude Code in Windows Terminal instead.");
            target = LaunchTarget.WindowsTerminal;
        }
        var host = target == LaunchTarget.DesktopApp ? SessionHost.DesktopApp : SessionHost.Terminal;

        // Jump straight into the newest Code session the desktop app has recorded for this repository.
        if (target == LaunchTarget.DesktopApp && settings.OpenRecentSession)
        {
            DesktopSession? recent = null;
            try
            {
                recent = await Task.Run(() => DesktopSessionStore.FindMostRecent(repo));
            }
            catch
            {
                // Best effort; fall through to the default behaviour below.
            }
            if (recent is not null && OpenDesktopLink("claude://code/continue?session=" + Uri.EscapeDataString(recent.SessionId)))
                return;
        }

        IReadOnlyList<ClaudeSession> sessions;
        try
        {
            var found = await Task.Run(() => discovery.FindSessions(repo, title));
            sessions = found.Where(s => s.Host == host).ToList();
        }
        catch
        {
            sessions = []; // discovery is best effort; the worst case is a second session
        }

        if (sessions.Count > 0)
        {
            if (sessions.Any(s => BringToFront(s.HostWindow))) return;
            // The desktop app may be closed to the tray; starting it again restores its window.
            if (host == SessionHost.DesktopApp && TryStartDesktopApp()) return;

            notifier.Warning("Claude Code is already running",
                $"A session for {displayName} is running (PID {sessions[0].ProcessId}), but its window couldn't be brought to the front.");
            return;
        }

        if (target == LaunchTarget.DesktopApp)
            OpenInDesktopApp(repo, activeFile);
        else
            StartInTerminal(repo, title, target);
    }

    // ---------------------------------------------------------------- desktop app

    private const string DesktopAlias = "claude-desktop.exe";

    // Opens a new Code session with this working directory; the app asks the user to confirm the folder.
    private void OpenInDesktopApp(string repo, string? activeFile)
    {
        string link = "claude://code/new?folder=" + Uri.EscapeDataString(repo);
        // Disabled: prefill the prompt box with an @mention of the active VS file, ready for the user's question.
        // string? mention = FileMention(repo, activeFile);
        // if (mention is not null) link += "&q=" + Uri.EscapeDataString(mention);
        OpenDesktopLink(link);
    }

    /// <summary>
    /// "@src/Foo.cs " for a file inside the repository, or null. Paths containing whitespace are skipped,
    /// since an @mention ends at the first space.
    /// </summary>
    internal static string? FileMention(string repo, string? file)
    {
        if (string.IsNullOrEmpty(file)) return null;
        string full = Path.GetFullPath(file);
        string? dir = Path.GetDirectoryName(full);
        if (dir is null || !ProcessDiscoveryService.IsSameOrUnder(ProcessDiscoveryService.NormalizeDirectory(dir), repo))
            return null;

        string relative = Path.GetRelativePath(repo, full).Replace('\\', '/');
        return relative.Any(char.IsWhiteSpace) ? null : "@" + relative + " ";
    }

    private bool OpenDesktopLink(string link)
    {
        try
        {
            Process.Start(new ProcessStartInfo(link) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            notifier.Error("Couldn't open the Claude desktop app", ex.Message);
            return false;
        }

        // A running instance receives the link in the background; raise its window so the result is visible.
        IntPtr window = ProcessDiscoveryService.FindDesktopAppWindow();
        if (window != IntPtr.Zero) BringToFront(window);
        return true;
    }

    private static bool IsDesktopAppInstalled()
    {
        using var protocol = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey("claude");
        return protocol is not null;
    }

    private static bool TryStartDesktopApp()
    {
        string alias = Path.Combine(WindowsAppsFolder, DesktopAlias);
        if (!File.Exists(alias)) return false;
        try
        {
            Process.Start(new ProcessStartInfo(alias) { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- terminal

    private void StartInTerminal(string repo, string title, LaunchTarget target)
    {
        string command = string.IsNullOrWhiteSpace(settings.ClaudeCommand) ? "claude" : settings.ClaudeCommand.Trim();
        string path = GetCurrentPath();
        string executable = FirstToken(command);
        if (!IsResolvable(executable, path))
        {
            notifier.Error("Claude Code not found",
                $"\"{executable}\" isn't on your PATH. Install Claude Code, then click the button again.");
            return;
        }

        try
        {
            bool wantTerminal = target == LaunchTarget.WindowsTerminal;
            string? wt = wantTerminal ? FindWindowsTerminal() : null;
            if (wantTerminal && wt is null)
                notifier.Info("Windows Terminal not found", "Opening Claude Code in a console window instead.");

            var psi = wt is not null
                ? ForWindowsTerminal(wt, repo, title, WindowName(repo), command)
                : ForConsole(repo, title, command);
            psi.Environment["PATH"] = path; // pick up installs made after the overlay started
            Process.Start(psi)?.Dispose();
        }
        catch (Exception ex)
        {
            notifier.Error("Couldn't start Claude Code", ex.Message);
        }
    }

    // ---------------------------------------------------------------- activation

    private static bool BringToFront(IntPtr hwnd)
    {
        if (!Native.IsWindow(hwnd)) return false;
        if (Native.IsIconic(hwnd)) Native.ShowWindow(hwnd, Native.SW_RESTORE);
        if (Native.SetForegroundWindow(hwnd)) return true;

        // The foreground lock can refuse us once input has moved elsewhere; borrow the foreground thread's input state.
        uint foregroundThread = Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out _);
        uint self = Native.GetCurrentThreadId();
        bool attached = foregroundThread != 0 && foregroundThread != self
                        && Native.AttachThreadInput(self, foregroundThread, true);
        try
        {
            Native.BringWindowToTop(hwnd);
            return Native.SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached) Native.AttachThreadInput(self, foregroundThread, false);
        }
    }

    // ---------------------------------------------------------------- start info

    internal static ProcessStartInfo ForWindowsTerminal(string wt, string directory, string title, string windowName, string command)
    {
        var psi = new ProcessStartInfo(wt) { UseShellExecute = false, WorkingDirectory = directory };
        // One named window per repository; a locked tab title keeps it findable while Claude Code runs.
        // cmd /k resolves claude.cmd / claude.exe and keeps the tab open if claude exits.
        // wt treats ';' as a sub-command separator, so it is escaped in everything passed through.
        foreach (string arg in new[] { "-w", windowName, "new-tab", "--title", title, "--suppressApplicationTitle",
                     "-d", NormalizeDir(directory), "cmd.exe", "/k", command })
        {
            psi.ArgumentList.Add(arg.Replace(";", "\\;"));
        }
        return psi;
    }

    internal static ProcessStartInfo ForConsole(string directory, string title, string command) =>
        // A console program started from a GUI process without a console gets a new console window.
        new("cmd.exe")
        {
            UseShellExecute = false,
            WorkingDirectory = directory,
            Arguments = $"/k title {EscapeCmd(title)} & {command}",
        };

    internal static string WindowName(string repo)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(repo.ToUpperInvariant()));
        return "claude-" + Convert.ToHexStringLower(hash, 0, 4);
    }

    internal static string EscapeCmd(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (c is '^' or '&' or '|' or '<' or '>' or '%') sb.Append('^');
            if (c != '"') sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>A trailing backslash would escape the closing quote when wt re-parses the argument.</summary>
    internal static string NormalizeDir(string dir)
    {
        string trimmed = dir.TrimEnd('\\');
        return trimmed.EndsWith(':') ? trimmed + "\\." : trimmed;
    }

    // ---------------------------------------------------------------- lookup

    /// <summary>The nearest folder containing .git (a directory, or a file for worktrees); else the solution folder.</summary>
    internal static string FindRepositoryRoot(string solutionDirectory)
    {
        string start = ProcessDiscoveryService.NormalizeDirectory(solutionDirectory);
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            string git = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(git) || File.Exists(git))
                return ProcessDiscoveryService.NormalizeDirectory(dir.FullName);
        }
        return start;
    }

    // App execution aliases (wt.exe, claude-desktop.exe) live here; File.Exists works on their reparse points.
    private static readonly string WindowsAppsFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps");

    private static string? FindWindowsTerminal()
    {
        string alias = Path.Combine(WindowsAppsFolder, "wt.exe");
        return File.Exists(alias) ? alias : null;
    }

    /// <summary>PATH as currently stored in the registry, so installs made after the overlay started are seen.</summary>
    private static string GetCurrentPath()
    {
        string machine = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? string.Empty;
        string user = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? string.Empty;
        string combined = string.Join(';', new[] { machine, user }.Where(s => s.Length > 0));
        return combined.Length > 0 ? combined : Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
    }

    internal static string FirstToken(string command)
    {
        if (command.StartsWith('"'))
        {
            int end = command.IndexOf('"', 1);
            return end > 0 ? command[1..end] : command.Trim('"');
        }
        int space = command.IndexOf(' ');
        return space > 0 ? command[..space] : command;
    }

    internal static bool IsResolvable(string executable, string path)
    {
        string[] extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries);

        bool Exists(string candidate) =>
            (Path.HasExtension(candidate) && File.Exists(candidate))
            || extensions.Any(ext => File.Exists(candidate + ext));

        if (Path.IsPathRooted(executable)) return Exists(executable);

        foreach (string dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                if (Exists(Path.Combine(dir, executable))) return true;
            }
            catch (ArgumentException)
            {
                // Malformed PATH entry.
            }
        }
        return false;
    }
}
