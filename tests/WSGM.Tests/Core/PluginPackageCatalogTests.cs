using System.Text;
using WSGM.Core;
using WSGM.Device.Sdk;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Tests;
using WSGM.Tests.Builders;

namespace WSGM.Tests.Core;

public sealed class PluginPackageCatalogTests
{
    private static string Host => PluginPackageFile.HostVersion.ToString(3);

    [Fact]
    public void AReleaseStampAdmitsEveryBuildOfThatRelease()
    {
        // The assembly's fourth part is the build revision; packers stamp only the release.
        Assert.Equal(-1, PluginPackageFile.HostVersion.Revision);
        Assert.True(PluginPackageFile.IsForThisHost(Host));
        Assert.True(PluginPackageFile.IsForThisHost(Host + ".1234"));
    }

    [Fact]
    public void MissingPluginsFolder_IsAValidInstallWithoutPlugins()
    {
        using TemporaryDirectory temporary = new();

        var catalog = PluginPackageCatalog.Discover(temporary.GetPath("absent"));

        Assert.Equal(DevicePackageCardinality.Empty, catalog.Device.Inventory.Cardinality);
        Assert.Null(catalog.Device.InstalledPackage);
        Assert.Empty(catalog.Common);
        Assert.Empty(catalog.Errors);
    }

    [Fact]
    public void OneDevicePackage_IsSelectedWithoutLoadingItsCode()
    {
        using TemporaryDirectory temporary = new();
        var path = WriteDevicePackage(temporary.Root, "claw.wsgmpkg", "test.device", "1.0.0");

        var catalog = PluginPackageCatalog.Discover(temporary.Root);

        var package = Assert.IsType<InstalledDevicePackage>(catalog.Device.InstalledPackage);
        Assert.True(package.Valid);
        Assert.Equal("test.device", package.Manifest?.Id);
        Assert.Equal(Path.GetFullPath(path), package.PackagePath);
        Assert.Empty(catalog.Errors);
    }

    [Fact]
    public void TwoDifferentDevicePackages_RefuseDeviceIntegrationButKeepBothFiles()
    {
        using TemporaryDirectory temporary = new();
        var first = WriteDevicePackage(temporary.Root, "a.wsgmpkg", "first.device", "1.0.0");
        var second = WriteDevicePackage(temporary.Root, "b.wsgmpkg", "second.device", "1.0.0");

        var discovery = PluginPackageCatalog.Discover(temporary.Root).Device;

        Assert.Equal(DevicePackageCardinality.Multiple, discovery.Inventory.Cardinality);
        Assert.Null(discovery.InstalledPackage);
        Assert.Equal("multiple-device-packages", discovery.ErrorCode);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
    }

    [Fact]
    public void NewerVersionOfTheSameId_WinsAndTheOlderFileIsReportedNotDeleted()
    {
        using TemporaryDirectory temporary = new();
        var older = WriteDevicePackage(temporary.Root, "older.wsgmpkg", "test.device", "1.2.0");
        var newer = WriteDevicePackage(temporary.Root, "newer.wsgmpkg", "test.device", "1.10.0");

        var catalog = PluginPackageCatalog.Discover(temporary.Root);

        Assert.Equal(DevicePackageCardinality.Single, catalog.Device.Inventory.Cardinality);
        Assert.Equal("1.10.0", catalog.Device.InstalledPackage?.Manifest?.Version);
        var superseded = Assert.Single(catalog.Superseded);
        Assert.Equal(Path.GetFullPath(older), superseded.PackagePath);
        Assert.True(File.Exists(older));
        Assert.True(File.Exists(newer));
    }

    [Fact]
    public void DevicePackageForAnotherApiVersion_IsReportedAsIncompatible()
    {
        using TemporaryDirectory temporary = new();
        WriteDevicePackage(temporary.Root, "future.wsgmpkg", "test.device", "1.0.0", DeviceApi.Version + 1);

        var package = PluginPackageCatalog.Discover(temporary.Root).Device.InstalledPackage;

        Assert.NotNull(package);
        Assert.False(package.Valid);
        Assert.Equal("api-incompatible", package.RejectionCode);
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData(null)]
    public void PackageBuiltForAnotherWsgmVersion_IsRefusedWithTheVersionItNames(string? builtFor)
    {
        using TemporaryDirectory temporary = new();
        var manifest = builtFor is null
            ? DeviceManifestJson("test.device", "1.0.0").Replace($",\"wsgmVersion\":\"{Host}\"", "",
                StringComparison.Ordinal)
            : DeviceManifestJson("test.device", "1.0.0", wsgmVersion: builtFor);
        WritePackage(temporary.Root, "old.wsgmpkg", manifest, ("plugin.dll", EntryImage()));

        var catalog = PluginPackageCatalog.Discover(temporary.Root);

        Assert.Null(catalog.Device.InstalledPackage);
        Assert.Contains(builtFor ?? "(unstamped)", Assert.Single(catalog.Errors), StringComparison.Ordinal);
    }

    [Fact]
    public void DeviceManifest_CarriesItsHardwareRulesAndDeclaredRoles()
    {
        using TemporaryDirectory temporary = new();
        WriteDevicePackage(temporary.Root, "claw.wsgmpkg", "test.device", "1.0.0");

        var manifest = PluginPackageCatalog.Discover(temporary.Root).Device.InstalledPackage?.Manifest;

        Assert.NotNull(manifest);
        Assert.Equal("MS-1T52", Assert.Single(manifest.Hardware).BaseboardProduct);
        Assert.Equal(CapabilityRole.FanMode, Assert.Single(manifest.Capabilities));
    }

    [Fact]
    public void CommonManifest_IsRoutedByItsCategoryAndDeviceCategoryIsRefused()
    {
        using TemporaryDirectory temporary = new();
        WritePackage(temporary.Root, "ir.wsgmpkg", CommonManifestJson("test.ir", "wsgm.infrared"),
            ("Fixture.dll", EntryImage()));
        WritePackage(temporary.Root, "sneaky.wsgmpkg", CommonManifestJson("test.sneaky", "wsgm.device"),
            ("Fixture.dll", EntryImage()));

        var catalog = PluginPackageCatalog.Discover(temporary.Root);

        Assert.Equal("test.ir", Assert.Single(catalog.Common).Manifest.Id);
        Assert.Null(catalog.Device.InstalledPackage);
        Assert.Contains(catalog.Errors, error => error.StartsWith("sneaky.wsgmpkg", StringComparison.Ordinal));
    }

    [Fact]
    public void NativeImage_IsRefusedBecauseItCannotLoadFromMemory()
    {
        using TemporaryDirectory temporary = new();
        WritePackage(temporary.Root, "native.wsgmpkg", DeviceManifestJson("test.device", "1.0.0"),
            ("plugin.dll", EntryImage()), ("helper.dll", NativeImage()));

        var catalog = PluginPackageCatalog.Discover(temporary.Root);

        Assert.Null(catalog.Device.InstalledPackage);
        Assert.Contains(catalog.Errors, error => error.Contains("native image", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("glyphs/../../escape.txt")]
    [InlineData("C:/escape.txt")]
    public void UnsafeEntryPath_RefusesThePackage(string entry)
    {
        using TemporaryDirectory temporary = new();
        WritePackage(temporary.Root, "unsafe.wsgmpkg", DeviceManifestJson("test.device", "1.0.0"),
            ("plugin.dll", EntryImage()), (entry, "x"u8.ToArray()));

        var catalog = PluginPackageCatalog.Discover(temporary.Root);

        Assert.Null(catalog.Device.InstalledPackage);
        Assert.Single(catalog.Errors);
    }

    [Fact]
    public void BrokenArchiveAndOtherFiles_AreReportedOrIgnoredWithoutSelectingADevice()
    {
        using TemporaryDirectory temporary = new();
        File.WriteAllText(Path.Combine(temporary.Root, "broken.wsgmpkg"), "not a zip");
        File.WriteAllText(Path.Combine(temporary.Root, "readme.txt"), "ignored");

        var catalog = PluginPackageCatalog.Discover(temporary.Root);

        Assert.Null(catalog.Device.InstalledPackage);
        Assert.StartsWith("broken.wsgmpkg", Assert.Single(catalog.Errors), StringComparison.Ordinal);
    }

    [Fact]
    public void OpenPackage_ServesGlyphFilesAndBlocksReplacementUntilDisposed()
    {
        using TemporaryDirectory temporary = new();
        var path = WritePackage(temporary.Root, "glyphs.wsgmpkg", DeviceManifestJson("test.device", "1.0.0"),
            ("plugin.dll", EntryImage()),
            ("glyphs/profiles/pad.json", "{}"u8.ToArray()),
            ("glyphs/assets/a.svg", "<svg/>"u8.ToArray()));

        using (var package = PluginPackageFile.Open(path))
        {
            Assert.Equal(["pad"], package.EnumerateProfileIds());
            Assert.True(package.TryRead("glyphs/assets/a.svg", 1024, out var bytes));
            Assert.Equal("<svg/>", Encoding.UTF8.GetString(bytes));
            Assert.False(package.TryRead("glyphs/assets/a.svg", 2, out _));
            Assert.False(package.TryRead("../plugin.wsgm.json", 1024, out _));
            Assert.ThrowsAny<IOException>(() => File.Delete(path));
        }

        File.Delete(path);
        Assert.False(File.Exists(path));
    }

    private static string WritePackage(string directory, string fileName, string manifest,
        params (string Name, byte[] Bytes)[] files)
    {
        return PluginPackageBuilders.Write(Path.Combine(directory, fileName), manifest, files);
    }

    private static string WriteDevicePackage(string directory, string fileName, string id, string version,
        int apiVersion = DeviceApi.Version)
    {
        return WritePackage(directory, fileName, DeviceManifestJson(id, version, apiVersion),
            ("plugin.dll", EntryImage()));
    }

    private static string DeviceManifestJson(string id, string version, int apiVersion = DeviceApi.Version,
        string? wsgmVersion = null)
    {
        return $$"""
                 {"id":"{{id}}","name":"Fixture","version":"{{version}}","apiVersion":{{apiVersion}},
                  "entryAssembly":"plugin.dll","entryType":"Fixture.Plugin","wsgmVersion":"{{wsgmVersion ?? Host}}",
                  "hardware":[{"baseboardProduct":"MS-1T52"}],"capabilities":["FanMode"]}
                 """;
    }

    private static string CommonManifestJson(string id, string category)
    {
        return $$"""
                 {"id":"{{id}}","name":"Fixture","version":"1.0.0","category":"{{category}}",
                  "entryAssembly":"Fixture.dll","entryType":"Fixture.Plugin","wsgmVersion":"{{Host}}"}
                 """;
    }

    // Any x64 managed image serves as an entry point: discovery reads metadata and never runs it.
    private static byte[] EntryImage()
    {
        return File.ReadAllBytes(typeof(PluginPackageCatalogTests).Assembly.Location);
    }

    private static byte[] NativeImage()
    {
        var bytes = new byte[70];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        BitConverter.GetBytes(64).CopyTo(bytes, 60);
        bytes[64] = (byte)'P';
        bytes[65] = (byte)'E';
        bytes[68] = 0x64;
        bytes[69] = 0x86;
        return bytes;
    }
}
