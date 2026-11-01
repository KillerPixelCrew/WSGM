using WSGM.Core;

namespace WSGM.Tests.Core;

public sealed class StoragePathsTests
{
    [Theory]
    [InlineData(@"C:\Packages\Emulator", true)]
    [InlineData(@"C:\Packages\Emulator\bin\game.exe", true)]
    [InlineData(@"C:\Packages\Emulator-old\game.exe", false)]
    [InlineData(@"C:\Packages\Emulator\..\game.exe", false)]
    [InlineData(@"D:\Packages\Emulator\game.exe", false)]
    public void ContainmentUsesTheDirectoryBoundaryAndResolvedPath(string path, bool expected)
    {
        Assert.Equal(expected, StoragePaths.IsUnder(@"C:\Packages\Emulator", path));
    }

    [Fact]
    public void AVolumeRootContainsItsChildren()
    {
        Assert.True(StoragePaths.IsUnder(@"C:\", @"C:\Packages\Emulator\game.exe"));
    }
}
