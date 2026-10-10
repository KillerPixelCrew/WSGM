using System.IO.Compression;
using System.Text.Json;
using WSGM.Setup.Engine;

namespace WSGM.Tests.Setup;

public sealed class HandheldPackageRetirementTests
{
    [Theory]
    [InlineData("wsgm.device.msi.claw")]
    [InlineData("wsgm.device.msi.claw-8-a2vm")]
    [InlineData("wsgm.device.asus.rog-ally")]
    public void DeviceArchiveRetirementPreservesFamilyRecoveryState(string id)
    {
        using var installation = new SetupTestInstallation();
        var path = Path.Combine(installation.Root, "Plugins", "renamed.wsgmpkg");
        WriteArchive(path, id);
        SetupTestInstallation.Write(installation.User, "DeviceState/" + id + "/journal.json", "recovery");
        Assert.True(installation.Engine.Run(installation.InstallThroughService(), () => { }));
        Assert.False(File.Exists(path));
        Assert.Equal("recovery", File.ReadAllText(Path.Combine(installation.User, "DeviceState", id, "journal.json")));
    }

    [Theory]
    [InlineData("wsgm.device.msi.claw")]
    [InlineData("wsgm.device.msi.claw-8-a2vm")]
    [InlineData("wsgm.device.asus.rog-ally")]
    public void FailedInstallationRestoresRetiredDeviceArchive(string id)
    {
        using var installation = new SetupTestInstallation();
        var path = Path.Combine(installation.Root, "Plugins", "renamed.wsgmpkg");
        WriteArchive(path, id);
        var original = File.ReadAllBytes(path);
        installation.Runtime.OnRun =
            (_, args) => args.StartsWith("--setup --answers=", StringComparison.Ordinal) ? 1 : 0;
        Assert.False(installation.Engine.Run(installation.InstallThroughService(), () => { }));
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void OtherDevicePackagesRemainInstalled()
    {
        using var installation = new SetupTestInstallation();
        var path = Path.Combine(installation.Root, "Plugins", "wsgm.device.msi.claw.wsgmpkg");
        WriteArchive(path, "community.device.claw");
        Assert.False(NeutralLibraryPackageRetirement.IsRetiredPackage(path));
    }

    private static void WriteArchive(string path, string id)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using var writer = new StreamWriter(archive.CreateEntry("plugin.wsgm.json").Open());
        writer.Write(JsonSerializer.Serialize(new { id }));
    }
}
