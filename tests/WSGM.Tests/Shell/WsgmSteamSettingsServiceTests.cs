using System.Text.Json;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Settings;
using WSGM.Plugin.Sdk;
using WSGM.Shell;
using Xunit.Sdk;

namespace WSGM.Tests.Shell;

/// <summary>
///     WSGM's settings page in Steam: which settings it offers, and that each change writes one field
///     through the store, the way WSGM Settings would, and nothing else.
/// </summary>
public sealed class WsgmSteamSettingsServiceTests
{
    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static SteamSettingsRow Row(WsgmSteamSettingsState state, string key)
    {
        return state.Pages.SelectMany(page => page.Sections).SelectMany(section => section.Rows)
                   .FirstOrDefault(row => row.Key == key)
               ?? throw new XunitException($"No row {key}.");
    }

    [Fact]
    public async Task AToggleWritesItsOneFieldAndThePageShowsItBeforeTheReload()
    {
        Harness harness = new();
        harness.Stored.Cef.WifiIndicator = true;
        var service = harness.Create();
        var changed = 0;
        service.Changed += () => changed++;

        var result = await service.SetAsync("cef.wifiIndicator", Json("false"), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.False(harness.Stored.Cef.WifiIndicator);
        Assert.Equal([false], harness.Boots);
        // The shell's reload takes half a second; the row must not flick back meanwhile.
        Assert.False(Row(service.ReadState(), "cef.wifiIndicator").Checked);
        Assert.Equal(1, changed);
    }

    [Fact]
    public async Task AKeyThePageDoesNotOfferIsRefused()
    {
        Harness harness = new();
        var service = harness.Create();

        var unknown = await service.SetAsync("cef.debugPort", Json("true"), CancellationToken.None);
        var wrongShape = await service.SetAsync("cef.wifiIndicator", Json("\"yes\""), CancellationToken.None);

        Assert.False(unknown.Succeeded);
        Assert.False(wrongShape.Succeeded);
        Assert.Empty(harness.Boots);
    }

    [Fact]
    public async Task TheStartSettingsRewriteBootJsonInTheSameTransaction()
    {
        Harness harness = new();
        var service = harness.Create();

        await service.SetAsync("startup.mode", Json("\"Desktop\""), CancellationToken.None);
        await service.SetAsync("startup.atSignIn", Json("true"), CancellationToken.None);
        var invalid = await service.SetAsync("startup.mode", Json("\"7\""), CancellationToken.None);

        Assert.Equal(SessionStartMode.Desktop, harness.Stored.StartMode);
        Assert.True(harness.Stored.StartAtSignIn);
        Assert.Equal([true, true], harness.Boots);
        Assert.False(invalid.Succeeded);
    }

    [Fact]
    public async Task SteamInputManagementReconcilesTheShimFromWhatWasSaved()
    {
        Harness harness = new();
        harness.Stored.SteamInputManagementEnabled = true;
        var service = harness.Create();

        await service.SetAsync("steamInput.management", Json("false"), CancellationToken.None);
        for (var attempt = 0; attempt < 100 && harness.SteamInputApplied.Count == 0; attempt++)
        {
            await Task.Delay(10);
        }

        Assert.False(Assert.Single(harness.SteamInputApplied).SteamInputManagementEnabled);
    }

    [Fact]
    public void OnlyTurningCefOffIsAskedAboutAsLosingThisPage()
    {
        // Native Quick Access off keeps this page: WSGM's pages follow CEF itself.
        var state = new Harness().Create().ReadState();

        var cef = Row(state, "cef.enabled").Confirm!;
        var bridge = Row(state, "cef.nativeQuickAccess").Confirm!;

        Assert.False(cef.When);
        Assert.True(cef.Destructive);
        Assert.Contains("This page", cef.Description, StringComparison.Ordinal);
        Assert.False(bridge.When);
        Assert.False(bridge.Destructive);
        Assert.Contains("This page stays", bridge.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void APluginSecretIsNeverPublished()
    {
        Harness harness = new();
        harness.Installed = [new InstalledCommonPlugin("vendor.plugin", "Vendor")];
        harness.Stored.PluginInstances =
            [new CommonPluginInstanceConfig { PluginId = "vendor.plugin", Enabled = true }];
        harness.Running =
        [
            new CommonPluginSettingsView("owner", new PluginInstanceIdentity("vendor.plugin", "default"), "Vendor", 3,
            [
                new CommonPluginSettingValue(
                    new PluginSetting("token", "Token", PluginSettingKind.Secret, new PluginValue()),
                    new PluginValue(Text: "hunter2"))
            ])
        ];

        var state = harness.Create().ReadState();
        var wire = JsonSerializer.Serialize(state, WsgmSteamSettingsJsonContext.Default.WsgmSteamSettingsState);

        Assert.DoesNotContain("hunter2", wire, StringComparison.Ordinal);
        Assert.Equal("Set", Row(state, "plugins.setting:owner/token").Text);
        Assert.Equal(SteamSettingsRowKind.Secret, Row(state, "plugins.setting:owner/token").Kind);
    }

    [Fact]
    public async Task APluginSettingGoesThroughThePluginsOwnPathAtTheRevisionInForce()
    {
        Harness harness = new();
        harness.Installed = [new InstalledCommonPlugin("vendor.plugin", "Vendor")];
        harness.Running =
        [
            new CommonPluginSettingsView("owner", new PluginInstanceIdentity("vendor.plugin", "default"), "Vendor", 7,
            [
                new CommonPluginSettingValue(
                    new PluginSetting("order", "Order", PluginSettingKind.OrderedChoices, new PluginValue(Text: "a,b"),
                        Choices: ["a", "b"]),
                    new PluginValue(Text: "a,b"))
            ])
        ];
        var service = harness.Create();

        var result = await service.SetAsync("plugins.setting:owner/order", Json("[\"b\",\"a\"]"),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        // An order is stored comma-joined, as the Quick Access tab sends it.
        Assert.Equal(("owner", "order", "\"b,a\"", 7L), Assert.Single(harness.PluginWrites));
        Assert.Empty(harness.Boots);
    }

    [Fact]
    public async Task APluginCanBeTurnedOnOnlyWhileItIsInstalled()
    {
        Harness harness = new();
        harness.Installed = [new InstalledCommonPlugin("vendor.plugin", "Vendor")];
        var service = harness.Create();

        Assert.False(Row(service.ReadState(), "plugins.enabled:vendor.plugin/default").Checked);
        var enabled = await service.SetAsync("plugins.enabled:vendor.plugin/default", Json("true"),
            CancellationToken.None);
        var missing = await service.SetAsync("plugins.enabled:other.plugin/default", Json("true"),
            CancellationToken.None);

        Assert.True(enabled.Succeeded);
        Assert.True(Assert.Single(harness.Stored.PluginInstances).Enabled);
        Assert.False(missing.Succeeded);
    }

    [Fact]
    public async Task ADeviceSettingIsValidatedAgainstItsDeclarationAndStoredInItsScope()
    {
        Harness harness = new();
        harness.Stored.DeviceIntegration.PluginSettings =
        [
            new PluginSettingsScope
            {
                DeviceDefinitionId = "device",
                PluginId = "plugin",
                Declaration = new PluginSettingsManifest
                {
                    Sections = [new PluginSettingSection { SectionId = "power", Key = SettingSectionKey.Power }],
                    Settings =
                    [
                        new PluginSettingDescriptor
                        {
                            SettingId = "limit",
                            ValueKind = CapabilityValueKind.Integer,
                            Display = new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = "Limit" },
                            Default = new CapabilityValue { Kind = CapabilityValueKind.Integer, IntegerValue = 10 },
                            Minimum = 5,
                            Maximum = 20,
                            Step = 1,
                            SectionId = "power"
                        }
                    ]
                }
            }
        ];
        var service = harness.Create();

        var row = Row(service.ReadState(), "device.setting:limit");
        var outOfRange = await service.SetAsync("device.setting:limit", Json("99"), CancellationToken.None);
        var saved = await service.SetAsync("device.setting:limit", Json("12"), CancellationToken.None);

        Assert.Equal(SteamSettingsRowKind.Range, row.Kind);
        Assert.Equal((5d, 20d), (row.Minimum, row.Maximum));
        Assert.False(outOfRange.Succeeded);
        Assert.True(saved.Succeeded);
        Assert.Equal(12, Assert.Single(harness.Stored.DeviceIntegration.PluginSettings[0].Values).Integer);
    }

    [Fact]
    public async Task AReloadHandsTheRowsBackToTheShellsConfiguration()
    {
        Harness harness = new();
        var service = harness.Create();
        await service.SetAsync("cef.wifiIndicator", Json("false"), CancellationToken.None);
        var before = service.ReadState().Revision;

        harness.Stored = ConfigStore.Normalize(new AppConfig());
        harness.Stored.Cef.WifiIndicator = true;
        service.ConfigurationChanged();

        var after = service.ReadState();
        Assert.True(after.Revision > before);
        Assert.True(Row(after, "cef.wifiIndicator").Checked);
    }

    [Fact]
    public void TheMenuRowIsWsgmBeforePowerOpeningThisPage()
    {
        var item = Assert.Single(WsgmSteamSettingsService.ReadMenu(true).Items);

        Assert.Equal("WSGM", item.Label);
        Assert.Equal("power", item.Before);
        Assert.Equal(SteamWsgmSettingsSurface.Route, item.Route);
        Assert.False(string.IsNullOrEmpty(item.Glyph));
    }

    [Fact]
    public void ThereIsNoMenuRowWhileItsPageCannotBeDrawn()
    {
        // A row that opens onto nothing is worse than no row.
        Assert.Empty(WsgmSteamSettingsService.ReadMenu(false).Items);
    }

    [Theory]
    [InlineData(SteamUiPatchState.Verified, SteamUiPatchState.Verified, true)]
    [InlineData(SteamUiPatchState.Verified, SteamUiPatchState.Incompatible, false)]
    [InlineData(SteamUiPatchState.Applied, SteamUiPatchState.Verified, false)]
    [InlineData(SteamUiPatchState.Verified, SteamUiPatchState.AbsentTarget, false)]
    public void ThePageIsDrawableOnlyWhenItsRouteAndItsRendererAreBothVerified(
        SteamUiPatchState pages, SteamUiPatchState settings, bool ready)
    {
        SteamUiPatchSnapshot[] snapshots =
        [
            new(SteamPageSurface.PatchId, 1, true, pages, null, default, null, DateTimeOffset.UnixEpoch),
            new(SteamWsgmSettingsSurface.PatchId, 1, true, settings, null, default, null, DateTimeOffset.UnixEpoch)
        ];

        Assert.Equal(ready, SteamUiSessionHost.WsgmSettingsPageReady(snapshots));
        Assert.False(SteamUiSessionHost.WsgmSettingsPageReady([snapshots[1]]));
    }

    [Theory]
    [InlineData("plugins.enabled:vendor.plugin/")]
    [InlineData("plugins.enabled:vendor.plugin/Bad Instance")]
    [InlineData("plugins.enabled:vendor.plugin/a/b")]
    public async Task AMalformedPluginInstanceIsRefusedBeforeItIsStored(string key)
    {
        // Reconcile refuses the whole list over one bad identity, which would stop every plugin.
        Harness harness = new();
        harness.Installed = [new InstalledCommonPlugin("vendor.plugin", "Vendor")];

        var result = await harness.Create().SetAsync(key, Json("true"), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Empty(harness.Stored.PluginInstances);
    }

    [Fact]
    public void ABoundedPluginNumberIsTextSoEveryAllowedValueCanBeEntered()
    {
        // The contract allows any finite number between the bounds; a slider reaches only its steps.
        Harness harness = new();
        harness.Installed = [new InstalledCommonPlugin("vendor.plugin", "Vendor")];
        harness.Running =
        [
            new CommonPluginSettingsView("owner", new PluginInstanceIdentity("vendor.plugin", "default"), "Vendor", 1,
            [
                new CommonPluginSettingValue(
                    new PluginSetting("ratio", "Ratio", PluginSettingKind.Number, new PluginValue(Number: 0.25), 0, 1),
                    new PluginValue(Number: 0.25))
            ])
        ];

        var row = Row(harness.Create().ReadState(), "plugins.setting:owner/ratio");

        Assert.Equal(SteamSettingsRowKind.Text, row.Kind);
        Assert.Equal("0.25", row.Text);
        Assert.Equal("From 0 to 1.", row.Description);
    }

    [Theory]
    [InlineData("""{"key":"cef.enabled","value":true}""", true)]
    [InlineData("""{"key":"order","value":["a","b"]}""", true)]
    [InlineData("""{"key":"cef.enabled"}""", false)]
    [InlineData("""{"key":"cef.enabled","value":null}""", false)]
    [InlineData("""{"key":"cef.enabled","value":{"nested":1}}""", false)]
    [InlineData("""{"key":"cef.enabled","value":true,"extra":1}""", false)]
    [InlineData("""{"key":"","value":true}""", false)]
    public void TheSetPayloadIsExactlyAKeyAndAValue(string json, bool accepted)
    {
        Assert.Equal(accepted, SteamWsgmSettingsSurface.TryReadSet(Json(json), out _));
    }

    private sealed class Harness
    {
        internal readonly List<bool> Boots = [];
        internal readonly List<(string Id, string Key, string Value, long Revision)> PluginWrites = [];
        internal readonly List<AppConfig> SteamInputApplied = [];
        internal List<InstalledCommonPlugin> Installed = [];
        internal List<CommonPluginSettingsView> Running = [];
        internal AppConfig Stored = ConfigStore.Normalize(new AppConfig());

        internal WsgmSteamSettingsService Create()
        {
            return new WsgmSteamSettingsService(
                () => Stored,
                (change, boot) =>
                {
                    // A fresh copy, as the store's strict load hands one to the change.
                    var fresh = ConfigStore.CloneJson(Stored, ConfigJsonContext.Default.AppConfig);
                    change(fresh);
                    Boots.Add(boot);
                    Stored = fresh;
                    return fresh;
                },
                config => SteamInputApplied.Add(config),
                () => new SteamInputShimStatus(SteamInputShimState.Disabled, SteamInputShimVector.None, null),
                () => Installed,
                () => Running,
                (id, key, value, revision, _) =>
                {
                    PluginWrites.Add((id, key, value.GetRawText(), revision));
                    return Task.FromResult(SteamUiCommandResult.Applied);
                });
        }
    }
}
