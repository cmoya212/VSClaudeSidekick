using CfVsClaude.Services;

namespace CfVsClaude.Tests;

/// <summary>Window-title parsing in <see cref="VsTracker"/> and toast text limits in <see cref="Notifier"/>.</summary>
public class TextHelperTests
{
    [Theory]
    [InlineData("MySolution - Microsoft Visual Studio", "MySolution")]
    [InlineData("MySolution - Program.cs - Microsoft Visual Studio", "MySolution")]
    [InlineData("MySolution - Microsoft Visual Studio (Administrator)", "MySolution")]
    [InlineData("MySolution (Debugging) - Microsoft Visual Studio", "MySolution (Debugging)")]
    [InlineData("  MySolution  - Microsoft Visual Studio", "MySolution")]
    [InlineData("MySolution", "MySolution")]
    [InlineData("Microsoft Visual Studio", "")]
    [InlineData("Microsoft Visual Studio (Administrator)", "")]
    [InlineData("", "")]
    public void ParseSolutionName(string title, string expected) =>
        Assert.Equal(expected, VsTracker.ParseSolutionName(title));

    [Theory]
    [InlineData("", 5, "")]
    [InlineData("abc", 5, "abc")]
    [InlineData("abcde", 5, "abcde")]
    [InlineData("abcdef", 5, "abcd…")]
    public void Truncate(string input, int max, string expected)
    {
        string result = Notifier.Truncate(input, max);

        Assert.Equal(expected, result);
        Assert.True(result.Length <= max);
    }
}
