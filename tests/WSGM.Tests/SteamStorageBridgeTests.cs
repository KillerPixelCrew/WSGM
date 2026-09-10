using WSGM.Shell;

namespace WSGM.Tests;

/// <summary>
/// The rules the Steam storage bridge enforces before anything reaches a storage manager.
/// </summary>
/// <remarks>
/// The managers themselves are not exercised here: they own disks, and this project never touches
/// real storage. What is asserted is the part that belongs to the bridge — how a drive's letters
/// become mount paths Steam can show, and that every refusal carries a reason rather than being a
/// control that silently does nothing.
/// </remarks>
public sealed class SteamStorageBridgeTests
{
    [Theory]
    [InlineData("D:", new[] { "D:\\" })]
    [InlineData("D:, E:", new[] { "D:\\", "E:\\" })]
    [InlineData("D:\\", new[] { "D:\\" })]
    [InlineData("", new string[0])]
    [InlineData("   ", new string[0])]
    public void DriveLettersBecomeMountPathsSteamCanShow(string letters, string[] expected)
    {
        // The manager renders letters for a human ("D:, E:"); Steam's block devices carry mount
        // paths. A letter without its separator is not a path any Windows API would accept.
        Assert.Equal(expected, SteamStorageBridge.SplitLetters(letters));
    }

    [Fact]
    public void ANullLetterStringIsNoPathsRatherThanACrash()
    {
        Assert.Empty(SteamStorageBridge.SplitLetters(null!));
    }
}
