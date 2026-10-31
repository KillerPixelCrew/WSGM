using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using WSGM.Core;
using WSGM.Device.Sdk.Glyphs;
using WSGM.Device.Sdk.Serialization;
using WSGM.Overlay;
using WSGM.Shell;
using WSGM.Tests.Builders;

namespace WSGM.Tests.Shell;

public sealed class PhysicalGlyphPlansTests
{
    static PhysicalGlyphPlansTests()
    {
        // Geometry.Parse uses the platform renderer even without a window. Use the real CPU Skia
        // implementation so these fixtures exercise authored paths rather than a mock geometry.
        Avalonia.Skia.SkiaPlatform.Initialize();
    }

    [Fact]
    public void Automatic_WithNoActiveDevice_ReportsTheMismatchRatherThanAProfile()
    {
        // The shape of the bug this replaced: the catalog held the package's profile and every
        // glyph surface still fell back, because the one production call site passed no device at
        // all. These tests passed throughout — they supplied a device id the coordinator never did.
        var profile = ImportProfile(["device-a"]);
        using PhysicalGlyphCatalog catalog = new();
        catalog.ReplacePackageProfiles([profile]);

        var result = catalog.SelectProfile(
            true,
            DeviceGlyphSelection.Automatic,
            null);

        Assert.Null(result.Profile);
        Assert.Equal(PhysicalGlyphFallbackReason.ExactDeviceMismatch, result.FallbackReason);
    }

    [Fact]
    public void SettingTheActiveDevice_AnnouncesTheChangeSoSurfacesReselect()
    {
        // The device definition and the profiles arrive from different places and in either order.
        // Whichever lands second has to announce itself, or every surface keeps the answer it
        // computed while the pair was still incomplete.
        var profile = ImportProfile(["device-a"]);
        using PhysicalGlyphCatalog catalog = new();
        var changes = 0;
        catalog.Changed += () => changes++;

        catalog.ReplacePackageProfiles([profile]);
        catalog.SetActiveDevice("device-a");

        Assert.Equal(2, changes);
        Assert.Same(
            profile,
            catalog.SelectProfile(true, DeviceGlyphSelection.Automatic, null).Profile);

        // Setting the same device again is not a change.
        catalog.SetActiveDevice("device-a");
        Assert.Equal(2, changes);
    }

    [Fact]
    public void Automatic_RequiresAnImportedProfileForTheExactDevice()
    {
        var profile = ImportProfile(["device-a"]);
        using PhysicalGlyphCatalog catalog = new();
        catalog.ReplacePackageProfiles([profile]);

        catalog.SetActiveDevice("device-a");
        var exact = catalog.SelectProfile(
            true,
            DeviceGlyphSelection.Automatic,
            null);
        catalog.SetActiveDevice("device-b");
        var otherDevice = catalog.SelectProfile(
            true,
            DeviceGlyphSelection.Automatic,
            null);

        Assert.Same(profile, exact.Profile);
        Assert.Null(otherDevice.Profile);
        Assert.Equal(PhysicalGlyphFallbackReason.ExactDeviceMismatch, otherDevice.FallbackReason);
    }

    [Fact]
    public void MissingManualProfile_FallsBackThroughAutomaticAndReportsMissing()
    {
        var profile = ImportProfile(["device-a"]);
        using PhysicalGlyphCatalog catalog = new();
        catalog.ReplacePackageProfiles([profile]);

        catalog.SetActiveDevice("device-a");
        var result = catalog.SelectProfile(
            true,
            DeviceGlyphSelection.ManualReviewedProfile,
            "removed.profile");

        Assert.Same(profile, result.Profile);
        Assert.True(result.FellBackFromMissingManualProfile);
    }

    [Fact]
    public void DeviceIntegrationOff_AlwaysReturnsGenericOrNativeFallback()
    {
        var profile = ImportProfile(["device-a"]);
        using PhysicalGlyphCatalog catalog = new();
        catalog.ReplacePackageProfiles([profile]);

        catalog.SetActiveDevice("device-a");
        var result = catalog.SelectProfile(
            false,
            DeviceGlyphSelection.ManualReviewedProfile,
            "example.handheld");

        Assert.Null(result.Profile);
        Assert.Equal(
            PhysicalGlyphFallbackReason.DeviceIntegrationDisabled,
            result.FallbackReason);
    }

    [Fact]
    public void GlyphSelectionViewReportsGenericFallbackWithoutClaimingDeviceArtwork()
    {
        var row = DeviceHostRows.PhysicalGlyphSelectionView(
            DeviceGlyphSelection.Automatic,
            new PhysicalGlyphSelectionResult(
                null,
                PhysicalGlyphFallbackReason.ExactDeviceMismatch,
                false));

        Assert.Equal(DescriptorStatus.Warning, row.Status);
        Assert.Equal("AUTO", row.TrailingText);
        Assert.Contains("generic glyphs", row.Description, StringComparison.OrdinalIgnoreCase);
        Assert.True(row.CanInvoke);
    }

    [Fact]
    public void DeviceDescriptionSurvivesControllerManagementOffButNavigationDoesNotMislabelExternalInput()
    {
        var profile = ImportProfile(["device-a"]);
        using PhysicalGlyphCatalog catalog = new();
        using PhysicalGlyphPlans service = new(catalog);
        catalog.ReplacePackageProfiles([profile]);
        catalog.SetActiveDevice("device-a");
        var selected = catalog.SelectProfile(
            true,
            DeviceGlyphSelection.Automatic,
            null);

        // Controller-management state is deliberately not an input to profile selection. Only the
        // surface authority decides whether an active external source may display it.
        var device = service.Resolve(
            selected,
            GlyphControlId.FaceSouth,
            PhysicalGlyphSurface.DeviceDescription,
            false);
        var externalNavigation = service.Resolve(
            selected,
            GlyphControlId.FaceSouth,
            PhysicalGlyphSurface.NavigationHint,
            false);

        Assert.True(device.UsesDeviceArtwork);
        Assert.False(externalNavigation.UsesDeviceArtwork);
        Assert.Equal(
            PhysicalGlyphFallbackReason.SourceNotHandheld,
            externalNavigation.FallbackReason);
    }

    [Fact]
    public void Cache_KeepsOnePlanPerControlAndIsReleasedWhenPackageProfileChanges()
    {
        var profile = ImportProfile(["device-a"]);
        using PhysicalGlyphCatalog catalog = new();
        using PhysicalGlyphPlans service = new(catalog);
        catalog.ReplacePackageProfiles([profile]);
        catalog.SetActiveDevice("device-a");
        var selected = catalog.SelectProfile(
            true,
            DeviceGlyphSelection.Automatic,
            null);

        var description = service.Resolve(selected, GlyphControlId.FaceSouth,
            PhysicalGlyphSurface.DeviceDescription, true);
        var hint = service.Resolve(selected, GlyphControlId.FaceSouth,
            PhysicalGlyphSurface.NavigationHint, true);

        Assert.Same(description, hint);
        Assert.Equal(1, service.CachedEntryCount);

        catalog.ReplacePackageProfiles([]);
        Assert.Equal(0, service.CachedEntryCount);
    }

    [Fact]
    public void PresentControlWithoutReviewedArtwork_UsesGenericFallback()
    {
        var profile = ImportProfile(["device-a"], false);
        using PhysicalGlyphCatalog catalog = new();
        using PhysicalGlyphPlans service = new(catalog);
        catalog.ReplacePackageProfiles([profile]);
        catalog.SetActiveDevice("device-a");
        var selected = catalog.SelectProfile(
            true,
            DeviceGlyphSelection.Automatic,
            null);

        var result = service.Resolve(
            selected,
            GlyphControlId.FaceSouth,
            PhysicalGlyphSurface.DeviceDescription,
            true);

        Assert.False(result.UsesDeviceArtwork);
        Assert.Equal(PhysicalGlyphFallbackReason.ArtworkMissing, result.FallbackReason);
    }

    private static ImportedGlyphProfile ImportProfile(
        IReadOnlyList<string> exactDeviceIds,
        bool includeArtwork = true)
    {
        var artwork = OnePixelPng();
        const string assetId = "face-south";
        GlyphAssetEntry asset = new()
        {
            AssetId = assetId,
            Format = GlyphAssetFormat.Png,
            Role = GlyphAssetRole.Control,
            PixelWidth = 1,
            PixelHeight = 1
        };
        GlyphProfileManifest manifest = new()
        {
            SchemaVersion = GlyphProfileLimits.CurrentSchemaVersion,
            ProfileId = "example.handheld",
            DisplayName = "Example handheld",
            Revision = 1,
            ExactDeviceIds = exactDeviceIds,
            SourceRevision = "revision-1",
            NoticePath = "THIRD_PARTY_NOTICES.md",
            Assets = includeArtwork ? [asset] : [],
            Controls =
            [
                new GlyphControlMapping
                {
                    Control = GlyphControlId.FaceSouth,
                    Presence = GlyphControlPresence.Present,
                    AssetId = includeArtwork ? assetId : null
                }
            ]
        };
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal)
        {
            [GlyphPackageLayout.ProfileManifest(manifest.ProfileId)] =
                JsonSerializer.SerializeToUtf8Bytes(
                    manifest,
                    GlyphJsonContext.Default.GlyphProfileManifest),
            [manifest.NoticePath] = [.. "Example glyph notice\n"u8]
        };
        if (includeArtwork)
        {
            files[GlyphPackageLayout.Asset(assetId, GlyphAssetFormat.Png)] = artwork;
        }

        var result = GlyphPackageImporter.Import(
            new GlyphTestPackageSource(manifest.ProfileId, files));
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        return Assert.Single(result.Profiles);
    }

    [Fact]
    public void EveryEmbeddedDefinitionImportsAndItsActualControlOutlinesParse()
    {
        var assembly = typeof(LibHandheld.HandheldDevice).Assembly;
        var resources = assembly.GetManifestResourceNames();
        foreach (var definition in LibHandheld.HandheldDevice.Definitions)
        {
            var manifestResource = Assert.Single(resources, name =>
                name.Contains(".glyphs.profiles.", StringComparison.Ordinal)
                && name.EndsWith("." + definition.GlyphProfileId + ".json", StringComparison.Ordinal));
            var imported = GlyphPackageImporter.Import(new EmbeddedProfileSource(manifestResource));
            Assert.True(imported.IsValid, definition.Id + ": " + string.Join("; ", imported.Errors));
            var profile = Assert.Single(imported.Profiles);
            Assert.Contains(definition.Id, profile.Manifest.ExactDeviceIds);
            using PhysicalGlyphCatalog catalog = new();
            using PhysicalGlyphPlans plans = new(catalog);
            var selected = new PhysicalGlyphSelectionResult(profile, PhysicalGlyphFallbackReason.None, false);
            foreach (var mapping in profile.Manifest.Controls.Where(mapping =>
                         mapping.Presence is GlyphControlPresence.Present && mapping.AssetId is not null))
            {
                var plan = plans.Resolve(selected, mapping.Control, PhysicalGlyphSurface.DeviceDescription, true);
                Assert.True(plan.UsesDeviceArtwork,
                    $"{definition.Id}/{mapping.Control}: {plan.FallbackReason}");
                if (profile.Assets[mapping.AssetId!].Vector is not null)
                {
                    Assert.NotEmpty(plan.Paths);
                }

                if (mapping.OemControlId is { } nativeId)
                {
                    Assert.Same(plan, plans.ResolveOem(selected, nativeId,
                        PhysicalGlyphSurface.DeviceDescription, true));
                }
            }

            var steam = SteamInputGlyphPresentation.Create(profile);
            Assert.NotNull(steam);
            if (definition.HasController)
            {
                Assert.NotEmpty(steam.StableResources);
            }
        }
    }

    [Fact]
    public void OemLookupUsesExactSelectedProfileMetadataAndHonorsInputAuthority()
    {
        var profile = ImportProfile(["device-a"]);
        profile = profile with
        {
            Manifest = profile.Manifest with
            {
                Controls =
                [
                    profile.Manifest.Controls[0] with { OemControlId = "native-front" }
                ]
            }
        };
        using PhysicalGlyphCatalog catalog = new();
        using PhysicalGlyphPlans plans = new(catalog);
        var selected = new PhysicalGlyphSelectionResult(profile, PhysicalGlyphFallbackReason.None, false);
        Assert.True(PhysicalGlyphPlans.TryGetOemControl(selected, "native-front", out var control));
        Assert.Equal(GlyphControlId.FaceSouth, control);
        Assert.False(PhysicalGlyphPlans.TryGetOemControl(selected, "NATIVE-FRONT", out _));
        Assert.False(PhysicalGlyphPlans.TryGetOemControl(selected, "FaceSouth", out _));
        Assert.True(plans.ResolveOem(selected, "native-front",
            PhysicalGlyphSurface.DeviceDescription, false).UsesDeviceArtwork);
        Assert.Equal(PhysicalGlyphFallbackReason.SourceNotHandheld,
            plans.ResolveOem(selected, "native-front", PhysicalGlyphSurface.NavigationHint, false).FallbackReason);
    }

    private sealed class EmbeddedProfileSource(string manifestResource) : IGlyphPackageSource
    {
        private readonly string _family = manifestResource[
            ..(manifestResource.IndexOf(".glyphs.profiles.", StringComparison.Ordinal) + 1)];

        private readonly string _profileId = manifestResource[
            (manifestResource.IndexOf(".glyphs.profiles.", StringComparison.Ordinal) + ".glyphs.profiles.".Length)..^5];

        public IReadOnlyList<string> EnumerateProfileIds()
        {
            return [_profileId];
        }

        public bool TryRead(string relativePath, int maximumBytes, out byte[] bytes)
        {
            using var resource = typeof(LibHandheld.HandheldDevice).Assembly.GetManifestResourceStream(
                _family + relativePath.Replace('/', '.'));
            if (resource is null || resource.Length > maximumBytes)
            {
                bytes = [];
                return false;
            }

            using MemoryStream output = new();
            resource.CopyTo(output);
            bytes = output.ToArray();
            return true;
        }
    }

    private static byte[] OnePixelPng()
    {
        using MemoryStream output = new();
        output.Write([137, 80, 78, 71, 13, 10, 26, 10]);

        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), 1);
        header[8] = 8;
        header[9] = 6;
        WritePngChunk(output, "IHDR", header);

        using MemoryStream compressed = new();
        using (ZLibStream zlib = new(compressed, CompressionLevel.SmallestSize, true))
        {
            zlib.Write([0, 255, 0, 0, 255]);
        }

        WritePngChunk(output, "IDAT", compressed.ToArray());
        WritePngChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void WritePngChunk(Stream output, string type, byte[] data)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        output.Write(length);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);

        byte[] crcInput = [.. typeBytes, .. data];
        var crc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(crcInput));
        output.Write(crc);
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                var mask = 0u - (crc & 1u);
                crc = (crc >> 1) ^ (0xedb88320u & mask);
            }
        }

        return ~crc;
    }
}
