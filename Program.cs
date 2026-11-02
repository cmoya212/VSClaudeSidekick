using System.Drawing.Drawing2D;
using CfVsClaude.Services;

namespace CfVsClaude;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, @"Local\CfVsClaude.Overlay", out bool first);
        if (!first) return; // one overlay per user session

        ApplicationConfiguration.Initialize();
        Application.Run(new OverlayContext());
    }
}

/// <summary>Owns the tracker, the overlay window and the tray icon (the only UI while VS is not running).</summary>
internal sealed class OverlayContext : ApplicationContext
{
    private readonly VsTracker _tracker;
    private readonly OverlayForm _overlay;
    private readonly NotifyIcon _tray;
    private readonly Icon _icon;

    public OverlayContext()
    {
        var settings = Settings.Load();

        _icon = CreateIcon();
        _tray = new NotifyIcon
        {
            Icon = _icon,
            Text = "Claude Code overlay for Visual Studio",
            Visible = true,
        };
        var notifier = new Notifier(_tray);
        var launcher = new ClaudeLauncherService(new ProcessDiscoveryService(), notifier, settings);

        _tracker = new VsTracker();
        _overlay = new OverlayForm(_tracker, settings, launcher, notifier);
        _ = _overlay.Handle; // create the window without showing it
        _tray.ContextMenuStrip = _overlay.OptionsMenu;

        _overlay.UpdatePlacement();
    }

    private static Icon CreateIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var bg = new SolidBrush(Color.FromArgb(0xD9, 0x77, 0x57));
            g.FillEllipse(bg, 1, 1, 30, 30);
            using var pen = new Pen(Color.White, 3f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            for (int i = 0; i < 4; i++)
            {
                double a = Math.PI / 4 * i;
                float dx = (float)(Math.Cos(a) * 9), dy = (float)(Math.Sin(a) * 9);
                g.DrawLine(pen, 16 - dx, 16 - dy, 16 + dx, 16 + dy);
            }
        }
        // Clone so the icon owns its data, then free the GDI handle.
        IntPtr h = bmp.GetHicon();
        using var temp = Icon.FromHandle(h);
        var icon = (Icon)temp.Clone();
        Native.DestroyIcon(h);
        return icon;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _overlay.Dispose();
            _tracker.Dispose();
            _icon.Dispose();
        }
        base.Dispose(disposing);
    }
}
