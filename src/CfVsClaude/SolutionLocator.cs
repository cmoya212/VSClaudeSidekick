using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace CfVsClaude;

internal enum SolutionLookupFailure
{
    None,

    /// <summary>No DTE object for the process in the Running Object Table (VS elevated relative to us, or still starting).</summary>
    NotReachable,

    /// <summary>VS kept rejecting calls or did not answer before the timeout (build, debugger break, modal dialog).</summary>
    Busy,

    /// <summary>VS answered, but no solution file, project or document gives a folder.</summary>
    NothingOpen,
}

/// <param name="ActiveFile">Full path of the document active in VS, if any and it exists on disk.</param>
internal readonly record struct SolutionLookup(string? Directory, SolutionLookupFailure Failure, string? ActiveFile = null);

/// <summary>
/// Finds the directory of the solution (or project, or folder) open in a given Visual Studio process by asking its
/// DTE automation object, which VS registers in the Running Object Table as "!VisualStudio.DTE.&lt;ver&gt;:&lt;pid&gt;".
/// Plain IDispatch late binding is used, so no EnvDTE assembly is required.
/// </summary>
internal static class SolutionLocator
{
    public static async Task<SolutionLookup> GetSolutionDirectoryAsync(uint pid, TimeSpan timeout)
    {
        // VS can block COM calls while busy (builds, modal dialogs); never hang the UI thread on it.
        var deadline = DateTime.UtcNow + timeout;
        var work = Task.Run(() => GetSolutionDirectory(pid, deadline));
        var done = await Task.WhenAny(work, Task.Delay(timeout + TimeSpan.FromMilliseconds(500))).ConfigureAwait(true);
        return done == work ? work.Result : new SolutionLookup(null, SolutionLookupFailure.Busy);
    }

    private const int RPC_E_CALL_REJECTED = unchecked((int)0x80010001);
    private const int RPC_E_SERVERCALL_RETRYLATER = unchecked((int)0x8001010A);

    private static SolutionLookup GetSolutionDirectory(uint pid, DateTime deadline)
    {
        object? dte = GetDte(pid);
        if (dte is null) return new SolutionLookup(null, SolutionLookupFailure.NotReachable);

        var comObjects = new List<object> { dte };
        try
        {
            while (true)
            {
                try
                {
                    string? activeFile = GetActiveDocument(dte, comObjects);
                    string? dir = FindDirectory(dte, activeFile, comObjects);
                    return new SolutionLookup(dir, dir is null ? SolutionLookupFailure.NothingOpen : SolutionLookupFailure.None,
                        activeFile);
                }
                // VS's message filter rejects calls while it is busy (builds, debugging, modal UI): retry briefly.
                catch (Exception ex) when (IsBusy(ex))
                {
                    if (DateTime.UtcNow >= deadline) return new SolutionLookup(null, SolutionLookupFailure.Busy);
                    Thread.Sleep(150);
                }
            }
        }
        catch
        {
            return new SolutionLookup(null, SolutionLookupFailure.NotReachable);
        }
        finally
        {
            foreach (object o in comObjects)
            {
                try
                {
                    if (Marshal.IsComObject(o)) Marshal.ReleaseComObject(o);
                }
                catch (InvalidComObjectException)
                {
                    // Already released; the RCW is gone.
                }
            }
        }
    }

    /// <summary>
    /// Solution file folder; else the folder the loaded projects share (VS opened a .csproj directly, so the
    /// solution is temporary and has no path); else the active document's folder (Open Folder mode, loose files).
    /// </summary>
    private static string? FindDirectory(object dte, string? activeFile, List<object> comObjects)
    {
        object? solution = Track(GetProperty(dte, "Solution"), comObjects);
        if (solution is not null)
        {
            string? fullName = GetProperty(solution, "FullName") as string;
            if (!string.IsNullOrWhiteSpace(fullName))
            {
                // In "Open Folder" mode FullName is the folder itself.
                if (Directory.Exists(fullName)) return fullName;
                string? dir = Path.GetDirectoryName(fullName);
                if (dir is not null && Directory.Exists(dir)) return dir;
            }

            string? projectsDir = CommonDirectory(GetProjectDirectories(solution, comObjects));
            if (projectsDir is not null) return projectsDir;
        }

        return activeFile is null ? null : Path.GetDirectoryName(activeFile);
    }

    private static string? GetActiveDocument(object dte, List<object> comObjects)
    {
        try
        {
            object? document = Track(GetProperty(dte, "ActiveDocument"), comObjects);
            return document is not null && GetProperty(document, "FullName") is string path && File.Exists(path) ? path : null;
        }
        catch (Exception ex) when (!IsBusy(ex))
        {
            return null; // some document windows (designers, unsaved files) don't expose a path
        }
    }

    private static List<string> GetProjectDirectories(object solution, List<object> comObjects)
    {
        var dirs = new List<string>();
        object? projects = Track(GetProperty(solution, "Projects"), comObjects);
        if (projects is null) return dirs;

        int count = Convert.ToInt32(GetProperty(projects, "Count"));
        for (int i = 1; i <= count; i++) // DTE collections are 1-based
        {
            object? project = Track(projects.GetType().InvokeMember("Item", BindingFlags.InvokeMethod, null, projects, [i]), comObjects);
            string? fullName;
            try
            {
                fullName = project is null ? null : GetProperty(project, "FullName") as string;
            }
            catch (Exception ex) when (!IsBusy(ex))
            {
                continue; // solution folders and unloaded projects can throw; skip them
            }
            // Solution folders and "Miscellaneous Files" have no path.
            if (string.IsNullOrWhiteSpace(fullName) || !File.Exists(fullName)) continue;
            string? dir = Path.GetDirectoryName(fullName);
            if (dir is not null) dirs.Add(dir);
        }
        return dirs;
    }

    /// <summary>
    /// Deepest folder containing all the given folders; the first one if they share none (different drives,
    /// or different UNC servers or shares).
    /// </summary>
    internal static string? CommonDirectory(List<string> dirs)
    {
        if (dirs.Count == 0) return null;

        // UNC paths start with two empty segments, which every UNC path shares; compare roots instead.
        string? root = Path.GetPathRoot(dirs[0]);
        if (dirs.Any(d => !string.Equals(Path.GetPathRoot(d), root, StringComparison.OrdinalIgnoreCase)))
            return dirs[0];

        string[] common = dirs[0].TrimEnd('\\').Split('\\');
        int length = common.Length;
        foreach (string dir in dirs.Skip(1))
        {
            string[] parts = dir.TrimEnd('\\').Split('\\');
            int n = 0;
            while (n < length && n < parts.Length && parts[n].Equals(common[n], StringComparison.OrdinalIgnoreCase)) n++;
            length = n;
        }

        if (length == 0) return dirs[0];
        string result = string.Join('\\', common, 0, length);
        return result.EndsWith(':') ? result + "\\" : result; // "C:" -> "C:\"
    }

    private static object? Track(object? o, List<object> comObjects)
    {
        if (o is not null) comObjects.Add(o);
        return o;
    }

    private static bool IsBusy(Exception ex)
    {
        int hr = (ex as TargetInvocationException)?.InnerException?.HResult ?? ex.HResult;
        return hr is RPC_E_CALL_REJECTED or RPC_E_SERVERCALL_RETRYLATER;
    }

    private static object? GetProperty(object target, string name) =>
        target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null);

    private static object? GetDte(uint pid)
    {
        if (Native.GetRunningObjectTable(0, out IRunningObjectTable rot) != 0) return null;
        if (Native.CreateBindCtx(0, out IBindCtx ctx) != 0) return null;

        string suffix = ":" + pid;
        rot.EnumRunning(out IEnumMoniker enumMoniker);
        var monikers = new IMoniker[1];
        try
        {
            while (enumMoniker.Next(1, monikers, IntPtr.Zero) == 0)
            {
                IMoniker moniker = monikers[0];
                try
                {
                    moniker.GetDisplayName(ctx, null, out string name);
                    if (name.StartsWith("!VisualStudio.DTE.", StringComparison.Ordinal)
                        && name.EndsWith(suffix, StringComparison.Ordinal)
                        && rot.GetObject(moniker, out object dte) == 0)
                    {
                        return dte;
                    }
                }
                catch
                {
                    // Some ROT entries throw on GetDisplayName; skip them.
                }
                finally
                {
                    Marshal.ReleaseComObject(moniker);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumMoniker);
            Marshal.ReleaseComObject(ctx);
            Marshal.ReleaseComObject(rot);
        }
        return null;
    }
}
