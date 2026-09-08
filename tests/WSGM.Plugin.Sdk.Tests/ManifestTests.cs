using System.Text;
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
        EntryType = "Example.Remote",
    };

    [Fact]
    public void CategoriesAreOpenAndDeviceIsAnOptionalSelectedSlot()
    {
        Assert.Empty(PluginManifestReader.Validate(Valid));
        Assert.Equal(0, PluginCategoryPolicy.Device.MinimumActive);
        Assert.Equal(1, PluginCategoryPolicy.Device.MaximumActive);
        Assert.True(PluginCategoryPolicy.Device.RequiresSelection);
        Assert.Null(PluginCategoryPolicy.Multiple.MaximumActive);
        Assert.DoesNotContain(typeof(IPlugin).Assembly.GetReferencedAssemblies(), name => name.Name!.StartsWith("WSGM.", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("../Remote.dll")]
    [InlineData("C:\\Remote.dll")]
    [InlineData("sub/Remote.dll")]
    [InlineData("Remote.dll:stream")]
    public void AssemblyTraversalAndAlternateStreamsAreRejected(string path) =>
        Assert.NotEmpty(PluginManifestReader.Validate(Valid with { EntryAssembly = path }));

    [Fact]
    public void DependenciesRejectSelfDuplicatesAndInvertedRanges()
    {
        Assert.NotEmpty(PluginManifestReader.Validate(Valid with { Dependencies = [new(Valid.Id, "1.0")] }));
        Assert.NotEmpty(PluginManifestReader.Validate(Valid with { Dependencies = [new("other", "2.0", "1.0")] }));
        Assert.NotEmpty(PluginManifestReader.Validate(Valid with { Dependencies = [new("other", "1.0"), new("other", "1.1")] }));
        Assert.Empty(PluginManifestReader.Validate(Valid with { Dependencies = [new("other", "1.0", "2.0")] }));
    }

    [Fact]
    public void CompatibilityAndUnknownJsonMembersFailBeforeLoading()
    {
        Assert.NotEmpty(PluginManifestReader.Validate(Valid with { MinimumApiVersion = 2, MaximumApiVersion = 3 }));
        byte[] json = Encoding.UTF8.GetBytes("""
            {"id":"example.remote","name":"Remote","version":"1.0","category":"example.remote",
             "entryAssembly":"Remote.dll","entryType":"Example.Remote","unexpected":true}
            """);
        Assert.False(PluginManifestReader.TryRead(json, out var rejected, out var errors));
        Assert.Null(rejected); Assert.NotEmpty(errors);
    }

    [Fact]
    public void StrictReaderAcceptsCommonMetadataAndBoundsMalformedInputs()
    {
        byte[] json = Encoding.UTF8.GetBytes("""
            {"id":"example.remote","name":"Remote","version":"1.0","category":"example.remote",
             "entryAssembly":"Remote.dll","entryType":"Example.Remote"}
            """);
        Assert.True(PluginManifestReader.TryRead(json, out var manifest, out var errors), string.Join("; ", errors));
        Assert.Empty(errors); Assert.Equal("example.remote", manifest!.Id);
        Assert.False(PluginManifestReader.TryRead(new byte[PluginManifestReader.MaximumBytes + 1], out _, out _));
        Assert.False(PluginManifestReader.TryRead("null"u8, out _, out _));
        Assert.False(PluginManifestReader.TryRead("{"u8, out _, out _));
    }
}
