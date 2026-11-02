using System.Text.Json;
using System.Text.RegularExpressions;

namespace CfVsClaude.Services;

/// <summary>A Code session recorded by the Claude desktop app.</summary>
internal sealed record DesktopSession(string SessionId, string WorkingDirectory, string Title, DateTimeOffset LastActivity);

/// <summary>
/// Reads the Claude desktop app's Code session records to find the most recent session for a repository.
/// Records live in %LocalAppData%\Claude-Data\claude_&lt;package&gt;\claude-code-sessions\&lt;account&gt;\&lt;org&gt;\local_&lt;id&gt;.json.
/// This is the app's internal, undocumented format, so every read is defensive: anything unexpected is skipped.
/// </summary>
internal static partial class DesktopSessionStore
{
    // The app's claude://code/continue handler only accepts "last" or ids of this shape.
    [GeneratedRegex("^local_[A-Za-z0-9-]{1,64}$")]
    private static partial Regex SessionIdPattern();

    public static DesktopSession? FindMostRecent(string repositoryRoot)
    {
        string root = ProcessDiscoveryService.NormalizeDirectory(repositoryRoot);
        DesktopSession? best = null;

        foreach (string file in EnumerateRecordFiles())
        {
            var session = TryRead(file);
            if (session is null) continue;
            if (!ProcessDiscoveryService.IsSameOrUnder(ProcessDiscoveryService.NormalizeDirectory(session.WorkingDirectory), root))
                continue;
            if (best is null || session.LastActivity > best.LastActivity)
                best = session;
        }
        return best;
    }

    private static IEnumerable<string> EnumerateRecordFiles()
    {
        string dataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Claude-Data");
        if (!Directory.Exists(dataRoot)) return [];

        try
        {
            return Directory.EnumerateDirectories(dataRoot, "claude_*")
                .Select(d => Path.Combine(d, "claude-code-sessions"))
                .Where(Directory.Exists)
                .SelectMany(d => Directory.EnumerateFiles(d, "local_*.json", SearchOption.AllDirectories))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static DesktopSession? TryRead(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            var r = doc.RootElement;

            if (r.TryGetProperty("isArchived", out var archived) && archived.ValueKind == JsonValueKind.True) return null;

            string? id = GetString(r, "sessionId");
            if (id is null || !SessionIdPattern().IsMatch(id)) return null;

            // originCwd is where the session started; cwd can move (e.g. into a worktree).
            string? cwd = GetString(r, "originCwd") ?? GetString(r, "cwd");
            if (string.IsNullOrWhiteSpace(cwd)) return null;

            var lastActivity = GetEpochMs(r, "lastActivityAt") ?? GetEpochMs(r, "lastFocusedAt")
                               ?? new DateTimeOffset(File.GetLastWriteTimeUtc(file));

            return new DesktopSession(id, cwd, GetString(r, "title") ?? string.Empty, lastActivity);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                       or ArgumentOutOfRangeException)
        {
            return null; // being written, locked, or not in the expected shape
        }
    }

    private static string? GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTimeOffset? GetEpochMs(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long ms)
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
            : null;
}
