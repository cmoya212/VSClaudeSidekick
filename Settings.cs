using System.Text.Json;
using System.Text.Json.Serialization;

namespace CfVsClaude;

internal enum LaunchTarget
{
    /// <summary>A new Code session in the Claude desktop app (claude://code/new).</summary>
    DesktopApp,

    /// <summary>The claude CLI in a Windows Terminal window (a console window if WT is missing).</summary>
    WindowsTerminal,

    /// <summary>The claude CLI in a classic console window.</summary>
    Console,
}

/// <summary>Persisted user settings, stored in %LocalAppData%\CfVsClaude\settings.json.</summary>
internal sealed class Settings
{
    /// <summary>Distance (DIPs) from the overlay's right edge to the VS window's right edge.</summary>
    public int OffsetRight { get; set; } = 24;

    /// <summary>Distance (DIPs) from the overlay's bottom edge to the VS window's bottom edge.</summary>
    public int OffsetBottom { get; set; } = 40;

    /// <summary>Hide the overlay while Visual Studio is not the foreground application.</summary>
    public bool HideWhenInactive { get; set; } = true;

    /// <summary>Command run inside the terminal. Extra args such as "--continue" can be appended.</summary>
    public string ClaudeCommand { get; set; } = "claude";

    /// <summary>Where a click opens Claude Code.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<LaunchTarget>))]
    public LaunchTarget LaunchTarget { get; set; } = LaunchTarget.DesktopApp;

    /// <summary>Desktop app only: open the most recent Code session recorded for the repository, if there is one.</summary>
    public bool OpenRecentSession { get; set; } = true;

    /// <summary>Folder chosen manually for a solution that could not be resolved, keyed by VS window title name.</summary>
    public Dictionary<string, string> ManualFolders { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CfVsClaude", "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), JsonOptions) ?? new Settings();
        }
        catch
        {
            // Corrupt settings fall back to defaults.
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
            // Settings are a convenience; never crash over them.
        }
    }
}
