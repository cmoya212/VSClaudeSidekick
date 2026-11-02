using System.Drawing.Drawing2D;
using System.Drawing.Text;
using CfVsClaude.Services;

namespace CfVsClaude;

/// <summary>
/// Small borderless, topmost, non-activating button pinned to the Visual Studio window.
/// Click launches Claude Code in the solution directory; drag to reposition; right-click for options.
/// </summary>
internal sealed class OverlayForm : Form
{
    private const int BaseWidth = 30;
    private const int BaseHeight = 30;

    private static readonly Color Accent = Color.FromArgb(0xD9, 0x77, 0x57);
    private static readonly Color AccentHover = Color.FromArgb(0xE5, 0x8B, 0x6D);
    private static readonly Color AccentPressed = Color.FromArgb(0xC2, 0x63, 0x45);

    private readonly VsTracker _tracker;
    private readonly Settings _settings;
    private readonly ClaudeLauncherService _launcher;
    private readonly Notifier _notifier;
    private readonly ToolTip _toolTip = new() { InitialDelay = 600 };
    private readonly ContextMenuStrip _menu;

    private float _scale = 1f;
    private bool _hover, _pressed, _dragging, _busy;
    private Point _dragStartCursor, _dragStartLocation;

    public OverlayForm(VsTracker tracker, Settings settings, ClaudeLauncherService launcher, Notifier notifier)
    {
        _tracker = tracker;
        _settings = settings;
        _launcher = launcher;
        _notifier = notifier;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None; // sized manually from the VS window's DPI
        BackColor = Accent;
        DoubleBuffered = true;
        Text = "Claude Code overlay";
        Cursor = Cursors.Hand;

        _menu = BuildMenu();
        _tracker.Changed += UpdatePlacement;
    }

    public ContextMenuStrip OptionsMenu => _menu;

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = Native.DWMWCP_ROUND;
        Native.DwmSetWindowAttribute(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
    }

    protected override void WndProc(ref Message m)
    {
        // Clicking must never pull focus away from the editor.
        if (m.Msg == Native.WM_MOUSEACTIVATE)
        {
            m.Result = Native.MA_NOACTIVATE;
            return;
        }
        base.WndProc(ref m);
    }

    // ---------------------------------------------------------------- placement

    public void UpdatePlacement()
    {
        if (IsDisposed) return;

        bool show = _tracker.IsTargetDisplayable
                    && (!_settings.HideWhenInactive || _tracker.IsActive || _dragging || _menu.Visible);
        if (!show)
        {
            if (Visible) Hide();
            return;
        }

        IntPtr vs = _tracker.Target;
        uint dpi = Native.GetDpiForWindow(vs);
        float scale = dpi == 0 ? 1f : dpi / 96f;
        if (Math.Abs(scale - _scale) > 0.001f)
        {
            _scale = scale;
            Invalidate();
        }

        int w = (int)(BaseWidth * scale), h = (int)(BaseHeight * scale);

        // While the context menu is open, re-asserting HWND_TOPMOST would raise the overlay above the menu.
        if (!_dragging && !_menu.Visible)
        {
            var r = Native.GetFrameBounds(vs);
            int x = r.Right - (int)(_settings.OffsetRight * scale) - w;
            int y = r.Bottom - (int)(_settings.OffsetBottom * scale) - h;
            x = Math.Clamp(x, r.Left, Math.Max(r.Left, r.Right - w));
            y = Math.Clamp(y, r.Top, Math.Max(r.Top, r.Bottom - h));
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, x, y, w, h, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        }

        if (!Visible) Show();
        _toolTip.SetToolTip(this, string.IsNullOrEmpty(_tracker.SolutionName)
            ? "Launch Claude Code"
            : $"Launch Claude Code in {_tracker.SolutionName}");
    }

    // ---------------------------------------------------------------- painting

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(_pressed ? AccentPressed : _hover ? AccentHover : Accent);

        // Drawn as vector strokes (like the tray icon) so it stays crisp at any DPI, without text-rendering fringes.
        float size = Math.Min(ClientSize.Width, ClientSize.Height);
        float cx = ClientSize.Width / 2f, cy = ClientSize.Height / 2f;
        if (_busy)
        {
            // Three dots while the solution folder is being located.
            float dot = size * 0.13f, gap = size * 0.22f;
            using var brush = new SolidBrush(Color.White);
            for (int i = -1; i <= 1; i++)
                g.FillEllipse(brush, cx + i * gap - dot / 2, cy - dot / 2, dot, dot);
            return;
        }

        float radius = size * 0.30f;
        using var pen = new Pen(Color.White, size * 0.09f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        for (int i = 0; i < 4; i++)
        {
            double a = Math.PI / 4 * i + Math.PI / 8; // tilted so no spoke is axis-aligned, like ✻
            float dx = (float)(Math.Cos(a) * radius), dy = (float)(Math.Sin(a) * radius);
            g.DrawLine(pen, cx - dx, cy - dy, cx + dx, cy + dy);
        }
    }

    // ---------------------------------------------------------------- mouse

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _pressed = true;
        _dragStartCursor = Cursor.Position;
        _dragStartLocation = Location;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_pressed) return;

        var p = Cursor.Position;
        int dx = p.X - _dragStartCursor.X, dy = p.Y - _dragStartCursor.Y;
        var drag = SystemInformation.DragSize;
        if (!_dragging && (Math.Abs(dx) > drag.Width || Math.Abs(dy) > drag.Height))
        {
            _dragging = true;
            Cursor = Cursors.SizeAll;
        }
        if (_dragging)
        {
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, _dragStartLocation.X + dx, _dragStartLocation.Y + dy, 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Right)
        {
            _menu.Show(this, e.Location);
            return;
        }
        if (e.Button != MouseButtons.Left || !_pressed) return;

        _pressed = false;
        Cursor = Cursors.Hand;
        Invalidate();

        if (_dragging)
        {
            _dragging = false;
            SaveOffsetFromCurrentPosition();
            UpdatePlacement();
        }
        else if (ClientRectangle.Contains(e.Location))
        {
            _ = LaunchAsync();
        }
    }

    private void SaveOffsetFromCurrentPosition()
    {
        if (_tracker.Target == IntPtr.Zero) return;
        var r = Native.GetFrameBounds(_tracker.Target);
        _settings.OffsetRight = Math.Max(0, (int)Math.Round((r.Right - Right) / _scale));
        _settings.OffsetBottom = Math.Max(0, (int)Math.Round((r.Bottom - Bottom) / _scale));
        _settings.Save();
    }

    // ---------------------------------------------------------------- actions

    private async Task LaunchAsync()
    {
        if (_busy || _tracker.TargetPid == 0) return;
        SetBusy(true);
        try
        {
            var (dir, activeFile) = await ResolveDirectoryAsync(allowPrompt: true);
            if (dir is not null)
                await _launcher.LaunchOrActivateAsync(dir, _tracker.SolutionName, activeFile);
        }
        catch (Exception ex)
        {
            _notifier.Error("Couldn't start Claude Code", ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task OpenSolutionFolderAsync()
    {
        if (_busy || _tracker.TargetPid == 0) return;
        SetBusy(true);
        try
        {
            var (dir, _) = await ResolveDirectoryAsync(allowPrompt: false);
            if (dir is not null)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{dir}\""))?.Dispose();
        }
        catch (Exception ex)
        {
            _notifier.Error("Couldn't open the solution folder", ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        Invalidate();
    }

    /// <returns>The solution folder (null if unresolved) and the file active in VS (null if none).</returns>
    private async Task<(string? Directory, string? ActiveFile)> ResolveDirectoryAsync(bool allowPrompt)
    {
        string name = _tracker.SolutionName;
        var lookup = await SolutionLocator.GetSolutionDirectoryAsync(_tracker.TargetPid, TimeSpan.FromSeconds(4));
        string? dir = lookup.Directory;

        // Fall back to a folder the user chose earlier for this solution.
        if (dir is null && name.Length > 0 && _settings.ManualFolders.TryGetValue(name, out var saved) && Directory.Exists(saved))
            dir = saved;

        if (dir is null)
        {
            _notifier.Warning("Couldn't find the solution folder", lookup.Failure switch
            {
                SolutionLookupFailure.Busy =>
                    "Visual Studio didn't respond in time. It may be busy with a build, the debugger or a dialog. Try again in a moment.",
                SolutionLookupFailure.NotReachable =>
                    "Couldn't connect to Visual Studio. If it runs as administrator, run the overlay as administrator too.",
                _ => "Visual Studio has no solution, project or document open that has a folder on disk.",
            });
            if (allowPrompt) dir = PromptForFolder(name);
        }

        return (dir, lookup.ActiveFile);
    }

    private string? PromptForFolder(string solutionName, string? initial = null)
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = solutionName.Length > 0
                ? $"Could not determine the folder for \"{solutionName}\". Choose it:"
                : "Choose the folder to run Claude Code in:",
            UseDescriptionForTitle = true,
            InitialDirectory = initial ?? string.Empty,
        };
        if (dlg.ShowDialog() != DialogResult.OK) return null;

        if (solutionName.Length > 0)
        {
            _settings.ManualFolders[solutionName] = dlg.SelectedPath;
            _settings.Save();
        }
        return dlg.SelectedPath;
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();

        menu.Items.Add("Launch Claude Code", null, (_, _) => _ = LaunchAsync());
        menu.Items.Add("Open solution folder", null, (_, _) => _ = OpenSolutionFolderAsync());
        menu.Items.Add("Choose folder for this solution…", null, (_, _) =>
        {
            string name = _tracker.SolutionName;
            _settings.ManualFolders.TryGetValue(name, out var current);
            PromptForFolder(name, current);
        });
        menu.Items.Add(new ToolStripSeparator());

        var hide = new ToolStripMenuItem("Hide when Visual Studio is inactive") { CheckOnClick = true };
        hide.CheckedChanged += (_, _) =>
        {
            _settings.HideWhenInactive = hide.Checked;
            _settings.Save();
            UpdatePlacement();
        };
        var openIn = new ToolStripMenuItem("Open in");
        foreach (var (target, label) in new[]
                 {
                     (LaunchTarget.DesktopApp, "Claude desktop app"),
                     (LaunchTarget.WindowsTerminal, "Windows Terminal"),
                     (LaunchTarget.Console, "Console window"),
                 })
        {
            var item = new ToolStripMenuItem(label) { Tag = target };
            item.Click += (_, _) =>
            {
                _settings.LaunchTarget = target;
                _settings.Save();
            };
            openIn.DropDownItems.Add(item);
        }
        var recent = new ToolStripMenuItem("Open most recent session for solution") { CheckOnClick = true };
        recent.CheckedChanged += (_, _) =>
        {
            _settings.OpenRecentSession = recent.Checked;
            _settings.Save();
        };
        menu.Items.Add(openIn);
        menu.Items.Add(recent);
        menu.Items.Add(hide);
        menu.Items.Add("Reset position", null, (_, _) =>
        {
            var defaults = new Settings();
            _settings.OffsetRight = defaults.OffsetRight;
            _settings.OffsetBottom = defaults.OffsetBottom;
            _settings.Save();
            UpdatePlacement();
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Application.Exit());

        menu.Opening += (_, _) =>
        {
            hide.Checked = _settings.HideWhenInactive;
            recent.Checked = _settings.OpenRecentSession;
            recent.Enabled = _settings.LaunchTarget == LaunchTarget.DesktopApp; // only the desktop app has recorded sessions
            foreach (ToolStripMenuItem item in openIn.DropDownItems)
                item.Checked = (LaunchTarget)item.Tag! == _settings.LaunchTarget;
            bool hasVs = _tracker.TargetPid != 0;
            menu.Items[0].Enabled = menu.Items[1].Enabled = menu.Items[2].Enabled = hasVs;
        };
        menu.Closed += (_, _) => UpdatePlacement();
        return menu;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tracker.Changed -= UpdatePlacement;
            _toolTip.Dispose();
            _menu.Dispose();
        }
        base.Dispose(disposing);
    }
}
