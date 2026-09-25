using System.Text;
using WSGM.Core;
using WSGM.Device.Tests;
using WSGM.Install;

namespace WSGM.Tests.Core;

public sealed class UpdateCheckerTests
{
    private static byte[] Release(string tag, bool prerelease = false, params string[] assets)
    {
        var list = string.Join(",", assets.Select(name =>
            $$"""{"name":"{{name}}","browser_download_url":"https://github.com/KillerPixelCrew/WSGM/releases/download/{{tag}}/{{name}}"}"""));
        return Encoding.UTF8.GetBytes(
            $$"""{"tag_name":"{{tag}}","draft":false,"prerelease":{{(prerelease ? "true" : "false")}},"html_url":"https://github.com/KillerPixelCrew/WSGM/releases/tag/{{tag}}","assets":[{{list}}]}""");
    }

    private static BundledPlugin Plugin(string id, string origin = "community", string? contact = "dev@example.com")
    {
        return new BundledPlugin
        {
            Id = id,
            Name = id + " plugin",
            Version = "1.0.0",
            Category = "wsgm.common",
            Origin = origin,
            Validation = "blind",
            Contact = contact,
            File = id + "-1.0.0.wsgmpkg",
            Sha256 = new string('a', 64)
        };
    }

    [Fact]
    public void LatestRelease_FindsTheSetupItsHashAndTheBundle()
    {
        var release = UpdateChecker.ParseLatestRelease(Release("v2.1.0", false,
            "WSGM-Setup-2.1.0.exe", "WSGM-Setup-2.1.0.exe.sha256", "bundle.json"));

        Assert.NotNull(release);
        Assert.Equal("2.1.0", release.Version);
        Assert.EndsWith("/WSGM-Setup-2.1.0.exe", release.SetupUrl, StringComparison.Ordinal);
        Assert.EndsWith("/WSGM-Setup-2.1.0.exe.sha256", release.HashUrl, StringComparison.Ordinal);
        Assert.EndsWith("/bundle.json", release.BundleUrl, StringComparison.Ordinal);
    }

    [Fact]
    public void LatestRelease_IgnoresPrereleasesAndReleasesWithoutAVerifiableSetup()
    {
        Assert.Null(UpdateChecker.ParseLatestRelease(Release("v2.1.0", true,
            "WSGM-Setup-2.1.0.exe", "WSGM-Setup-2.1.0.exe.sha256")));
        Assert.Null(UpdateChecker.ParseLatestRelease(Release("v2.1.0", false, "WSGM-Setup-2.1.0.exe")));
        Assert.Null(UpdateChecker.ParseLatestRelease(Encoding.UTF8.GetBytes(
            """{"tag_name":"v2.1.0","assets":[{"name":"WSGM-Setup-2.1.0.exe","browser_download_url":"http://example.com/a"},{"name":"WSGM-Setup-2.1.0.exe.sha256","browser_download_url":"http://example.com/b"}]}""")));
    }

    [Theory]
    [InlineData("2.0.1", true)]
    [InlineData("2.1", true)]
    [InlineData("3", true)]
    [InlineData("2.0.0", false)]
    [InlineData("1.9.9", false)]
    [InlineData("2.0.1-rc1", true)]
    [InlineData("not-a-version", false)]
    public void Newer_ComparesTheNumericCore(string candidate, bool newer)
    {
        Assert.Equal(newer, UpdateChecker.IsNewer(candidate, new Version(2, 0, 0, 0)));
    }

    [Fact]
    public void Hash_ReadsTheSha256SumFormat()
    {
        var hash = new string('A', 64);

        Assert.Equal(hash.ToLowerInvariant(), UpdateChecker.ParseHash($"{hash}  WSGM-Setup-2.1.0.exe"));
        Assert.Null(UpdateChecker.ParseHash("not a hash"));
        Assert.Null(UpdateChecker.ParseHash(""));
    }

    [Fact]
    public void Warnings_NameInstalledCommunityPluginsTheReleaseDropped()
    {
        BundleManifest installed = new()
        {
            SchemaVersion = 1,
            WsgmVersion = "2.0.0",
            Plugins = [Plugin("kept"), Plugin("dropped"), Plugin("first", "first-party"), Plugin("not-installed")]
        };
        BundleManifest next = new()
        {
            SchemaVersion = 1,
            WsgmVersion = "2.1.0",
            Plugins = [Plugin("kept")],
            Outdated =
            [
                new OutdatedPlugin { Id = "dropped", Contact = "new@example.com", Log = "https://example.com/log" }
            ]
        };

        var warnings = UpdateChecker.Warnings(installed, ["kept", "dropped", "first"], next);

        var warning = Assert.Single(warnings);
        Assert.Equal("dropped", warning.PluginId);
        Assert.Equal("new@example.com", warning.Contact);
        Assert.Equal("https://example.com/log", warning.Log);
        Assert.Empty(UpdateChecker.Warnings(null, ["dropped"], next));
    }

    [Fact]
    public void State_RoundTripsThroughItsFile()
    {
        using TemporaryDirectory directory = new();
        var path = Path.Combine(directory.Root, "update.json");
        UpdateState state = new()
        {
            LastCheckUtc = new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero),
            Offer = new UpdateOffer(
                new UpdateRelease("2.1.0", "https://page", "WSGM-Setup-2.1.0.exe", "https://setup", "https://hash",
                    null),
                [new UpdateWarning("dropped", "Dropped", "dev@example.com", null)])
        };

        UpdateChecker.WriteState(state, path);
        var read = UpdateChecker.ReadState(path);

        Assert.Equal(state.LastCheckUtc, read.LastCheckUtc);
        Assert.Equal("2.1.0", read.Offer?.Release.Version);
        Assert.Equal("dev@example.com", Assert.Single(read.Offer!.Warnings).Contact);
        Assert.Null(UpdateChecker.ReadState(Path.Combine(directory.Root, "missing.json")).LastCheckUtc);
    }
}
