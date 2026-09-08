using WSGM.Device.Tests;
using WSGM.Shell;

namespace WSGM.Tests;

public sealed class CommonPluginCatalogTests
{
    [Fact]
    public void MissingInstalledDirectoryIsAValidZeroPluginDesktop()
    {
        using TemporaryDirectory temporary = new();
        var catalog = CommonPluginCatalog.Discover(temporary.GetPath("absent"));
        Assert.Empty(catalog.Packages);
        Assert.Empty(catalog.Errors);
    }

    [Fact]
    public async Task DiscoveryChecksFilesAndIdentityWithoutExecutingPluginCode()
    {
        using TemporaryDirectory temporary = new();
        string installed = temporary.GetPath("plugins");
        foreach (string id in new[] { "valid.plugin", "wrong-folder", "missing-entry" })
        {
            string root = Path.Combine(installed, id);
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(Path.Combine(root, "plugin.wsgm.json"), $$"""
                {"id":"valid.plugin","name":"Fixture","version":"1.0","category":"example.status",
                 "entryAssembly":"Fixture.dll","entryType":"Fixture.MustNotExecute"}
                """);
            if (id != "missing-entry")
            { await File.WriteAllTextAsync(Path.Combine(root, "Fixture.dll"), "Not executable; discovery reads metadata only."); }
        }
        var catalog = CommonPluginCatalog.Discover(installed);
        Assert.Equal("valid.plugin", Assert.Single(catalog.Packages).Manifest.Id);
        Assert.Equal(2, catalog.Errors.Count);
    }
}
