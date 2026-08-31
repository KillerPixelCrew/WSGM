using System.Text.Json;
using WSGM.Core;
using WSGM.Device.Tests;

namespace WSGM.Tests;

public sealed class SteamUiExtensionHostTests
{
    [Fact]
    public void AWellFormedExtensionLoads()
    {
        using TemporaryDirectory root = new();
        Install(root, "com.example.tidy", script: "function tidy() {}");

        SteamUiExtension extension = Assert.Single(SteamUiExtensionHost.Discover(root.Root));

        Assert.True(extension.Loaded);
        Assert.Equal("com.example.tidy", extension.Id);
        Assert.Equal("function tidy() {}", extension.Script);
    }

    [Fact]
    public void NoExtensionsInstalledIsNormalRatherThanAnError()
    {
        using TemporaryDirectory root = new();

        Assert.Empty(SteamUiExtensionHost.Discover(root.Root));
        Assert.Empty(SteamUiExtensionHost.Discover(Path.Combine(root.Root, "never-created")));
    }

    [Fact]
    public void ARejectedExtensionIsStillReportedAndNamed()
    {
        // The whole point: "my extension does nothing" with no reason anywhere is the failure this
        // subsystem exists to avoid, so a rejection is a result, never a skip.
        using TemporaryDirectory root = new();
        Directory.CreateDirectory(Path.Combine(root.Root, "broken-one"));
        File.WriteAllText(
            Path.Combine(root.Root, "broken-one", SteamUiExtensionHost.ManifestFileName),
            "{ this is not json");

        SteamUiExtension extension = Assert.Single(SteamUiExtensionHost.Discover(root.Root));

        Assert.False(extension.Loaded);
        Assert.Equal(SteamUiExtensionRejection.UnreadableManifest, extension.Rejection);
        // Named by its directory, because the manifest that would have named it is what failed.
        Assert.Equal("broken-one", extension.Id);
        Assert.False(string.IsNullOrWhiteSpace(extension.Detail));
    }

    [Fact]
    public void AnExtensionBuiltAgainstAnotherApiVersionIsRefused()
    {
        using TemporaryDirectory root = new();
        Install(root, "com.example.old", apiVersion: SteamUiExtensionHost.ApiVersion + 1);

        SteamUiExtension extension = Assert.Single(SteamUiExtensionHost.Discover(root.Root));

        Assert.Equal(SteamUiExtensionRejection.ApiVersionMismatch, extension.Rejection);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Has Spaces")]
    [InlineData("UPPERCASE")]
    [InlineData(".leading")]
    [InlineData("trailing.")]
    [InlineData("path/traversal")]
    public void AnUnsafeIdIsRefused(string id)
    {
        // The id prefixes every patch the extension may claim, so a permissive one would let an
        // extension scope its patches under a name that is not really its own.
        using TemporaryDirectory root = new();
        Install(root, id, directory: "candidate");

        SteamUiExtension extension = Assert.Single(SteamUiExtensionHost.Discover(root.Root));

        Assert.Equal(SteamUiExtensionRejection.InvalidManifest, extension.Rejection);
    }

    [Fact]
    public void APatchOutsideTheExtensionsOwnScopeIsRefused()
    {
        using TemporaryDirectory root = new();
        Install(root, "com.example.greedy", patches: ["wsgm.native-qam.tdp"]);

        SteamUiExtension extension = Assert.Single(SteamUiExtensionHost.Discover(root.Root));

        Assert.Equal(SteamUiExtensionRejection.UnscopedPatch, extension.Rejection);
    }

    [Fact]
    public void AScopedPatchIsAccepted()
    {
        using TemporaryDirectory root = new();
        Install(root, "com.example.good", patches: ["com.example.good.row"]);

        Assert.True(Assert.Single(SteamUiExtensionHost.Discover(root.Root)).Loaded);
    }

    [Fact]
    public void AScriptOutsideThePackageIsRefused()
    {
        // Without this a manifest could name ..\..\anything and have the host read and inject a
        // file it never installed.
        using TemporaryDirectory root = new();
        File.WriteAllText(Path.Combine(root.Root, "outside.js"), "stolen();");
        Install(root, "com.example.escape", script: null, scriptPath: "../outside.js");

        SteamUiExtension extension = Assert.Single(SteamUiExtensionHost.Discover(root.Root));

        Assert.Equal(SteamUiExtensionRejection.UnreadableScript, extension.Rejection);
        Assert.Contains("leaves the package", extension.Detail);
    }

    [Fact]
    public void AMissingScriptIsRefusedRatherThanLoadedEmpty()
    {
        using TemporaryDirectory root = new();
        Install(root, "com.example.absent", script: null);

        Assert.Equal(
            SteamUiExtensionRejection.UnreadableScript,
            Assert.Single(SteamUiExtensionHost.Discover(root.Root)).Rejection);
    }

    [Fact]
    public void AnOversizedScriptIsRefusedBeforeItIsRead()
    {
        // The whole asset is evaluated in one CDP call, so an unbounded script breaks every
        // surface, not only the extension's own.
        using TemporaryDirectory root = new();
        Install(
            root,
            "com.example.huge",
            script: new string('x', SteamUiExtensionHost.MaximumScriptCharacters + 1));

        SteamUiExtension extension = Assert.Single(SteamUiExtensionHost.Discover(root.Root));

        Assert.Equal(SteamUiExtensionRejection.UnreadableScript, extension.Rejection);
        Assert.Null(extension.Script);
    }

    [Fact]
    public void TwoExtensionsClaimingOneIdKeepTheFirstAndNameTheSecond()
    {
        using TemporaryDirectory root = new();
        Install(root, "com.example.dup", directory: "a-first");
        Install(root, "com.example.dup", directory: "b-second");

        IReadOnlyList<SteamUiExtension> found = SteamUiExtensionHost.Discover(root.Root);

        Assert.Equal(2, found.Count);
        Assert.True(found[0].Loaded);
        Assert.Equal(SteamUiExtensionRejection.Conflict, found[1].Rejection);
    }

    [Fact]
    public void NestedIdsCanCollideOnAPatchAndTheSecondIsNamed()
    {
        // The scope rule alone does not prevent this, which is the whole reason the patch-conflict
        // check exists. "com.example.a" may claim "com.example.a.b.row" because it carries that
        // prefix, and so may "com.example.a.b" — two distinct, individually valid ids reaching the
        // same patch. Without this check the later one would silently displace the earlier's work.
        using TemporaryDirectory root = new();
        Install(root, "com.example.a", directory: "a", patches: ["com.example.a.b.row"]);
        Install(root, "com.example.a.b", directory: "b", patches: ["com.example.a.b.row"]);

        IReadOnlyList<SteamUiExtension> found = SteamUiExtensionHost.Discover(root.Root);

        Assert.True(found[0].Loaded);
        Assert.Equal(SteamUiExtensionRejection.Conflict, found[1].Rejection);
        Assert.Contains("com.example.a.b.row", found[1].Detail);
    }

    [Fact]
    public void AnExtensionCannotClaimAPatchScopedToAnother()
    {
        // The first line of defence, before the conflict check ever matters.
        using TemporaryDirectory root = new();
        Install(root, "com.example.b", patches: ["com.example.a.row"]);

        Assert.Equal(
            SteamUiExtensionRejection.UnscopedPatch,
            Assert.Single(SteamUiExtensionHost.Discover(root.Root)).Rejection);
    }

    [Fact]
    public void DiscoveryIsOrderedSoTheSameInstallAlwaysGivesTheSameWinner()
    {
        using TemporaryDirectory root = new();
        Install(root, "com.example.z", directory: "zeta");
        Install(root, "com.example.a", directory: "alpha");

        IReadOnlyList<SteamUiExtension> found = SteamUiExtensionHost.Discover(root.Root);

        Assert.Equal(["com.example.a", "com.example.z"], found.Select(e => e.Id));
    }

    private static void Install(
        TemporaryDirectory root,
        string id,
        string? directory = null,
        int? apiVersion = null,
        string? script = "function extension() {}",
        string? scriptPath = null,
        string[]? patches = null)
    {
        string packageDirectory = Path.Combine(root.Root, directory ?? id);
        Directory.CreateDirectory(packageDirectory);
        if (script is not null && scriptPath is null)
        {
            File.WriteAllText(Path.Combine(packageDirectory, "extension.js"), script);
        }

        var manifest = new SteamUiExtensionManifest
        {
            Id = id,
            Name = "Test extension",
            Version = "1.0.0",
            ApiVersion = apiVersion ?? SteamUiExtensionHost.ApiVersion,
            Script = scriptPath ?? "extension.js",
            Patches = [.. patches ?? []],
        };
        File.WriteAllText(
            Path.Combine(packageDirectory, SteamUiExtensionHost.ManifestFileName),
            JsonSerializer.Serialize(manifest));
    }
}
