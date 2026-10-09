namespace WSGM.Plugin.Sdk.Tests;

public sealed class FrontendManifestTests
{
    private static PluginManifest Manifest(bool optIn, params PluginFrontendModule[] modules)
    {
        return new PluginManifest
        {
            Id = "example.frontend", Name = "Frontend", Version = "1.0.0", Category = "example.frontend",
            EntryAssembly = "Frontend.dll", EntryType = "Example.Frontend", SteamCef = optIn, FrontendModules = modules
        };
    }

    [Fact]
    public void FrontendBundlesRequireDeclaredCefAccess()
    {
        var module = new PluginFrontendModule("page", "frontend/page.js", "frontend/page.css");
        Assert.NotEmpty(PluginManifestReader.Validate(Manifest(false, module)));
        Assert.Empty(PluginManifestReader.Validate(Manifest(true, module)));
        Assert.NotEmpty(PluginManifestReader.Validate(Manifest(true, module, module)));
    }

    [Theory]
    [InlineData("../page.js")]
    [InlineData("/page.js")]
    [InlineData("frontend\\page.js")]
    [InlineData("https://example.com/page.js")]
    public void BundlesCannotEscapeTheirPackage(string path)
    {
        Assert.NotEmpty(PluginManifestReader.Validate(Manifest(true, new PluginFrontendModule("page", path))));
    }
}
