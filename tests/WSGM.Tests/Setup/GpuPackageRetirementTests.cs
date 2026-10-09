using System.IO.Compression;
using System.Text.Json;
using WSGM.Setup.Engine;
using WSGM.Testing;

namespace WSGM.Tests.Setup;

public sealed class GpuPackageRetirementTests
{
    [Theory]
    [InlineData("wsgm.gpu.intel")]
    [InlineData("wsgm.gpu.amd")]
    [InlineData("wsgm.gpu.nvidia")]
    public void InstalledArchiveIdentityRetiresTheBuiltInPackageAndKeepsRecoveryState(string id)
    {
        using SetupTestInstallation installation = new();
        var archive = Path.Combine(installation.Root, "Plugins", "custom-name.wsgmpkg");
        WritePackage(archive, id);
        SetupTestInstallation.Write(installation.User, "PluginState/" + id + "/gpu-state.json", "recovery-state");

        Assert.True(installation.Engine.Run(installation.InstallThroughService(), () => { }));

        Assert.False(File.Exists(archive));
        Assert.Equal("recovery-state",
            File.ReadAllText(Path.Combine(installation.User, "PluginState", id, "gpu-state.json")));
    }

    [Theory]
    [InlineData("community.gpu.intel")]
    [InlineData("wsgm.gpu.intel.extra")]
    [InlineData("WSGM.GPU.INTEL")]
    public void AMatchingFilenameDoesNotRetireAnotherManifestIdentity(string id)
    {
        using SetupTestInstallation installation = new();
        var archive = Path.Combine(installation.Root, "Plugins", "wsgm.gpu.intel-0.1.0.wsgmpkg");
        WritePackage(archive, id);

        Assert.True(installation.Engine.Run(installation.InstallThroughService(), () => { }));

        Assert.True(File.Exists(archive));
    }

    [Fact]
    public void FailedInstallRestoresRetiredArchivesThroughTheExistingFileTransaction()
    {
        using SetupTestInstallation installation = new();
        var archive = Path.Combine(installation.Root, "Plugins", "wsgm.gpu.nvidia-0.1.0.wsgmpkg");
        WritePackage(archive, "wsgm.gpu.nvidia");
        var original = File.ReadAllBytes(archive);
        installation.Runtime.OnRun = (_, arguments) =>
            arguments.StartsWith("--setup --answers=", StringComparison.Ordinal) ? 1 : 0;

        Assert.False(installation.Engine.Run(installation.InstallThroughService(), () => { }));

        Assert.Equal(original, File.ReadAllBytes(archive));
    }

    [Theory]
    [InlineData("not-a-package")]
    [InlineData("{\"id\":\"wsgm.gpu.intel\"}")]
    public void NonArchiveFilesArePreserved(string content)
    {
        using TemporaryDirectory directory = new();
        var path = directory.GetPath("wsgm.gpu.intel-0.1.0.wsgmpkg");
        File.WriteAllText(path, content);

        Assert.False(GpuPackageRetirement.IsRetiredPackage(path));
        Assert.Equal(content, File.ReadAllText(path));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{\"id\":42}")]
    public void InvalidManifestsArePreserved(string manifest)
    {
        using TemporaryDirectory directory = new();
        var path = directory.GetPath("package.wsgmpkg");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(archive.CreateEntry("plugin.wsgm.json").Open());
            writer.Write(manifest);
        }

        Assert.False(GpuPackageRetirement.IsRetiredPackage(path));
        Assert.True(File.Exists(path));
    }

    private static void WritePackage(string path, string id)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using var writer = new StreamWriter(archive.CreateEntry("plugin.wsgm.json").Open());
        writer.Write(JsonSerializer.Serialize(new { id }));
    }
}
