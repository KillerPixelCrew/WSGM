using WSGM.Setup.Engine;

namespace WSGM.Tests.Setup;

public sealed class RtssInstallerTests
{
    [Fact]
    public void Matches_RefusesAnythingButThePinnedBuild()
    {
        using MemoryStream other = new([0x50, 0x4B, 0x03, 0x04]);

        Assert.False(RtssInstaller.Matches(other));
    }

    [Fact]
    public void Pin_NamesOneVersionAcrossTheInstallerAndTheDownload()
    {
        var compact = RtssInstaller.Version.Replace(".", "", StringComparison.Ordinal);

        Assert.StartsWith($"RTSSSetup{compact}.", RtssInstaller.SetupEntry, StringComparison.Ordinal);
        Assert.Contains($"RTSSSetup{compact}Build", RtssInstaller.Download.OriginalString, StringComparison.Ordinal);
        Assert.Equal(64, RtssInstaller.Sha256.Length);
    }
}
