using WSGM.Device.Contracts.Identity;
using WSGM.Device.Contracts.Packaging;

namespace WSGM.DeviceHost.Tests;

public class PluginPackageLoaderTests
{
    [Fact]
    public void ReadMetadata_ValidPackage_ReturnsConstrainedEntryPoint()
    {
        using TemporaryPackage package = new();
        package.WriteManifest("plugin.dll");
        package.WriteEntryPoint("plugin.dll");

        PluginPackageMetadata metadata = PluginPackageLoader.ReadMetadata(
            package.Root,
            TemporaryPackage.PackageId);

        Assert.Equal(Path.Combine(package.Root, "plugin.dll"), metadata.EntryPath);
        Assert.Equal(TemporaryPackage.PackageId, metadata.Manifest.Id);
    }

    [Fact]
    public void ReadMetadata_LaunchGrantNamesAnotherPackage_RejectsBeforeLoadingCode()
    {
        using TemporaryPackage package = new();
        package.WriteManifest("plugin.dll");
        package.WriteEntryPoint("plugin.dll");

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            PluginPackageLoader.ReadMetadata(package.Root, "wsgm.device.confused"));

        Assert.Equal("The package identifier does not match the launch grant.", error.Message);
    }

    [Fact]
    public void ReadMetadata_EntryPointTraversal_RejectsBeforeFilesystemResolution()
    {
        using TemporaryPackage package = new();
        package.WriteManifest("../plugin.dll");

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            PluginPackageLoader.ReadMetadata(package.Root, TemporaryPackage.PackageId));

        Assert.Contains("manifest failed validation", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadMetadata_MissingEntryPoint_RejectsDeterministically()
    {
        using TemporaryPackage package = new();
        package.WriteManifest("plugin.dll");

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            PluginPackageLoader.ReadMetadata(package.Root, TemporaryPackage.PackageId));

        Assert.Equal("The plugin entry point is missing or is a link.", error.Message);
    }

    private sealed class TemporaryPackage : IDisposable
    {
        public const string PackageId = "wsgm.device.test";

        public TemporaryPackage()
        {
            Root = Path.Combine(Path.GetTempPath(), "wsgm-device-host-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void WriteEntryPoint(string relativePath)
        {
            string path = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, [0x4D, 0x5A]);
        }

        public void WriteManifest(string entryPoint)
        {
            PluginManifest manifest = new()
            {
                SchemaVersion = 1,
                Id = PackageId,
                Version = "1.0.0",
                DisplayName = "DeviceHost test package",
                Publisher = "WSGM tests",
                MinApiVersion = 1,
                MaxApiVersion = 1,
                EntryPoint = entryPoint,
                Devices =
                [
                    new DeviceDefinition
                    {
                        Id = "test-device",
                        DisplayName = "Test device",
                        Identity =
                        [
                            new IdentityObservation
                            {
                                Signal = IdentitySignal.SmbiosBaseboardProduct,
                                Strength = IdentityStrength.Required,
                                Values = ["TEST-BOARD"],
                            },
                        ],
                    },
                ],
                Provenance = new PackageProvenance
                {
                    Source = "Test fixture",
                    License = "MIT",
                    ProvenanceClass = ProvenanceClass.IndependentCapture,
                },
            };
            File.WriteAllBytes(
                Path.Combine(Root, "plugin.wsgm.json"),
                PluginManifestReader.ToCanonicalUtf8(manifest));
        }

        public void Dispose()
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}
