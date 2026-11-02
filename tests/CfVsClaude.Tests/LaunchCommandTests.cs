using System.Diagnostics;
using CfVsClaude.Services;

namespace CfVsClaude.Tests;

/// <summary>How <see cref="ClaudeLauncherService"/> turns settings and paths into process start info.</summary>
public class LaunchCommandTests
{
    // ---------------------------------------------------------------- FirstToken

    [Theory]
    [InlineData("claude", "claude")]
    [InlineData("claude --continue", "claude")]
    [InlineData(@"""C:\Program Files\Claude\claude.exe"" --continue", @"C:\Program Files\Claude\claude.exe")]
    [InlineData(@"""C:\Program Files\Claude\claude.exe""", @"C:\Program Files\Claude\claude.exe")]
    [InlineData(@"""C:\Program Files\claude", @"C:\Program Files\claude")] // unclosed quote
    public void FirstToken_ReturnsExecutable(string command, string expected) =>
        Assert.Equal(expected, ClaudeLauncherService.FirstToken(command));

    // ---------------------------------------------------------------- EscapeCmd

    [Theory]
    [InlineData("Claude – MySolution", "Claude – MySolution")]
    [InlineData("a&b|c<d>e^f%g", "a^&b^|c^<d^>e^^f^%g")]
    [InlineData("say \"hi\"", "say hi")]
    [InlineData("", "")]
    public void EscapeCmd_EscapesCmdMetacharactersAndDropsQuotes(string input, string expected) =>
        Assert.Equal(expected, ClaudeLauncherService.EscapeCmd(input));

    // ---------------------------------------------------------------- NormalizeDir

    [Theory]
    [InlineData(@"C:\Repo", @"C:\Repo")]
    [InlineData(@"C:\Repo\", @"C:\Repo")]
    [InlineData(@"C:\Repo\\", @"C:\Repo")]
    [InlineData(@"C:\", @"C:\.")]
    [InlineData("C:", @"C:\.")]
    public void NormalizeDir_NeverEndsWithBackslash(string input, string expected) =>
        Assert.Equal(expected, ClaudeLauncherService.NormalizeDir(input));

    // ---------------------------------------------------------------- WindowName

    [Fact]
    public void WindowName_HasExpectedShape() =>
        Assert.Matches("^claude-[0-9a-f]{8}$", ClaudeLauncherService.WindowName(@"C:\Repo"));

    [Fact]
    public void WindowName_IgnoresCase() =>
        Assert.Equal(ClaudeLauncherService.WindowName(@"C:\Repo"), ClaudeLauncherService.WindowName(@"c:\REPO"));

    [Fact]
    public void WindowName_DiffersPerRepository() =>
        Assert.NotEqual(ClaudeLauncherService.WindowName(@"C:\RepoA"), ClaudeLauncherService.WindowName(@"C:\RepoB"));

    // ---------------------------------------------------------------- ForWindowsTerminal

    [Fact]
    public void ForWindowsTerminal_BuildsNamedWindowTab()
    {
        ProcessStartInfo psi = ClaudeLauncherService.ForWindowsTerminal(
            @"C:\wt.exe", @"C:\Repo\", "Claude – Sln", "claude-0123abcd", "claude");

        Assert.Equal(@"C:\wt.exe", psi.FileName);
        Assert.False(psi.UseShellExecute);
        Assert.Equal(@"C:\Repo\", psi.WorkingDirectory);
        Assert.Equal(
            ["-w", "claude-0123abcd", "new-tab", "--title", "Claude – Sln", "--suppressApplicationTitle",
             "-d", @"C:\Repo", "cmd.exe", "/k", "claude"],
            psi.ArgumentList);
    }

    [Fact]
    public void ForWindowsTerminal_EscapesSemicolons()
    {
        ProcessStartInfo psi = ClaudeLauncherService.ForWindowsTerminal(
            "wt.exe", @"C:\a;b", "Claude – A;B", "claude-0123abcd", "claude --continue; echo");

        Assert.Equal("Claude – A\\;B", psi.ArgumentList[4]);
        Assert.Equal(@"C:\a\;b", psi.ArgumentList[7]);
        Assert.Equal("claude --continue\\; echo", psi.ArgumentList[^1]);
    }

    [Fact]
    public void ForWindowsTerminal_DriveRoot_PassesDotSoTheQuoteIsNotEscaped()
    {
        ProcessStartInfo psi = ClaudeLauncherService.ForWindowsTerminal("wt.exe", @"D:\", "t", "w", "claude");
        Assert.Equal(@"D:\.", psi.ArgumentList[7]);
    }

    // ---------------------------------------------------------------- ForConsole

    [Fact]
    public void ForConsole_SetsTitleThenRunsCommand()
    {
        ProcessStartInfo psi = ClaudeLauncherService.ForConsole(@"C:\Repo", "Claude – A&B", "claude --continue");

        Assert.Equal("cmd.exe", psi.FileName);
        Assert.False(psi.UseShellExecute);
        Assert.Equal(@"C:\Repo", psi.WorkingDirectory);
        Assert.Equal("/k title Claude – A^&B & claude --continue", psi.Arguments);
    }

    // ---------------------------------------------------------------- FileMention

    [Theory]
    [InlineData(@"C:\Repo\src\Foo.cs", "@src/Foo.cs ")]
    [InlineData(@"C:\Repo\Foo.cs", "@Foo.cs ")]
    [InlineData(@"C:\Repo\src\..\Foo.cs", "@Foo.cs ")]
    [InlineData(@"c:\repo\src\Foo.cs", "@src/Foo.cs ")]
    public void FileMention_FileInRepository_ReturnsRelativeMention(string file, string expected) =>
        Assert.Equal(expected, ClaudeLauncherService.FileMention(@"C:\Repo", file));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"C:\Other\Foo.cs")]
    [InlineData(@"C:\Repository\Foo.cs")]
    [InlineData(@"C:\Repo\My Docs\Foo.cs")]
    [InlineData(@"C:\Repo\src\My Foo.cs")]
    public void FileMention_ReturnsNull(string? file) =>
        Assert.Null(ClaudeLauncherService.FileMention(@"C:\Repo", file));
}
