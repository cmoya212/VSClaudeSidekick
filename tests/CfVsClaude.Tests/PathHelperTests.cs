using CfVsClaude.Services;

namespace CfVsClaude.Tests;

/// <summary><see cref="ProcessDiscoveryService.NormalizeDirectory"/> and <see cref="ProcessDiscoveryService.IsSameOrUnder"/>.</summary>
public class PathHelperTests
{
    [Theory]
    [InlineData(@"C:\Repo", @"C:\Repo")]
    [InlineData(@"C:\Repo\", @"C:\Repo")]
    [InlineData(@"C:/Repo/src/", @"C:\Repo\src")]
    [InlineData(@"C:\Repo\src\..\lib", @"C:\Repo\lib")]
    [InlineData(@"\\server\share\dir\", @"\\server\share\dir")]
    public void NormalizeDirectory_ReturnsFullPathWithoutTrailingSeparator(string input, string expected) =>
        Assert.Equal(expected, ProcessDiscoveryService.NormalizeDirectory(input));

    [Fact]
    public void NormalizeDirectory_DriveRoot_KeepsTheBackslash() =>
        // A bare "C:" would resolve against the drive's current directory.
        Assert.Equal(@"C:\", ProcessDiscoveryService.NormalizeDirectory(@"C:\"));

    [Fact]
    public void NormalizeDirectory_InvalidPath_KeepsInputAndTrims() =>
        Assert.Equal("a\0b", ProcessDiscoveryService.NormalizeDirectory("a\0b\\"));

    [Theory]
    [InlineData(@"C:\Repo", @"C:\Repo")]
    [InlineData(@"C:\Repo\src", @"C:\Repo")]
    [InlineData(@"C:\Repo\src\deep\er", @"C:\Repo")]
    [InlineData(@"c:\repo\SRC", @"C:\Repo")]
    [InlineData(@"C:\Repo", @"C:\")]
    [InlineData(@"C:\", @"C:\")]
    public void IsSameOrUnder_True(string dir, string root) =>
        Assert.True(ProcessDiscoveryService.IsSameOrUnder(dir, root));

    [Theory]
    [InlineData(@"C:\Repository", @"C:\Repo")] // shares a prefix, but is a sibling
    [InlineData(@"C:\Other", @"C:\Repo")]
    [InlineData(@"C:\", @"C:\Repo")]
    [InlineData(@"D:\Repo", @"C:\Repo")]
    [InlineData(@"D:\Repo", @"C:\")]
    public void IsSameOrUnder_False(string dir, string root) =>
        Assert.False(ProcessDiscoveryService.IsSameOrUnder(dir, root));
}
