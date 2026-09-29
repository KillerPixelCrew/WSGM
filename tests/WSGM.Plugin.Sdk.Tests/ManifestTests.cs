using WSGM.Device.Sdk.Capabilities;
using Xunit;

namespace WSGM.Plugin.Sdk.Tests;

public sealed class ManifestTests
{
    private static PluginManifest Valid => new()
    {
        Id = "example.remote",
        Name = "Remote",
        Version = "1.2.0",
        Category = "example.future-category",
        EntryAssembly = "Remote.dll",
        EntryType = "Example.Remote"
    };

    [Fact]
    public void CategoriesAreOpenAndDeviceIsAnOptionalSelectedSlot()
    {
        Assert.Empty(PluginManifestReader.Validate(Valid));
        Assert.Equal(0, PluginCategoryPolicy.Device.MinimumActive);
        Assert.Equal(1, PluginCategoryPolicy.Device.MaximumActive);
        Assert.True(PluginCategoryPolicy.Device.RequiresSelection);
        Assert.Null(PluginCategoryPolicy.Multiple.MaximumActive);
        // The one WSGM assembly the common contracts lean on is the Device SDK, for its active-time
        // Deadline; anything else would tie an outside package to WSGM's own code.
        Assert.Equal(["WSGM.Device.Sdk"],
            typeof(IPlugin).Assembly.GetReferencedAssemblies()
                .Select(name => name.Name!)
                .Where(name => name.StartsWith("WSGM.", StringComparison.Ordinal)));
    }

    [Fact]
    public void DisplayNamesRejectBidirectionalFormatting()
    {
        Assert.NotEmpty(PluginManifestReader.Validate(Valid with { Name = "Remote‮name" }));
    }

    [Theory]
    [InlineData("../Remote.dll")]
    [InlineData("C:\\Remote.dll")]
    [InlineData("sub/Remote.dll")]
    [InlineData("Remote.dll:stream")]
    public void AssemblyTraversalAndAlternateStreamsAreRejected(string path)
    {
        Assert.NotEmpty(PluginManifestReader.Validate(Valid with { EntryAssembly = path }));
    }

    [Fact]
    public void DependenciesRejectSelfDuplicatesAndInvertedRanges()
    {
        Assert.NotEmpty(PluginManifestReader.Validate(Valid with
        {
            Dependencies = [new PluginDependency(Valid.Id, "1.0")]
        }));
        Assert.NotEmpty(PluginManifestReader.Validate(Valid with
        {
            Dependencies = [new PluginDependency("other", "2.0", "1.0")]
        }));
        Assert.NotEmpty(PluginManifestReader.Validate(Valid with
        {
            Dependencies = [new PluginDependency("other", "1.0"), new PluginDependency("other", "1.1")]
        }));
        Assert.Empty(PluginManifestReader.Validate(Valid with
        {
            Dependencies = [new PluginDependency("other", "1.0", "2.0")]
        }));
    }

    [Fact]
    public void CompatibilityAndUnknownJsonMembersFailBeforeLoading()
    {
        Assert.NotEmpty(PluginManifestReader.Validate(Valid with
        {
            MinimumApiVersion = PluginApi.Version + 1, MaximumApiVersion = PluginApi.Version + 2
        }));
        var json = """
                   {"id":"example.remote","name":"Remote","version":"1.0","category":"example.remote",
                    "entryAssembly":"Remote.dll","entryType":"Example.Remote","unexpected":true}
                   """u8;
        Assert.False(PluginManifestReader.TryRead(json, out var rejected, out var errors));
        Assert.Null(rejected);
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void StrictReaderAcceptsCommonMetadataAndBoundsMalformedInputs()
    {
        var json = """
                   {"id":"example.remote","name":"Remote","version":"1.0","category":"example.remote",
                    "entryAssembly":"Remote.dll","entryType":"Example.Remote"}
                   """u8;
        Assert.True(PluginManifestReader.TryRead(json, out var manifest, out var errors), string.Join("; ", errors));
        Assert.Empty(errors);
        Assert.Equal("example.remote", manifest!.Id);
        Assert.False(PluginManifestReader.TryRead([], out _, out _));
        Assert.False(PluginManifestReader.TryRead("null"u8, out _, out _));
        Assert.False(PluginManifestReader.TryRead("{"u8, out _, out _));
    }

    private static PluginManifest Gpu => Valid with
    {
        Category = PluginCategories.Gpu,
        DisplayAdapters = [new DisplayAdapterMatch("8086")],
        Capabilities = [CapabilityRole.VariableRefreshRate, CapabilityRole.GenericToggle]
    };

    [Fact]
    public void GraphicsPackagesDeclareAdaptersAndCapabilities()
    {
        Assert.Empty(PluginManifestReader.Validate(Gpu));
        Assert.NotEmpty(PluginManifestReader.Validate(Gpu with { DisplayAdapters = [] }));
        Assert.NotEmpty(PluginManifestReader.Validate(Gpu with { Capabilities = [] }));
    }

    [Fact]
    public void OtherCategoriesDeclareNeitherAdaptersNorCapabilities()
    {
        Assert.NotEmpty(PluginManifestReader.Validate(Valid with { DisplayAdapters = [new DisplayAdapterMatch("8086")] }));
        Assert.NotEmpty(PluginManifestReader.Validate(Valid with { Capabilities = [CapabilityRole.GenericToggle] }));
    }

    [Theory]
    [InlineData("808")]
    [InlineData("80866")]
    [InlineData("80G6")]
    [InlineData("")]
    public void AdapterVendorIdsAreFourHexDigits(string vendor)
    {
        Assert.NotEmpty(PluginManifestReader.Validate(Gpu with { DisplayAdapters = [new DisplayAdapterMatch(vendor)] }));
    }

    [Fact]
    public void AdapterAndCapabilityListsRejectDuplicatesDeviceRolesAndExcess()
    {
        Assert.NotEmpty(PluginManifestReader.Validate(Gpu with
        {
            DisplayAdapters = [new DisplayAdapterMatch("10de"), new DisplayAdapterMatch("10DE")]
        }));
        Assert.NotEmpty(PluginManifestReader.Validate(Gpu with
        {
            Capabilities = [CapabilityRole.GenericToggle, CapabilityRole.GenericToggle]
        }));
        Assert.NotEmpty(PluginManifestReader.Validate(Gpu with { Capabilities = [CapabilityRole.ControllerSource] }));
        Assert.NotEmpty(PluginManifestReader.Validate(Gpu with { Capabilities = [(CapabilityRole)999] }));
        Assert.NotEmpty(PluginManifestReader.Validate(Gpu with
        {
            DisplayAdapters =
            [
                .. Enumerable.Range(0, PluginManifestReader.MaximumDisplayAdapters + 1)
                    .Select(index => new DisplayAdapterMatch(index.ToString("X4", System.Globalization.CultureInfo.InvariantCulture)))
            ]
        }));
    }

    [Fact]
    public void ReaderNormalizesVendorIdsAndRefusesUnknownRoles()
    {
        var json = """
                   {"id":"wsgm.gpu.example","name":"Example","version":"1.0","category":"wsgm.gpu",
                    "entryAssembly":"Example.dll","entryType":"Example.Plugin",
                    "displayAdapters":[{"pciVendorId":"10de"}],"capabilities":["VariableRefreshRate"]}
                   """u8;
        Assert.True(PluginManifestReader.TryRead(json, out var manifest, out var errors), string.Join("; ", errors));
        Assert.Equal("10DE", Assert.Single(manifest!.DisplayAdapters).PciVendorId);
        Assert.Equal([CapabilityRole.VariableRefreshRate], manifest.Capabilities);

        var unknownRole = """
                          {"id":"wsgm.gpu.example","name":"Example","version":"1.0","category":"wsgm.gpu",
                           "entryAssembly":"Example.dll","entryType":"Example.Plugin",
                           "displayAdapters":[{"pciVendorId":"10DE"}],"capabilities":["WarpDrive"]}
                          """u8;
        Assert.False(PluginManifestReader.TryRead(unknownRole, out _, out _));

        var unknownAdapterMember = """
                                   {"id":"wsgm.gpu.example","name":"Example","version":"1.0","category":"wsgm.gpu",
                                    "entryAssembly":"Example.dll","entryType":"Example.Plugin",
                                    "displayAdapters":[{"pciVendorId":"10DE","pciDeviceId":"2684"}],
                                    "capabilities":["GenericToggle"]}
                                   """u8;
        Assert.False(PluginManifestReader.TryRead(unknownAdapterMember, out _, out _));
    }
}
