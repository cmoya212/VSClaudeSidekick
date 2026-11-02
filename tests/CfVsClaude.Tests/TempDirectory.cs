namespace CfVsClaude.Tests;

/// <summary>A uniquely named folder under %TEMP%, deleted on dispose.</summary>
internal sealed class TempDirectory : IDisposable
{
    public string Path { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CfVsClaude.Tests", Guid.NewGuid().ToString("N"));

    public TempDirectory() => Directory.CreateDirectory(Path);

    public string CreateDirectory(string relative) => Directory.CreateDirectory(Combine(relative)).FullName;

    public string CreateFile(string relative)
    {
        string full = Combine(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, string.Empty);
        return full;
    }

    public string Combine(string relative) => System.IO.Path.Combine(Path, relative);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; %TEMP% is cleaned up eventually.
        }
    }
}
