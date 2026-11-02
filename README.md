# CfVsClaude

An always-on-top button that sits on the Visual Studio window and opens Claude Code in the folder of the current solution.

- **Click** to open Claude Code for the solution's repository (the nearest folder with `.git`, otherwise the solution folder):
  - **Claude desktop app** (default): opens `claude://code/new?folder=<repo>`, which starts a new Code session in that folder. The app asks you to confirm the folder each time. If a desktop Code session for the repository is already running, the Claude window comes to the front instead; pick the session in its sidebar.
  - **Open most recent session for solution** (right-click toggle, on by default, desktop app only): opens the newest non-archived Code session the desktop app has recorded for the repository, using `claude://code/continue?session=local_<id>`, with no folder prompt. If there isn't one, the click behaves as above. Session records are read from `%LocalAppData%\Claude-Data\claude_*\claude-code-sessions\…\local_*.json`. Both the records and this link are undocumented app internals and may change in an app update.
  - **Windows Terminal / Console window** (right-click → *Open in*): runs the `claude` CLI. Each repository gets its own named Windows Terminal window, and a running session's window comes to the front instead of starting a new one.
  - If the desktop app isn't installed, the widget uses Windows Terminal.
- Problems (Claude Code not on PATH, solution folder not found, start failures) show up as Windows toast notifications from the tray icon.
- **Drag** to move the button. Its position is saved relative to the bottom-right corner of the VS window.
- **Right-click** (on the button or the tray icon) for options: open the solution folder, choose a folder by hand, *Open in* (desktop app, Windows Terminal, console), hide when VS is inactive, reset position, exit.

## How it works

| Concern | Mechanism |
|---|---|
| Follow VS | `SetWinEventHook` (foreground, location, minimize, destroy, name change) for the `devenv` process, plus a 1 s check |
| Accurate bounds | `DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)`, per-monitor DPI via `GetDpiForWindow` |
| Never steal focus | `WS_EX_NOACTIVATE \| WS_EX_TOOLWINDOW \| WS_EX_TOPMOST`, `WM_MOUSEACTIVATE → MA_NOACTIVATE` |
| Solution path | VS's DTE object from the Running Object Table (`!VisualStudio.DTE.<ver>:<pid>`) via IDispatch late binding, with no EnvDTE reference. It retries while VS reports busy and gives up after 4 s |
| Existing sessions | `ProcessDiscoveryService` checks `claude.exe` and `node.exe` (running `claude-code`) processes. It reads their working directory from the process's PEB, then finds the host window: through `AttachConsole` + `GetConsoleWindow`, the console window's owner for Windows Terminal, or a Windows Terminal parent process. A process with no console whose parent chain leads to the desktop app's `Claude.exe` counts as a desktop session, and its window is the app's main window |
| Launching (desktop) | `ClaudeLauncherService` opens `claude://code/new?folder=<repo>`, then raises the Claude window. If that window is hidden in the tray, it starts `claude-desktop.exe` again to restore it |
| Launching (terminal) | `ClaudeLauncherService` runs `wt -w claude-<repo hash> new-tab --title "Claude – <Solution>" --suppressApplicationTitle -d <repo> cmd /k claude` |
| Fallback | If VS runs elevated and the overlay doesn't (or DTE is unavailable), it asks you for a folder once and remembers it per solution name |

Settings are stored in `%LocalAppData%\CfVsClaude\settings.json`. `ClaudeCommand` can be changed there (for example to `claude --continue`).

## Layout

```
CfVsClaude.sln            solution (open this in Visual Studio)
src/CfVsClaude/           the app project: CfVsClaude.csproj and its sources
```

## Build

Run these from the repository root:

```bash
dotnet publish src/CfVsClaude -c Release -o publish
```

This builds `publish\CfVsClaude.exe`, a self-contained single file (about 55 MB). For a much smaller exe that needs the .NET 10 Desktop Runtime installed, use:

```bash
dotnet publish src/CfVsClaude -c Release -o publish -p:SelfContained=false
```

Run only one copy at a time. A second launch exits right away.
