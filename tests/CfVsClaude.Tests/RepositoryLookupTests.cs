using CfVsClaude.Services;

namespace CfVsClaude.Tests;

/// <summary>File-system lookups in <see cref="ClaudeLauncherService"/>, run against a temp folder.</summary>
public sealed class RepositoryLookupTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    // ---------------------------------------------------------------- FindRepositoryRoot

    [Fact]
    public void FindRepositoryRoot_GitDirectoryAbove_ReturnsRepository()
    {
        _temp.CreateDirectory(@"repo\.git");
        string solution = _temp.CreateDirectory(@"repo\src\App");

        Assert.Equal(_temp.Combine("repo"), ClaudeLauncherService.FindRepositoryRoot(solution));
    }

    [Fact]
    public void FindRepositoryRoot_GitFile_IsAWorktree()
    {
        _temp.CreateFile(@"worktree\.git");
        string solution = _temp.CreateDirectory(@"worktree\src");

        Assert.Equal(_temp.Combine("worktree"), ClaudeLauncherService.FindRepositoryRoot(solution));
    }

    [Fact]
    public void FindRepositoryRoot_NestedRepositories_ReturnsNearest()
    {
        _temp.CreateDirectory(@"outer\.git");
        _temp.CreateDirectory(@"outer\inner\.git");
        string solution = _temp.CreateDirectory(@"outer\inner\src");

        Assert.Equal(_temp.Combine(@"outer\inner"), ClaudeLauncherService.FindRepositoryRoot(solution));
    }

    [Fact]
    public void FindRepositoryRoot_NoGit_ReturnsSolutionDirectory()
    {
        string solution = _temp.CreateDirectory(@"plain\src");

        Assert.Equal(solution, ClaudeLauncherService.FindRepositoryRoot(solution + "\\"));
    }

    [Fact]
    public void FindRepositoryRoot_DriveRootWithoutGit_ReturnsDriveRoot()
    {
        // Tests run from bin\ inside this repository, so a "C:" resolved against the current directory would find it.
        string root = Path.GetPathRoot(Environment.CurrentDirectory)!;
        if (Directory.Exists(Path.Combine(root, ".git")) || File.Exists(Path.Combine(root, ".git")))
            return; // the drive root is itself a repository

        string result = ClaudeLauncherService.FindRepositoryRoot(root);

        Assert.Equal(root.TrimEnd('\\'), result.TrimEnd('\\'), ignoreCase: true);
    }

    // ---------------------------------------------------------------- IsResolvable

    [Fact]
    public void IsResolvable_ExecutableOnPath_WithoutExtension()
    {
        _temp.CreateFile(@"bin\claude.cmd");
        Assert.True(ClaudeLauncherService.IsResolvable("claude", _temp.Combine("bin")));
    }

    [Fact]
    public void IsResolvable_ExecutableOnPath_WithExtension()
    {
        _temp.CreateFile(@"bin\claude.exe");
        Assert.True(ClaudeLauncherService.IsResolvable("claude.exe", _temp.Combine("bin")));
    }

    [Fact]
    public void IsResolvable_SearchesEveryEntry_SkippingBlankOnes()
    {
        _temp.CreateFile(@"second\claude.exe");
        string path = $"{_temp.Combine("first")};; ;  {_temp.Combine("second")}  ";

        Assert.True(ClaudeLauncherService.IsResolvable("claude", path));
    }

    [Fact]
    public void IsResolvable_NotOnPath_ReturnsFalse()
    {
        _temp.CreateFile(@"bin\other.exe");
        Assert.False(ClaudeLauncherService.IsResolvable("claude", _temp.Combine("bin")));
    }

    [Fact]
    public void IsResolvable_FileWithoutExecutableExtension_ReturnsFalse()
    {
        _temp.CreateFile(@"bin\claude");
        Assert.False(ClaudeLauncherService.IsResolvable("claude", _temp.Combine("bin")));
    }

    [Fact]
    public void IsResolvable_EmptyPath_ReturnsFalse() =>
        Assert.False(ClaudeLauncherService.IsResolvable("claude", string.Empty));

    [Fact]
    public void IsResolvable_RootedPath_IgnoresSearchPath()
    {
        string exe = _temp.CreateFile(@"tools\claude.exe");

        Assert.True(ClaudeLauncherService.IsResolvable(exe, string.Empty));
        Assert.True(ClaudeLauncherService.IsResolvable(Path.ChangeExtension(exe, null), string.Empty));
        Assert.False(ClaudeLauncherService.IsResolvable(_temp.Combine(@"tools\missing.exe"), string.Empty));
    }
}
