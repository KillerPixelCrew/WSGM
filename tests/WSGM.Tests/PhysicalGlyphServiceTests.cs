using System.Security.Cryptography;
using System.Text;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Device.Contracts.Glyphs;
using WSGM.Shell;

namespace WSGM.Tests;

public sealed class PhysicalGlyphServiceTests
{
    [Theory]
    [InlineData(DeviceGlyphSelection.Automatic, PhysicalGlyphSelectionMode.Automatic)]
    [InlineData(DeviceGlyphSelection.NativeSteam, PhysicalGlyphSelectionMode.NativeSteam)]
    [InlineData(DeviceGlyphSelection.ManualReviewedProfile, PhysicalGlyphSelectionMode.ManualReviewed)]
    public void PersistedSelection_MapsToClosedPhysicalMode(
        DeviceGlyphSelection persisted,
        PhysicalGlyphSelectionMode expected)
    {
        Assert.Equal(expected, DeviceCoordinator.MapGlyphSelection(persisted));
    }

    private const string NoticeHash =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void Automatic_RequiresExactProfileAndExactDeviceVerification()
    {
        ImportedGlyphProfile profile = ImportProfile(
            GlyphProfileVerification.ExactDeviceVerified,
            ["device-a"]);
        using PhysicalGlyphCatalog catalog = new();
        catalog.ReplacePackageProfiles([profile]);

        PhysicalGlyphSelectionResult exact = catalog.SelectProfile(
            true,
            PhysicalGlyphSelectionMode.Automatic,
            "device-a",
            "example.handheld",
            null);
        PhysicalGlyphSelectionResult otherDevice = catalog.SelectProfile(
            true,
            PhysicalGlyphSelectionMode.Automatic,
            "device-b",
            "example.handheld",
            null);

        Assert.Same(profile, exact.Profile);
        Assert.Null(otherDevice.Profile);
        Assert.Equal(PhysicalGlyphFallbackReason.ExactDeviceMismatch, otherDevice.FallbackReason);
    }

    [Fact]
    public void Automatic_NeverEnablesAnUnverifiedProfile()
    {
        ImportedGlyphProfile profile = ImportProfile(
            GlyphProfileVerification.Unverified,
            ["ms-1t52"]);
        using PhysicalGlyphCatalog catalog = new();
        catalog.ReplacePackageProfiles([profile]);

        PhysicalGlyphSelectionResult result = catalog.SelectProfile(
            true,
            PhysicalGlyphSelectionMode.Automatic,
            "ms-1t52",
            "example.handheld",
            null);

        Assert.Null(result.Profile);
        Assert.Equal(PhysicalGlyphFallbackReason.ProfileUnverified, result.FallbackReason);
    }

    [Fact]
    public void MissingManualProfile_FallsBackThroughAutomaticAndReportsMissing()
    {
        ImportedGlyphProfile profile = ImportProfile(
            GlyphProfileVerification.ExactDeviceVerified,
            ["device-a"]);
        using PhysicalGlyphCatalog catalog = new();
        catalog.ReplacePackageProfiles([profile]);

        PhysicalGlyphSelectionResult result = catalog.SelectProfile(
            true,
            PhysicalGlyphSelectionMode.ManualReviewed,
            "device-a",
            "example.handheld",
            "removed.profile");

        Assert.Same(profile, result.Profile);
        Assert.True(result.FellBackFromMissingManualProfile);
    }

    [Fact]
    public void DeviceIntegrationOff_AlwaysReturnsGenericOrNativeFallback()
    {
        ImportedGlyphProfile profile = ImportProfile(
            GlyphProfileVerification.ExactDeviceVerified,
            ["device-a"]);
        using PhysicalGlyphCatalog catalog = new();
        catalog.ReplacePackageProfiles([profile]);

        PhysicalGlyphSelectionResult result = catalog.SelectProfile(
            false,
            PhysicalGlyphSelectionMode.ManualReviewed,
            "device-a",
            "example.handheld",
            "example.handheld");

        Assert.Null(result.Profile);
        Assert.Equal(
            PhysicalGlyphFallbackReason.DeviceIntegrationDisabled,
            result.FallbackReason);
    }

    [Fact]
    public void DeviceDescriptionSurvivesControllerManagementOffButNavigationDoesNotMislabelExternalInput()
    {
        ImportedGlyphProfile profile = ImportProfile(
            GlyphProfileVerification.ExactDeviceVerified,
            ["device-a"]);
        using PhysicalGlyphCatalog catalog = new();
        using PhysicalGlyphService service = new(catalog);
        catalog.ReplacePackageProfiles([profile]);
        PhysicalGlyphSelectionResult selected = catalog.SelectProfile(
            true,
            PhysicalGlyphSelectionMode.Automatic,
            "device-a",
            "example.handheld",
            null);

        // Controller-management state is deliberately not an input to profile selection. Only the
        // surface authority decides whether an active external source may display it.
        PhysicalGlyphRenderPlan device = service.Resolve(
            selected,
            GlyphControlId.FaceSouth,
            PhysicalGlyphSurface.DeviceDescription,
            activeInputSourceIsManagedHandheld: false,
            steamRouteSubjectIsHandheld: false,
            PhysicalGlyphTheme.Dark,
            1);
        PhysicalGlyphRenderPlan externalNavigation = service.Resolve(
            selected,
            GlyphControlId.FaceSouth,
            PhysicalGlyphSurface.NavigationHint,
            activeInputSourceIsManagedHandheld: false,
            steamRouteSubjectIsHandheld: false,
            PhysicalGlyphTheme.Dark,
            1);

        Assert.True(device.UsesDeviceArtwork);
        Assert.False(externalNavigation.UsesDeviceArtwork);
        Assert.Equal(
            PhysicalGlyphFallbackReason.SourceNotHandheld,
            externalNavigation.FallbackReason);
    }

    [Fact]
    public void Cache_IsBoundedAndReleasedWhenPackageProfileChanges()
    {
        ImportedGlyphProfile profile = ImportProfile(
            GlyphProfileVerification.ExactDeviceVerified,
            ["device-a"]);
        using PhysicalGlyphCatalog catalog = new();
        using PhysicalGlyphService service = new(
            catalog,
            maximumCacheEntries: 1,
            maximumCacheBytes: 4096);
        catalog.ReplacePackageProfiles([profile]);
        PhysicalGlyphSelectionResult selected = catalog.SelectProfile(
            true,
            PhysicalGlyphSelectionMode.Automatic,
            "device-a",
            "example.handheld",
            null);

        _ = service.Resolve(selected, GlyphControlId.FaceSouth,
            PhysicalGlyphSurface.DeviceDescription, true, false, PhysicalGlyphTheme.Light, 1);
        _ = service.Resolve(selected, GlyphControlId.FaceSouth,
            PhysicalGlyphSurface.DeviceDescription, true, false, PhysicalGlyphTheme.Dark, 1.5);

        Assert.Equal(1, service.CachedEntryCount);
        Assert.InRange(service.CachedBytes, 1, 4096);

        catalog.ReplacePackageProfiles([]);
        Assert.Equal(0, service.CachedEntryCount);
        Assert.Equal(0, service.CachedBytes);
    }

    [Fact]
    public void PresentControlWithoutReviewedArtwork_UsesGenericFallback()
    {
        ImportedGlyphProfile profile = ImportProfile(
            GlyphProfileVerification.ExactDeviceVerified,
            ["device-a"],
            includeArtwork: false);
        using PhysicalGlyphCatalog catalog = new();
        using PhysicalGlyphService service = new(catalog);
        catalog.ReplacePackageProfiles([profile]);
        PhysicalGlyphSelectionResult selected = catalog.SelectProfile(
            true,
            PhysicalGlyphSelectionMode.Automatic,
            "device-a",
            "example.handheld",
            null);

        PhysicalGlyphRenderPlan result = service.Resolve(
            selected,
            GlyphControlId.FaceSouth,
            PhysicalGlyphSurface.DeviceDescription,
            true,
            false,
            PhysicalGlyphTheme.HighContrast,
            2);

        Assert.False(result.UsesDeviceArtwork);
        Assert.Equal(PhysicalGlyphFallbackReason.ArtworkMissing, result.FallbackReason);
    }

    private static ImportedGlyphProfile ImportProfile(
        GlyphProfileVerification verification,
        IReadOnlyList<string> exactDeviceIds,
        bool includeArtwork = true)
    {
        byte[] svg = Encoding.UTF8.GetBytes(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 64 64\">"
            + "<path d=\"M 0 0 L 64 64 Z\"/></svg>");
        string hash = Convert.ToHexString(SHA256.HashData(svg)).ToLowerInvariant();
        GlyphProfileProvenance provenance = new()
        {
            SourceId = "example.source",
            SourceRevision = "revision-1",
            License = "MIT",
            LicenseNoticeSha256 = NoticeHash,
        };
        GlyphAssetLockEntry asset = new()
        {
            Sha256 = hash,
            Format = GlyphAssetFormat.Svg,
            ByteCount = svg.Length,
            Role = GlyphAssetRole.Control,
            ViewBox = new GlyphViewBox(0, 0, 64, 64),
            Conversion = GlyphConversionKind.NormalizedVector,
            ImporterVersion = GlyphProfileImporter.CurrentImporterVersion,
            Provenance = provenance,
        };
        GlyphProfileManifest manifest = new()
        {
            SchemaVersion = GlyphProfileLimits.CurrentSchemaVersion,
            ProfileId = "example.handheld",
            DisplayName = "Example handheld",
            Revision = 1,
            Verification = verification,
            ExactDeviceIds = exactDeviceIds,
            Provenance = provenance,
            Assets = includeArtwork ? [asset] : [],
            Controls =
            [
                new GlyphControlMapping
                {
                    Control = GlyphControlId.FaceSouth,
                    Presence = GlyphControlPresence.Present,
                    AssetSha256 = includeArtwork ? hash : null,
                },
            ],
        };
        GlyphProfileImportResult result = GlyphProfileImporter.Import(
            manifest,
            new MemorySource(hash, svg));
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        return result.Profile!;
    }

    private sealed class MemorySource(string hash, byte[] bytes) : IGlyphAssetSource
    {
        public bool TryRead(string sha256, int maximumBytes, out byte[] result)
        {
            if (sha256 == hash && bytes.Length <= maximumBytes)
            {
                result = bytes.ToArray();
                return true;
            }
            result = [];
            return false;
        }
    }
}
