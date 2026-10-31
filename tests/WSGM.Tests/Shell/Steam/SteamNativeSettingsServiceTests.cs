using System.Text.Json;
using SteamUiToolkit;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Shell;
using WSGM.Tests.Builders;
using WSGM.Testing;

namespace WSGM.Tests.Shell;

public sealed partial class SteamNativeSettingsServiceTests
{
    [Theory]
    [InlineData("#FF0000", 0xFF0000)]
    [InlineData("hsla(120, 100%, 50%, 1)", 0x00FF00)]
    [InlineData("hsla(240, 100%, 50%, 1)", 0x0000FF)]
    [InlineData("hsla(0, 0%, 100%, 1)", 0xFFFFFF)]
    public void SavedOpaqueColorPreservesRgb(string css, int expected)
    {
        Assert.True(SteamCapabilityRows.TryColor(JsonSerializer.SerializeToElement(css), out var color));
        Assert.Equal(expected, color);
    }

    [Theory]
    [InlineData("hsla(0, 100%, 50%, 0.5)")]
    [InlineData("hsla(NaN, 100%, 50%, 1)")]
    [InlineData("hsla(0, 101%, 50%, 1)")]
    [InlineData("hsla(0, 100%, -1%, 1)")]
    [InlineData("#FFFFFFFF")]
    [InlineData("#GGGGGG")]
    public void MalformedOrTransparentColorCannotReachDevice(string css)
    {
        Assert.False(SteamCapabilityRows.TryColor(JsonSerializer.SerializeToElement(css), out _));
    }

    [Fact]
    public void RebuildingIdenticalPagesKeepsTheRevisionSoNothingIsResent()
    {
        static SteamNativeSettingsPage[] Pages(int limit) =>
        [
            new SteamNativeSettingsPage("power",
            [
                new SteamSettingsSection("Limits",
                    [new SteamSettingsRow("limit", SteamSettingsRowKind.Range, "Limit", Number: limit, Minimum: 5,
                        Maximum: 35, Step: 1, Choices: [new SteamSettingsChoice("a", "A")])], "power.limits")
            ])
        ];

        string? published = null;
        var revision = SteamNativeSettingsService.NextRevision(Pages(20), ref published, 0);
        Assert.Equal(1, revision);
        // A fresh build with equal content, including fresh arrays, is not a new revision.
        Assert.Equal(1, SteamNativeSettingsService.NextRevision(Pages(20), ref published, revision));
        Assert.Equal(2, SteamNativeSettingsService.NextRevision(Pages(25), ref published, revision));
    }

    [Fact]
    public void RangeReplacementRequiresRebuildingTheMountedEditor()
    {
        var row = new DeviceOverlayCapability("limit", null, DeviceOverlaySection.PowerAndThermals,
            DescriptorStatus.Available, "Limit", "", "", true, CapabilityValue.Integer(10))
        {
            Role = CapabilityRole.GenericRange, Writable = true, ValueKind = CapabilityValueKind.Integer,
            Minimum = 0, Maximum = 40, Step = 1
        };
        Assert.True(row.SameLayoutAs(row with { CurrentValue = CapabilityValue.Integer(15) }));
        Assert.False(row.SameLayoutAs(row with { Maximum = 20 }));
        Assert.False(row.SameLayoutAs(row with { Step = 5 }));
        Assert.False(row.SameLayoutAs(row with { Role = CapabilityRole.PowerSustainedLimit }));
    }

    [Fact]
    public void LightingEditorStagesOpaqueColorWithStableLogicalIdentity()
    {
        var capability = new DeviceOverlayCapability("lighting.color", "left", DeviceOverlaySection.LightingAndFeatures,
            DescriptorStatus.Available, "Left ring", string.Empty, string.Empty, true,
            new CapabilityValue { Kind = CapabilityValueKind.Color, ColorValue = 0x123456 })
        {
            Writable = true,
            ValueKind = CapabilityValueKind.Color
        };
        var row = SteamNativeSettingsService.DeviceRow(capability);
        Assert.Equal(SteamSettingsRowKind.Color, row.Kind);
        Assert.Equal("#123456", row.Text);
        Assert.False(row.ColorAlpha);
        Assert.Contains("Save", row.Description);
        Assert.Equal(row.Key, SteamNativeSettingsService.DeviceRow(capability).Key);
        Assert.NotEqual(row.Key, SteamNativeSettingsService.DeviceRow(capability with { InstanceId = "right" }).Key);
    }

    [Fact]
    public void StepAndSupportedChoicesAreValidatedBeforeTheOwnerIsCalled()
    {
        var range = new SteamSettingsRow("limit", SteamSettingsRowKind.Range, "Limit",
            Minimum: 5, Maximum: 35, Step: 5);
        Assert.True(SteamNativeSettingsService.ValidValue(range, JsonSerializer.SerializeToElement(20)));
        Assert.False(SteamNativeSettingsService.ValidValue(range, JsonSerializer.SerializeToElement(21)));
        Assert.False(SteamNativeSettingsService.ValidValue(range, JsonSerializer.SerializeToElement(40)));
        Assert.False(SteamNativeSettingsService.ValidValue(range with { Disabled = true },
            JsonSerializer.SerializeToElement(20)));

        var choice = new SteamSettingsRow("format", SteamSettingsRowKind.Choice, "Format",
            Choices: [new SteamSettingsChoice("stereo", "Stereo")]);
        Assert.True(SteamNativeSettingsService.ValidValue(choice, JsonSerializer.SerializeToElement("stereo")));
        Assert.False(SteamNativeSettingsService.ValidValue(choice, JsonSerializer.SerializeToElement("surround")));
    }

    [Fact]
    public void RepublishedProfileRowsKeepOldScopeCommandsOutOfTheOfferedSet()
    {
        var active = new ActiveProfile("steam:1", "game.exe", 1, "game-one", true);
        var original = SteamNativeSettingsService.ProfileKey("power.sustained", active, true);
        Assert.Equal(original, SteamNativeSettingsService.ProfileKey("power.sustained", active, true));
        Assert.NotEqual(original, SteamNativeSettingsService.ProfileKey("power.sustained", active, false));
        Assert.NotEqual(original, SteamNativeSettingsService.ProfileKey("power.sustained",
            active with { ApplicationId = "steam:2" }, true));
        Assert.NotEqual(original, SteamNativeSettingsService.ProfileKey("power.sustained",
            active with { GameProfileId = "game-two" }, true));

        var offered = new Dictionary<string, bool>
        {
            [SteamNativeSettingsService.ProfileKey("power.sustained", active, false)] = true
        };
        Assert.False(offered.ContainsKey(original));
    }

    [Fact]
    public void AnUnselectableChoiceIsListedOnlyWhileCurrentAndNeverAccepted()
    {
        ChoiceRowState state = new(true,
            [new ChoiceRowOption("a", "A"), new ChoiceRowOption("b", "B"), new ChoiceRowOption("custom", "Custom", false)],
            "custom", "Status", true);

        var row = SteamNativeSettingsService.ChoiceRow("key", "Label", "power", state)!;

        Assert.Equal(["a", "b", "custom"], row.Choices!.Select(choice => choice.Value));
        Assert.False(row.Choices![2].Selectable);
        Assert.Equal("custom", row.Text);
        Assert.Equal("Custom", row.Summary);
        Assert.Equal("power", row.Icon);
        Assert.Equal(NativeQamLayout.AccentDescription("Status"), row.Description);
        Assert.False(SteamNativeSettingsService.ValidValue(row, JsonSerializer.SerializeToElement("custom")));
        Assert.True(SteamNativeSettingsService.ValidValue(row, JsonSerializer.SerializeToElement("b")));

        var other = SteamNativeSettingsService.ChoiceRow("key", "Label", "power", state with { Current = "a" })!;
        Assert.Equal(["a", "b"], other.Choices!.Select(choice => choice.Value));
        Assert.Null(SteamNativeSettingsService.ChoiceRow("key", "Label", "power", state with { Options = [] }));
        Assert.True(SteamNativeSettingsService.ChoiceRow("key", "Label", "power",
            state with { Options = [new ChoiceRowOption("a", "A")], Current = "a" })!.Disabled);
        Assert.True(SteamNativeSettingsService.ChoiceRow("key", "Label", "power",
            state with { Available = false })!.Disabled);
    }

    [Fact]
    public async Task QuickAccessAndSettingsShareOneRowAndOneOfferedCommand()
    {
        await using var rig = await ProducerRig.CreateAsync();

        var quickAccess = await rig.Producer.ReadQuickAccessAsync();
        var settings = await rig.Producer.ReadAsync();

        var cores = Assert.Single(Assert.Single(quickAccess!.Sections, section => section.Id == "hybridCores").Rows);
        Assert.Equal("Processor cores", cores.Label);
        Assert.Equal("cores", cores.Icon);
        Assert.Equal(cores.Text is { Length: > 0 }
            ? cores.Choices!.Single(choice => choice.Value == cores.Text).Label
            : null, cores.Summary);
        var page = Assert.Single(settings!.Pages, candidate => candidate.Id == "power");
        // One row, one key: both surfaces describe it identically and offer one command.
        Assert.Contains(page.Sections.SelectMany(section => section.Rows),
            row => row.Key == cores.Key && row.Label == cores.Label && row.Text == cores.Text);
        var other = cores.Choices!.First(choice => choice.Value != cores.Text);
        Assert.True((await rig.Producer.SetAsync(cores.Key, JsonSerializer.SerializeToElement(other.Value),
            CancellationToken.None)).Succeeded);
        Assert.False((await rig.Producer.SetAsync(cores.Key, JsonSerializer.SerializeToElement("nope"),
            CancellationToken.None)).Succeeded);
        Assert.False((await rig.Producer.SetAsync("no.such.row", JsonSerializer.SerializeToElement(true),
            CancellationToken.None)).Succeeded);
    }

    [Fact]
    public async Task AnUnchangedBuildKeepsTheQuickAccessRevision()
    {
        await using var rig = await ProducerRig.CreateAsync();

        var first = await rig.Producer.ReadQuickAccessAsync();
        var second = await rig.Producer.ReadQuickAccessAsync();

        Assert.Equal(first!.Revision, second!.Revision);
        Assert.True(first.Revision > 0);
    }

    [Fact]
    public void AutoTdpPublishesItsAvailabilityProgressAndHeldWatts()
    {
        var holding = SteamNativeSettingsService.ProjectAutoTdp(true,
            new AutoTdpStatus(AutoTdpState.Controlling, 17, 14.2, 16.6, "steam:70", "sustained-miss"), true);
        Assert.True(holding is { Available: true, Controlling: true, Watts: 17, Progress: "completed" });
        Assert.False(SteamNativeSettingsService.ProjectAutoTdp(true, null, false).Available);
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>A producer over inert owners: no device, display, audio or graphics, a fake core and scheme API.</summary>
    private sealed class ProducerRig : IAsyncDisposable
    {
        private readonly PerformanceService _performance;

        private ProducerRig(PerformanceService performance, SteamNativeSettingsService producer)
        {
            _performance = performance;
            Producer = producer;
        }

        internal SteamNativeSettingsService Producer { get; }

        public ValueTask DisposeAsync()
        {
            return _performance.DisposeAsync();
        }

        internal static Task<ProducerRig> CreateAsync()
        {
            FakeHybridCoreApi api = new();
            var profiles = PerformanceBuilders.Profiles();
            var performance = PerformanceBuilders.Service(profiles);
            SteamNativeSettingsService producer = new(
                new NativeQamBrightnessService(() => false, () => null, _ => false, Timeout.InfiniteTimeSpan),
                null,
                new PerformanceServiceNativeQamAdapter(performance, profiles),
                new NativeQamPowerProfileService(new PowerSchemes(api)),
                null,
                new NativeQamHybridCoreService(api.Owner()),
                null,
                null,
                null,
                profiles);
            return Task.FromResult(new ProducerRig(performance, producer));
        }
    }
}
