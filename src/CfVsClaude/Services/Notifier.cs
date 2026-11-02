namespace CfVsClaude.Services;

/// <summary>
/// Toast notifications through the tray icon. Windows 11 renders tray balloon tips as native toasts,
/// which avoids the Windows App SDK and AppUserModelID registration needed by the WinRT toast API.
/// Call on the UI thread.
/// </summary>
internal sealed class Notifier(NotifyIcon tray)
{
    public void Error(string title, string message) => Show(title, message, ToolTipIcon.Error);

    public void Warning(string title, string message) => Show(title, message, ToolTipIcon.Warning);

    public void Info(string title, string message) => Show(title, message, ToolTipIcon.Info);

    private void Show(string title, string message, ToolTipIcon icon)
    {
        if (!tray.Visible) return;
        // Shell limits: 63 characters for the title, 255 for the text.
        tray.ShowBalloonTip(5000, Truncate(title, 63), Truncate(message, 255), icon);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
