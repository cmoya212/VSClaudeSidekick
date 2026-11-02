namespace CfVsClaude.Tests;

public class SolutionLocatorTests
{
    [Fact]
    public void CommonDirectory_Empty_ReturnsNull() =>
        Assert.Null(SolutionLocator.CommonDirectory([]));

    [Theory]
    [InlineData(@"C:\Repo\App", new[] { @"C:\Repo\App" })]
    [InlineData(@"C:\Repo\App", new[] { @"C:\Repo\App", @"C:\Repo\App\Sub" })]
    [InlineData(@"C:\Repo", new[] { @"C:\Repo\App", @"C:\Repo\Lib", @"C:\Repo\Tests\Unit" })]
    [InlineData(@"C:\Repo", new[] { @"C:\Repo\App\", @"C:\Repo\Lib" })]
    [InlineData(@"C:\Repo", new[] { @"C:\Repo\App", @"c:\REPO\Lib" })] // casing comes from the first folder
    [InlineData(@"C:\Repo", new[] { @"C:\Repo\App", @"C:\Repo\AppTests" })] // whole segments, not string prefixes
    [InlineData(@"\\server\share", new[] { @"\\server\share\App", @"\\server\share\Lib" })]
    public void CommonDirectory_ReturnsDeepestSharedFolder(string expected, string[] dirs) =>
        Assert.Equal(expected, SolutionLocator.CommonDirectory([.. dirs]));

    [Fact]
    public void CommonDirectory_OnlyTheDriveInCommon_ReturnsDriveRoot() =>
        Assert.Equal(@"C:\", SolutionLocator.CommonDirectory([@"C:\App", @"C:\Lib"]));

    [Fact]
    public void CommonDirectory_DifferentDrives_ReturnsFirst() =>
        Assert.Equal(@"C:\App", SolutionLocator.CommonDirectory([@"C:\App", @"D:\Lib"]));

    [Fact]
    public void CommonDirectory_DifferentUncServers_ReturnsFirst() =>
        Assert.Equal(@"\\alpha\share\App", SolutionLocator.CommonDirectory([@"\\alpha\share\App", @"\\beta\share\Lib"]));

    [Fact]
    public void CommonDirectory_DifferentUncShares_ReturnsFirst() =>
        Assert.Equal(@"\\alpha\one\App", SolutionLocator.CommonDirectory([@"\\alpha\one\App", @"\\alpha\two\Lib"]));

    [Fact]
    public void CommonDirectory_DriveAndUnc_ReturnsFirst() =>
        Assert.Equal(@"C:\App", SolutionLocator.CommonDirectory([@"C:\App", @"\\alpha\share\Lib"]));
}
