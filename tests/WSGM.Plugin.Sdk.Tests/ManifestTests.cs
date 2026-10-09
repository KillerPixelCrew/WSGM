using System.Globalization;
using System.Text;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Input;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Device.Sdk.Packaging;
using WSGM.Device.Sdk.Plugin;

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

    private static PluginManifest Gpu => Valid with
    {
        Category = PluginCategories.Gpu,
        DisplayAdapters = [new DisplayAdapterMatch("8086")],
        Capabilities = [CapabilityRole.VariableRefreshRate, CapabilityRole.GenericToggle]
    };

    [Fact]
    public void CategoriesAreOpenAndDeviceIsAnOptionalSelectedSlot()
    {
        Assert.Empty(PluginManifestReader.Validate(Valid));
        Assert.Equal(0, PluginCategoryPolicy.Device.MinimumActive);
        Assert.Equal(1, PluginCategoryPolicy.Device.MaximumActive);
        Assert.True(PluginCategoryPolicy.Device.RequiresSelection);
        Assert.Null(PluginCategoryPolicy.Multiple.MaximumActive);
        Assert.DoesNotContain(typeof(IPlugin).Assembly.GetReferencedAssemblies(),
            name => name.Name!.StartsWith("WSGM.", StringComparison.Ordinal));
    }

    [Fact]
    public void SharedContractsHaveOneAssemblyIdentity()
    {
        Assert.Equal(5, PluginApi.Version);
        Assert.Same(typeof(IPlugin).Assembly, typeof(CapabilityDescriptor).Assembly);
        Assert.Same(typeof(IPlugin).Assembly, typeof(Deadline).Assembly);
        Assert.Same(typeof(IPlugin).Assembly, typeof(PluginTrace).Assembly);
        Assert.Same(typeof(IPlugin).Assembly, typeof(CanonicalControllerSample).Assembly);
        Assert.DoesNotContain("WSGM.Device.Sdk", PluginPackageLayout.HostProvidedAssemblies);
        Assert.Contains("LibHandheld", PluginPackageLayout.HostProvidedAssemblies);
        Assert.Contains("LibGPUDriverInteract", PluginPackageLayout.HostProvidedAssemblies);
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
    public void FieldsAreCheckedForShapeNotLengthOrCount()
    {
        Assert.Empty(PluginManifestReader.Validate(Valid with
        {
            Id = "example." + new string('a', 300),
            Name = new string('N', 300),
            Dependencies = [.. Enumerable.Range(0, 40).Select(index => new PluginDependency($"other{index}", "1.0"))],
            Permissions = [.. Enumerable.Range(0, 40).Select(index => $"permission.{index}")]
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

    [Theory]
    [InlineData(4, 4)]
    [InlineData(4, 5)]
    [InlineData(1, 99)]
    [InlineData(5, 6)]
    public void SharedAssemblyIdentityBreakRefusesLegacyOrBroadApiRanges(int minimum, int maximum)
    {
        Assert.Contains("Incompatible common Plugin SDK version range.", PluginManifestReader.Validate(Valid with
        {
            MinimumApiVersion = minimum, MaximumApiVersion = maximum
        }));
        var json = Encoding.UTF8.GetBytes(
            $$"""
              {"id":"example.remote","name":"Remote","version":"1.0","category":"example.remote",
               "entryAssembly":"Remote.dll","entryType":"Example.Remote",
               "minimumApiVersion":{{minimum}},"maximumApiVersion":{{maximum}}}
              """);
        Assert.False(PluginManifestReader.TryRead(json, out var rejected, out var errors));
        Assert.Null(rejected);
        Assert.Contains("Incompatible common Plugin SDK version range.", errors);
    }

    [Fact]
    public void RebuiltApiFiveManifestIsAccepted()
    {
        var json = """
                   {"id":"example.remote","name":"Remote","version":"1.0","category":"example.remote",
                    "entryAssembly":"Remote.dll","entryType":"Example.Remote",
                    "minimumApiVersion":5,"maximumApiVersion":5}
                   """u8;
        Assert.True(PluginManifestReader.TryRead(json, out var accepted, out var errors), string.Join("; ", errors));
        Assert.Empty(errors);
        Assert.Equal(5, accepted!.MinimumApiVersion);
        Assert.Equal(5, accepted.MaximumApiVersion);
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
        Assert.NotEmpty(
            PluginManifestReader.Validate(Valid with { DisplayAdapters = [new DisplayAdapterMatch("8086")] }));
        Assert.NotEmpty(PluginManifestReader.Validate(Valid with { Capabilities = [CapabilityRole.GenericToggle] }));
    }

    [Theory]
    [InlineData("808")]
    [InlineData("80866")]
    [InlineData("80G6")]
    [InlineData("")]
    public void AdapterVendorIdsAreFourHexDigits(string vendor)
    {
        Assert.NotEmpty(
            PluginManifestReader.Validate(Gpu with { DisplayAdapters = [new DisplayAdapterMatch(vendor)] }));
    }

    [Fact]
    public void AdapterAndCapabilityListsRejectDuplicatesAndDeviceRoles()
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
    }

    [Fact]
    public void AdapterListsAreCheckedForShapeNotCount()
    {
        Assert.Empty(PluginManifestReader.Validate(Gpu with
        {
            DisplayAdapters =
            [
                .. Enumerable.Range(0, 40)
                    .Select(index => new DisplayAdapterMatch(index.ToString("X4", CultureInfo.InvariantCulture)))
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
