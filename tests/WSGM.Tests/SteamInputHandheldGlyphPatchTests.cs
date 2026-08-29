using WSGM.Core;

namespace WSGM.Tests;

public sealed class SteamInputHandheldGlyphPatchTests
{
    [Fact]
    public async Task KnownSteamBuildRequiresEveryUniqueControllerRouteBeforeVerification()
    {
        await using var transport = new GlyphSelectorTransport();
        await using var manager = new SteamUiPatchManager(transport);
        manager.Register(new SteamInputHandheldGlyphPatch());

        await manager.SynchronizeAsync();

        SteamUiPatchSnapshot snapshot = Assert.Single(manager.GetSnapshots());
        Assert.Equal(SteamUiPatchState.Verified, snapshot.State);
        Assert.Contains("catalog-1:selector-1", snapshot.Fingerprint, StringComparison.Ordinal);
        Assert.True(transport.Installed);
        Assert.All(transport.Expressions, expression =>
        {
            Assert.DoesNotContain("document.", expression, StringComparison.Ordinal);
            Assert.DoesNotContain("SteamClient.", expression, StringComparison.Ordinal);
            Assert.DoesNotContain("createObjectURL", expression, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task MissingControllerRouteFailsClosedBeforeInstallation()
    {
        await using var transport = new GlyphSelectorTransport { ConfigurationCount = 0 };
        await using var manager = new SteamUiPatchManager(transport);
        manager.Register(new SteamInputHandheldGlyphPatch());

        await manager.SynchronizeAsync();

        SteamUiPatchSnapshot snapshot = Assert.Single(manager.GetSnapshots());
        Assert.Equal(SteamUiPatchState.Incompatible, snapshot.State);
        Assert.False(transport.Installed);
    }

    [Fact]
    public async Task AmbiguousPromptRouteFailsClosedBeforeInstallation()
    {
        await using var transport = new GlyphSelectorTransport { MenuPromptCount = 2 };
        await using var manager = new SteamUiPatchManager(transport);
        manager.Register(new SteamInputHandheldGlyphPatch());

        await manager.SynchronizeAsync();

        SteamUiPatchSnapshot snapshot = Assert.Single(manager.GetSnapshots());
        Assert.Equal(SteamUiPatchState.Incompatible, snapshot.State);
        Assert.False(transport.Installed);
    }

    [Fact]
    public async Task DriftedSelectorResultFailsClosedBeforeInstallation()
    {
        await using var transport = new GlyphSelectorTransport { DriftSemanticPrompt = true };
        await using var manager = new SteamUiPatchManager(transport);
        manager.Register(new SteamInputHandheldGlyphPatch());

        await manager.SynchronizeAsync();

        SteamUiPatchSnapshot snapshot = Assert.Single(manager.GetSnapshots());
        Assert.Equal(SteamUiPatchState.Incompatible, snapshot.State);
        Assert.False(transport.Installed);
    }

    [Fact]
    public async Task IndependentKillSwitchRemovesOnlyOwnedSelectorState()
    {
        await using var transport = new GlyphSelectorTransport();
        await using var manager = new SteamUiPatchManager(transport);
        manager.Register(new SteamInputHandheldGlyphPatch());
        await manager.SynchronizeAsync();

        manager.SetPatchEnabled("wsgm.steam-input.handheld-glyphs", false);
        await manager.SynchronizeAsync();

        SteamUiPatchSnapshot snapshot = Assert.Single(manager.GetSnapshots());
        Assert.Equal(SteamUiPatchState.Disabled, snapshot.State);
        Assert.False(transport.Installed);
        Assert.True(transport.Removed);
    }

    [Fact]
    public void PatchTargetsOnlyValidatedSharedSteamContext()
    {
        var patch = new SteamInputHandheldGlyphPatch();

        Assert.Equal(SteamUiTargetRole.SharedJsContext, patch.TargetRole);
        Assert.Equal(1, SteamInputHandheldGlyphPatch.PatchVersion);
        Assert.Equal(1, SteamInputHandheldGlyphPatch.CatalogVersion);
        Assert.Equal(1, SteamInputHandheldGlyphPatch.SelectorVersion);
    }

    private sealed class GlyphSelectorTransport : ISteamUiTransport
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

        internal int ConfigurationCount { get; init; } = 1;

        internal int MenuPromptCount { get; init; } = 1;

        internal bool DriftSemanticPrompt { get; init; }

        internal bool Installed { get; private set; }

        internal bool Removed { get; private set; }

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
            if (expression.Contains("wsgm_handheld_glyph_probe_", StringComparison.Ordinal))
            {
                string semantic = DriftSemanticPrompt
                    ? "\"semanticPromptV2\":1"
                    : "\"semanticPrompt\":1";
                value = "{"
                    + $"\"configuration\":{ConfigurationCount},"
                    + "\"layoutEditor\":1,\"controllerSettings\":1,\"inputTest\":1,"
                    + "\"bindingGlyph\":1,"
                    + $"\"menuPrompt\":{MenuPromptCount},"
                    + semantic
                    + ",\"controllerImageContainer\":1,\"inlineShape\":1"
                    + "}";
            }
            else if (expression.Contains("Object.defineProperty", StringComparison.Ordinal))
            {
                Installed = true;
                Removed = false;
                value = "{\"ok\":true}";
            }
            else if (expression.Contains("approved.every", StringComparison.Ordinal))
            {
                value = Installed ? "{\"ok\":true}" : "{\"ok\":false}";
            }
            else if (expression.Contains("delete window[key]", StringComparison.Ordinal))
            {
                Installed = false;
                Removed = true;
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
}
