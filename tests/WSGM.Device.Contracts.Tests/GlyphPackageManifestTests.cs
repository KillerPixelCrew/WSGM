using WSGM.Device.Contracts.Glyphs;
using WSGM.Device.Contracts.Identity;
using WSGM.Device.Contracts.Packaging;

namespace WSGM.Device.Contracts.Tests;

public sealed class GlyphPackageManifestTests
{
    private const string Hash =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void SchemaTwo_ExactDeviceCanReferenceHashPinnedProfile()
    {
        PluginManifest manifest = Manifest() with
        {
            SchemaVersion = 2,
            GlyphProfiles =
            [
                new GlyphProfilePackageReference
                {
                    ProfileId = "example.handheld",
                    ManifestSha256 = Hash,
                },
            ],
            Devices = [Device() with { GlyphProfileId = "example.handheld" }],
        };

        Assert.Empty(PluginManifestValidator.Validate(manifest));
    }

    [Fact]
    public void SchemaOne_CannotSmuggleGlyphFieldsIntoAnOlderContract()
    {
        PluginManifest manifest = Manifest() with
        {
            GlyphProfiles =
            [
                new GlyphProfilePackageReference
                {
                    ProfileId = "example.handheld",
                    ManifestSha256 = Hash,
                },
            ],
            Devices = [Device() with { GlyphProfileId = "example.handheld" }],
        };

        Assert.Contains(
            PluginManifestValidator.Validate(manifest),
            error => error.Code == ManifestValidationCode.FieldRequiresNewerSchema);
    }

    [Theory]
    [InlineData("../profile.json")]
    [InlineData("https://example.invalid/profile")]
    [InlineData("example/handheld")]
    public void ProfileIdentifier_CannotBecomeAPathOrUrl(string profileId)
    {
        PluginManifest manifest = Manifest() with
        {
            SchemaVersion = 2,
            GlyphProfiles =
            [
                new GlyphProfilePackageReference
                {
                    ProfileId = profileId,
                    ManifestSha256 = Hash,
                },
            ],
        };

        Assert.Contains(
            PluginManifestValidator.Validate(manifest),
            error => error.Code == ManifestValidationCode.InvalidIdentifier);
    }

    [Fact]
    public void ExactDeviceReference_MustResolveInsideItsPackage()
    {
        PluginManifest manifest = Manifest() with
        {
            SchemaVersion = 2,
            Devices = [Device() with { GlyphProfileId = "missing.profile" }],
        };

        Assert.Contains(
            PluginManifestValidator.Validate(manifest),
            error => error.Code == ManifestValidationCode.UnresolvedReference);
    }

    private static PluginManifest Manifest() => new()
    {
        SchemaVersion = 1,
        Id = "wsgm.device.example",
        Version = "1.0.0",
        DisplayName = "Example",
        Publisher = "WSGM",
        MinApiVersion = 1,
        MaxApiVersion = 1,
        EntryPoint = "Example.dll",
        Devices = [Device()],
        Provenance = new PackageProvenance
        {
            Source = "Example",
            License = "GPL-3.0-or-later",
            ProvenanceClass = ProvenanceClass.IndependentCapture,
        },
    };

    private static DeviceDefinition Device() => new()
    {
        Id = "example-device",
        DisplayName = "Example device",
        Identity =
        [
            new IdentityObservation
            {
                Signal = IdentitySignal.SmbiosBaseboardProduct,
                Strength = IdentityStrength.Required,
                Values = ["EX-0001"],
            },
        ],
    };
}
