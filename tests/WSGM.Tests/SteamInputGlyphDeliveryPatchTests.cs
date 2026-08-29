using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WSGM.Core;
using WSGM.Device.Sdk.Glyphs;
using WSGM.Device.Sdk.Ipc;

namespace WSGM.Tests;

public sealed class SteamInputGlyphDeliveryPatchTests
{
    [Fact]
    public void ImportedProfileProducesOnlyCatalogOwnedExactMappings()
    {
        ImportedGlyphProfile profile = ImportProfile();

        SteamInputGlyphPresentation? presentation = SteamInputGlyphPresentation.Create(profile);

        Assert.NotNull(presentation);
        Assert.Equal("example.handheld", presentation.ProfileId);
        Assert.Equal(2, presentation.StableResources.Count);
        Assert.All(presentation.StableResources, mapping =>
        {
            Assert.Equal(GlyphControlId.FaceSouth, mapping.Control);
            Assert.StartsWith("/steaminputglyphs/", mapping.ValvePath, StringComparison.Ordinal);
            Assert.StartsWith("data:image/svg+xml;base64,", mapping.Asset.DataUri, StringComparison.Ordinal);
        });
        Assert.Equal("full", Assert.Single(presentation.ControllerImages).Slot);
        Assert.Empty(presentation.InlineMappings);
        Assert.Equal(GlyphControlId.LeftTrackpad, Assert.Single(presentation.AbsentControls));
    }

    [Fact]
    public async Task StableAndStructuralTiersRemainHealthyWhenInlineAndCapabilityFailClosed()
    {
        SteamInputGlyphDeliveryState state = new();
        state.Update(ImportProfile());
        await using var transport = new GlyphTierTransport();
        await using var manager = new SteamUiPatchManager(transport);
        manager.Register(new SteamInputStableResourceGlyphPatch(state));
        manager.Register(new SteamInputControllerImageGlyphPatch(state));
        manager.Register(new NonGlyphFixturePatch());
        manager.Register(new SteamInputInlineValveSvgGlyphPatch(state));
        manager.Register(new SteamInputCapabilityHidingGlyphPatch(state));

        await manager.SynchronizeAsync();

        IReadOnlyDictionary<string, SteamUiPatchSnapshot> snapshots = manager.GetSnapshots()
            .ToDictionary(snapshot => snapshot.Id, StringComparer.Ordinal);
        Assert.Equal(
            SteamUiPatchState.Verified,
            snapshots[SteamInputStableResourceGlyphPatch.PatchId].State);
        Assert.Equal(
            SteamUiPatchState.Verified,
            snapshots[SteamInputControllerImageGlyphPatch.PatchId].State);
        Assert.Equal(SteamUiPatchState.Verified, snapshots["fixture.non-glyph"].State);
        Assert.Equal(
            SteamUiPatchState.Incompatible,
            snapshots[SteamInputInlineValveSvgGlyphPatch.PatchId].State);
        Assert.Equal(
            SteamUiPatchState.Incompatible,
            snapshots[SteamInputCapabilityHidingGlyphPatch.PatchId].State);
        Assert.True(transport.ResourceInstalled);
        Assert.True(transport.ControllerImageInstalled);
        Assert.DoesNotContain(
            transport.Expressions,
            expression => expression.Contains("document.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task IndependentKillSwitchRemovesOnlyOneTier()
    {
        SteamInputGlyphDeliveryState state = new();
        state.Update(ImportProfile());
        await using var transport = new GlyphTierTransport();
        await using var manager = new SteamUiPatchManager(transport);
        manager.Register(new SteamInputStableResourceGlyphPatch(state));
        manager.Register(new SteamInputControllerImageGlyphPatch(state));
        await manager.SynchronizeAsync();

        manager.SetPatchEnabled(SteamInputStableResourceGlyphPatch.PatchId, false);
        await manager.SynchronizeAsync();

        IReadOnlyDictionary<string, SteamUiPatchSnapshot> snapshots = manager.GetSnapshots()
            .ToDictionary(snapshot => snapshot.Id, StringComparer.Ordinal);
        Assert.Equal(
            SteamUiPatchState.Disabled,
            snapshots[SteamInputStableResourceGlyphPatch.PatchId].State);
        Assert.Equal(
            SteamUiPatchState.Verified,
            snapshots[SteamInputControllerImageGlyphPatch.PatchId].State);
        Assert.False(transport.ResourceInstalled);
        Assert.True(transport.ControllerImageInstalled);
    }

    [Fact]
    public async Task AmbiguousStableResourceProbeDoesNotDisableStructuralTier()
    {
        SteamInputGlyphDeliveryState state = new();
        state.Update(ImportProfile());
        await using var transport = new GlyphTierTransport(resourceProbeCompatible: false);
        await using var manager = new SteamUiPatchManager(transport);
        manager.Register(new SteamInputStableResourceGlyphPatch(state));
        manager.Register(new SteamInputControllerImageGlyphPatch(state));

        await manager.SynchronizeAsync();

        IReadOnlyDictionary<string, SteamUiPatchSnapshot> snapshots = manager.GetSnapshots()
            .ToDictionary(snapshot => snapshot.Id, StringComparer.Ordinal);
        Assert.Equal(
            SteamUiPatchState.Incompatible,
            snapshots[SteamInputStableResourceGlyphPatch.PatchId].State);
        Assert.Equal(
            SteamUiPatchState.Verified,
            snapshots[SteamInputControllerImageGlyphPatch.PatchId].State);
        Assert.False(transport.ResourceInstalled);
        Assert.True(transport.ControllerImageInstalled);
    }

    [Fact]
    public async Task DriftedControllerImageProbeDoesNotDisableStableResourceTier()
    {
        SteamInputGlyphDeliveryState state = new();
        state.Update(ImportProfile());
        await using var transport = new GlyphTierTransport(controllerImageProbeCompatible: false);
        await using var manager = new SteamUiPatchManager(transport);
        manager.Register(new SteamInputStableResourceGlyphPatch(state));
        manager.Register(new SteamInputControllerImageGlyphPatch(state));

        await manager.SynchronizeAsync();

        IReadOnlyDictionary<string, SteamUiPatchSnapshot> snapshots = manager.GetSnapshots()
            .ToDictionary(snapshot => snapshot.Id, StringComparer.Ordinal);
        Assert.Equal(
            SteamUiPatchState.Verified,
            snapshots[SteamInputStableResourceGlyphPatch.PatchId].State);
        Assert.Equal(
            SteamUiPatchState.Incompatible,
            snapshots[SteamInputControllerImageGlyphPatch.PatchId].State);
        Assert.True(transport.ResourceInstalled);
        Assert.False(transport.ControllerImageInstalled);
    }

    [Fact]
    public async Task MissingReviewedProfileKeepsEveryTierNative()
    {
        SteamInputGlyphDeliveryState state = new();
        await using var transport = new GlyphTierTransport();
        await using var manager = new SteamUiPatchManager(transport);
        manager.Register(new SteamInputStableResourceGlyphPatch(state));
        manager.Register(new SteamInputControllerImageGlyphPatch(state));
        manager.Register(new SteamInputInlineValveSvgGlyphPatch(state));
        manager.Register(new SteamInputCapabilityHidingGlyphPatch(state));

        await manager.SynchronizeAsync();

        Assert.All(
            manager.GetSnapshots(),
            snapshot => Assert.Equal(SteamUiPatchState.Incompatible, snapshot.State));
        Assert.False(transport.ResourceInstalled);
        Assert.False(transport.ControllerImageInstalled);
    }

    private static ImportedGlyphProfile ImportProfile()
    {
        byte[] controlSvg = Encoding.UTF8.GetBytes(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 64 64\">"
            + "<path d=\"M 0 0 L 64 64 Z\"/></svg>");
        byte[] controllerSvg = Encoding.UTF8.GetBytes(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 128 64\">"
            + "<path d=\"M 0 0 L 128 64 Z\"/></svg>");
        string controlHash = Hash(controlSvg);
        string controllerHash = Hash(controllerSvg);
        GlyphAssetLockEntry controlAsset = Asset(
            controlHash,
            controlSvg.Length,
            GlyphAssetRole.Control,
            new GlyphViewBox(0, 0, 64, 64));
        GlyphAssetLockEntry controllerAsset = Asset(
            controllerHash,
            controllerSvg.Length,
            GlyphAssetRole.FullController,
            new GlyphViewBox(0, 0, 128, 64));
        GlyphProfileManifest manifest = new()
        {
            SchemaVersion = GlyphProfileLimits.CurrentSchemaVersion,
            ProfileId = "example.handheld",
            DisplayName = "Example handheld",
            Revision = 4,
            ExactDeviceIds = ["example-device"],
            SourceRevision = "revision-1",
            NoticePath = "THIRD_PARTY_NOTICES.md",
            Assets = [controlAsset, controllerAsset],
            ControllerImages = new GlyphControllerImages { FullSha256 = controllerHash },
            Controls =
            [
                new GlyphControlMapping
                {
                    Control = GlyphControlId.FaceSouth,
                    Presence = GlyphControlPresence.Present,
                    AssetSha256 = controlHash,
                },
                new GlyphControlMapping
                {
                    Control = GlyphControlId.LeftTrackpad,
                    Presence = GlyphControlPresence.Absent,
                },
            ],
        };
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal)
        {
            [GlyphPackageLayout.ProfileManifest(manifest.ProfileId)] =
                JsonSerializer.SerializeToUtf8Bytes(
                    manifest,
                    DeviceWireJsonContext.Default.GlyphProfileManifest),
            [manifest.NoticePath] = Encoding.UTF8.GetBytes("Example glyph notice\n"),
            [GlyphPackageLayout.Asset(controlHash, GlyphAssetFormat.Svg)] = controlSvg,
            [GlyphPackageLayout.Asset(controllerHash, GlyphAssetFormat.Svg)] = controllerSvg,
        };
        GlyphPackageImportResult result = GlyphPackageImporter.Import(
            new MemoryPackageSource(manifest.ProfileId, files));
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        return Assert.Single(result.Profiles);
    }

    private static GlyphAssetLockEntry Asset(
        string hash,
        int byteCount,
        GlyphAssetRole role,
        GlyphViewBox viewBox) => new()
        {
            Sha256 = hash,
            Format = GlyphAssetFormat.Svg,
            ByteCount = byteCount,
            Role = role,
            ViewBox = viewBox,
        };

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class MemoryPackageSource(
        string profileId,
        IReadOnlyDictionary<string, byte[]> files) : IGlyphPackageSource
    {
        public IReadOnlyList<string> EnumerateProfileIds() => [profileId];

        public bool TryRead(string relativePath, int maximumBytes, out byte[] bytes)
        {
            if (files.TryGetValue(relativePath, out byte[]? asset)
                && asset.Length <= maximumBytes)
            {
                bytes = asset.ToArray();
                return true;
            }

            bytes = [];
            return false;
        }
    }

    private sealed class GlyphTierTransport(
        bool resourceProbeCompatible = true,
        bool controllerImageProbeCompatible = true) : ISteamUiTransport
    {
        public event EventHandler<SteamUiNotification>? NotificationReceived
        {
            add { }
            remove { }
        }

        public event EventHandler<SteamUiTransportSnapshot>? GenerationChanged
        {
            add { }
            remove { }
        }

        internal bool ResourceInstalled { get; private set; }

        internal bool ControllerImageInstalled { get; private set; }

        internal List<string> Expressions { get; } = [];

        public ValueTask<IAsyncDisposable> SubscribeAsync(
            SteamUiTargetRole role,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IAsyncDisposable>(new Lease());

        public Task<SteamUiEvaluationResult> EvaluateAsync(
            SteamUiTargetRole role,
            string expression,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            Expressions.Add(expression);
            string value;
            if (expression.Contains("wsgm_glyph_resource_probe_", StringComparison.Ordinal))
            {
                value = resourceProbeCompatible ? "{\"ok\":true}" : "{\"ok\":false}";
            }
            else if (expression.Contains("wsgm_controller_image_probe_", StringComparison.Ordinal))
            {
                value = controllerImageProbeCompatible
                    ? "{\"ok\":true}"
                    : "{\"ok\":false}";
            }
            else if (expression.Contains("no audited inline", StringComparison.Ordinal)
                || expression.Contains("exact capability control-set", StringComparison.Ordinal))
            {
                value = "{\"ok\":false}";
            }
            else if (expression.Contains("Object.defineProperty", StringComparison.Ordinal))
            {
                if (expression.Contains("__wsgmSteamInputGlyphResources_3a19cd7e", StringComparison.Ordinal))
                {
                    ResourceInstalled = true;
                }
                if (expression.Contains("__wsgmSteamInputControllerImages_91a5d482", StringComparison.Ordinal))
                {
                    ControllerImageInstalled = true;
                }
                value = "{\"ok\":true}";
            }
            else if (expression.Contains("resolvedReference", StringComparison.Ordinal))
            {
                value = "{\"ok\":true}";
            }
            else if (expression.Contains("delete window[key]", StringComparison.Ordinal))
            {
                if (expression.Contains("__wsgmSteamInputGlyphResources_3a19cd7e", StringComparison.Ordinal))
                {
                    ResourceInstalled = false;
                }
                if (expression.Contains("__wsgmSteamInputControllerImages_91a5d482", StringComparison.Ordinal))
                {
                    ControllerImageInstalled = false;
                }
                value = "{\"ok\":true}";
            }
            else
            {
                value = "{\"ok\":false}";
            }

            return Task.FromResult(new SteamUiEvaluationResult(
                true,
                value,
                null,
                new SteamUiGenerations(1, 1, 1, 1, 1, 1)));
        }

        public IReadOnlyList<SteamUiTransportSnapshot> GetSnapshots() =>
        [
            new(
                SteamUiTargetRole.SharedJsContext,
                SteamUiTransportHealth.Ready,
                new SteamUiGenerations(1, 1, 1, 1, 1, 1),
                "shared-js-fixture",
                null,
                0,
                1),
        ];

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class Lease : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class NonGlyphFixturePatch : ISteamUiPatch
    {
        public string Id => "fixture.non-glyph";

        public int Version => 1;

        public SteamUiTargetRole TargetRole => SteamUiTargetRole.SharedJsContext;

        public string ResourceKey => "fixture.non-glyph-resource";

        public SteamUiPatchBounds Bounds => SteamUiPatchBounds.Default;

        public Task<SteamUiPatchProbeResult> ProbeAsync(
            SteamUiPatchContext context,
            CancellationToken cancellationToken) => Task.FromResult(
                new SteamUiPatchProbeResult(true, true, true, "fixture-v1", null));

        public Task<SteamUiPatchOperationResult> ApplyAsync(
            SteamUiPatchContext context,
            CancellationToken cancellationToken) => Task.FromResult(
                new SteamUiPatchOperationResult(true, null));

        public Task<SteamUiPatchOperationResult> VerifyAsync(
            SteamUiPatchContext context,
            CancellationToken cancellationToken) => Task.FromResult(
                new SteamUiPatchOperationResult(true, null));

        public Task<SteamUiPatchOperationResult> RemoveAsync(
            SteamUiPatchContext context,
            CancellationToken cancellationToken) => Task.FromResult(
                new SteamUiPatchOperationResult(true, null));
    }
}
