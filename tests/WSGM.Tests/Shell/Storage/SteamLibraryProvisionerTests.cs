using WSGM.Shell;

namespace WSGM.Tests.Shell;

public sealed class SteamLibraryProvisionerTests
{
    [Theory]
    [InlineData("D:", @"D:\SteamLibrary")]
    [InlineData(@"D:\", @"D:\SteamLibrary")]
    [InlineData(@"D:\Games", @"D:\Games")]
    [InlineData(@"\\nas\media\steam", @"\\nas\media\steam")]
    [InlineData(@"E:\SteamLibrary\", @"E:\SteamLibrary")]
    public void DriveRootsGetTheSteamLibrarySubfolderOthersAreUsedAsIs(
        string picked, string expected)
    {
        Assert.Equal(expected, SteamLibraryProvisioner.ResolveLibraryRoot(picked));
    }
}
