using System.Text.Json;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Settings;
using WSGM.Plugin.Sdk;
using WSGM.Shell;
using Xunit.Sdk;
using static WSGM.Tests.Builders.CapabilityBuilders;

namespace WSGM.Tests.Shell;

/// <summary>
///     The Graphics page in Steam: each graphics row as one of Steam's own settings fields, a Use global row
///     for a game override, and every change routed back to the graphics coordinator through the source.
/// </summary>
public sealed class SteamGraphicsServiceTests
{
    private const string Plugin = "wsgm.test-gpu";

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static SteamSettingsRow Row(IReadOnlyList<SteamSettingsPage> pages, string key)
    {
        return pages.SelectMany(page => page.Sections).SelectMany(section => section.Rows)
                   .FirstOrDefault(row => row.Key == key)
               ?? throw new XunitException($"No row {key}.");
    }

    private static CapabilityDescriptor Range(string id, CapabilityProfileScope scope,
        CapabilityApplyTiming timing = CapabilityApplyTiming.Immediate)
    {
        return Toggle(scope, id) with
        {
            Role = CapabilityRole.GenericRange,
            ValueKind = CapabilityValueKind.Integer,
            Minimum = 0,
            Maximum = 100,
            Step = 5,
            Unit = CapabilityUnit.Percent,
            ApplyTiming = timing,
            SectionId = "graphics",
            CategoryId = "quality"
        };
    }

    private static CapabilityDescriptor Choice(string id)
    {
        return Toggle(CapabilityProfileScope.Switched, id) with
        {
            Role = CapabilityRole.GenericChoice,
            ValueKind = CapabilityValueKind.Choice,
            SectionId = "graphics",
            Choices =
            [
                new CapabilityChoice("off", new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = "Off" }),
                new CapabilityChoice("on", new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = "On" })
            ]
        };
    }

    private static GraphicsOverlaySnapshot Snapshot(params GpuCapabilityView[] capabilities)
    {
        return GraphicsOverlayBridge.Project(
        [
            new GpuPublisherSnapshot(
                new GpuPublisherView(GpuInstance, "Test graphics", GpuPublisher, PluginHealth.Ready, null),
                [
                    new CapabilitySection
                    {
                        SectionId = "graphics", Key = SettingSectionKey.Custom, CustomTitle = "Graphics",
                        Icon = SectionIcon.Wrench,
                        Categories =
                        [
                            new CapabilityCategory
                            {
                                CategoryId = "quality", Key = SettingSectionKey.Custom, CustomTitle = "Image quality"
                            }
                        ]
                    },
                    new CapabilitySection
                    {
                        SectionId = "display-1", Key = SettingSectionKey.Custom, CustomTitle = "Built-in display",
                        Icon = SectionIcon.Display, SortOrder = 1
                    }
                ],
                capabilities)
        ]);
    }

    private static GpuCapabilityView Placed(CapabilityDescriptor descriptor, CapabilityValue? observed = null,
        string? overrideId = null)
    {
        var view = View(descriptor with { SectionId = descriptor.SectionId ?? "graphics" }, null, null);
        return new GpuCapabilityView(
            view with
            {
                Projection = view.Projection with
                {
                    State = view.Projection.State with { ObservedValue = observed ?? view.Projection.State.ObservedValue }
                }
            },
            overrideId);
    }

    [Fact]
    public void EachSectionIsASidebarPageWithACategorySection()
    {
        var pages = SteamGraphicsService.Pages(Snapshot(
            Placed(Range("graphics.sharpen", CapabilityProfileScope.Switched), CapabilityValue.Integer(40)),
            Placed(Toggle(CapabilityProfileScope.Switched, "display.vrr") with { SectionId = "display-1" })));

        Assert.Equal(["wsgm-test-gpu-graphics", "wsgm-test-gpu-display-1"], pages.Select(page => page.Id));
        Assert.Equal("Image quality", Assert.Single(pages[0].Sections).Title);
        Assert.NotEqual(pages[0].Glyph, pages[1].Glyph);
    }

    [Fact]
    public void ValueKindsBecomeSteamsOwnFields()
    {
        var pages = SteamGraphicsService.Pages(Snapshot(
            Placed(Toggle(CapabilityProfileScope.Switched, "graphics.toggle"), Flag(true)),
            Placed(Range("graphics.sharpen", CapabilityProfileScope.Switched), CapabilityValue.Integer(40)),
            Placed(Choice("graphics.choice"), CapabilityValue.Choice("on")),
            Placed(Toggle(CapabilityProfileScope.Switched, "graphics.reading") with
            {
                SupportsWrite = false,
                Role = CapabilityRole.GenericReadOnly
            }, Flag(true))));

        var toggle = Row(pages, "wsgm.test-gpu/graphics.toggle");
        Assert.Equal(SteamSettingsRowKind.Boolean, toggle.Kind);
        Assert.True(toggle.Checked);
        var range = Row(pages, "wsgm.test-gpu/graphics.sharpen");
        Assert.Equal(SteamSettingsRowKind.Range, range.Kind);
        Assert.Equal((40d, 0d, 100d, 5d, "%"), (range.Number, range.Minimum, range.Maximum, range.Step, range.Suffix));
        var choice = Row(pages, "wsgm.test-gpu/graphics.choice");
        Assert.Equal(SteamSettingsRowKind.Choice, choice.Kind);
        Assert.Equal("on", choice.Text);
        Assert.Equal(["Off", "On"], choice.Choices!.Select(option => option.Label));
        var reading = Row(pages, "wsgm.test-gpu/graphics.reading");
        Assert.Equal(SteamSettingsRowKind.Note, reading.Kind);
        Assert.Equal("ON", reading.Text);
    }

    [Fact]
    public void AnInstanceIsPartOfTheRowKey()
    {
        var pages = SteamGraphicsService.Pages(Snapshot(
            Placed(Toggle(CapabilityProfileScope.Switched, "display.vrr") with
            {
                InstanceId = "internal-edid-1", SectionId = "display-1"
            })));

        Assert.Equal(SteamSettingsRowKind.Boolean, Row(pages, "wsgm.test-gpu/display.vrr#internal-edid-1").Kind);
    }

    [Fact]
    public void AGameOverrideIsMarkedOnTheRowWithoutAUseGlobalControl()
    {
        var pages = SteamGraphicsService.Pages(Snapshot(
            Placed(Toggle(CapabilityProfileScope.Switched, "graphics.toggle"), overrideId: "gpu:x:graphics.toggle"),
            Placed(Toggle(CapabilityProfileScope.Switched, "graphics.plain"))));

        Assert.True(Row(pages, "wsgm.test-gpu/graphics.toggle").Override);
        Assert.False(Row(pages, "wsgm.test-gpu/graphics.plain").Override);
        Assert.DoesNotContain(pages.SelectMany(page => page.Sections).SelectMany(section => section.Rows),
            row => row.Kind == SteamSettingsRowKind.Action);
    }

    [Fact]
    public void AGlobalOnlyRowSaysItAppliesAfterRestart()
    {
        var pages = SteamGraphicsService.Pages(Snapshot(
            Placed(Range("graphics.memory", CapabilityProfileScope.GlobalOnly, CapabilityApplyTiming.SystemRestart),
                CapabilityValue.Integer(50), "gpu:x:graphics.memory")));

        Assert.Equal("Applies after restart", Row(pages, "wsgm.test-gpu/graphics.memory").Description);
    }

    [Fact]
    public void AnUnavailableRowIsDisabledAndSaysWhy()
    {
        var placed = Placed(Choice("display.profile") with { SectionId = "display-1" });
        placed = placed with
        {
            View = placed.View with
            {
                Projection = placed.View.Projection with
                {
                    State = placed.View.Projection.State with
                    {
                        Available = false,
                        Reason = new CapabilityReason(CapabilityReasonCode.PrerequisiteMissing, "Turn on VRR first.")
                    }
                }
            }
        };

        var row = Row(SteamGraphicsService.Pages(Snapshot(placed)), "wsgm.test-gpu/display.profile");

        Assert.True(row.Disabled);
        Assert.Equal("Turn on VRR first.", row.Description);
    }

    [Fact]
    public void ANotReadyPublisherPutsItsStatusFirst()
    {
        var snapshot = Snapshot(Placed(Toggle(CapabilityProfileScope.Switched, "graphics.toggle")));
        snapshot = snapshot with
        {
            Publishers = [snapshot.Publishers[0] with { Note = "Test graphics stopped working." }]
        };

        var first = SteamGraphicsService.Pages(snapshot)[0].Sections[0].Rows[0];

        Assert.Equal(SteamSettingsRowKind.Note, first.Kind);
        Assert.Equal("Test graphics stopped working.", first.Text);
    }

    [Theory]
    [InlineData("45", true, 45)]
    [InlineData("44.6", true, 45)]
    [InlineData("42", false, 0)]
    [InlineData("105", false, 0)]
    [InlineData("\"45\"", false, 0)]
    public void ARangeTakesOnlyValuesOnItsSteps(string json, bool accepted, int expected)
    {
        var row = Snapshot(Placed(Range("graphics.sharpen", CapabilityProfileScope.Switched))).Sections[0]
            .Capabilities[0];

        Assert.Equal(accepted, SteamGraphicsService.TryValue(row, Json(json), out var value));
        if (accepted)
        {
            Assert.Equal(expected, value?.IntegerValue);
        }
    }

    [Fact]
    public void AChoiceTakesOnlyItsOwnValues()
    {
        var row = Snapshot(Placed(Choice("graphics.choice"))).Sections[0].Capabilities[0];

        Assert.True(SteamGraphicsService.TryValue(row, Json("\"off\""), out var value));
        Assert.Equal("off", value?.ChoiceValue);
        Assert.False(SteamGraphicsService.TryValue(row, Json("\"boost\""), out _));
        Assert.False(SteamGraphicsService.TryValue(row, Json("true"), out _));
    }

    [Fact]
    public async Task ASetWritesThroughTheSourceAndBumpsTheRevision()
    {
        FakeSource source = new(Snapshot(Placed(Toggle(CapabilityProfileScope.Switched, "graphics.toggle"))));
        using SteamGraphicsService service = new(source);
        var before = service.ReadState().Revision;

        var result = await service.SetAsync("wsgm.test-gpu/graphics.toggle", Json("true"), CancellationToken.None);

        Assert.True(result.Succeeded);
        var (capability, value) = Assert.Single(source.Writes);
        Assert.Equal("graphics.toggle", capability.CapabilityId);
        Assert.True(value?.BooleanValue);
        Assert.True(service.ReadState().Revision > before);
    }

    [Fact]
    public async Task ARefusedWriteIsReportedWithThePluginsReason()
    {
        FakeSource source = new(Snapshot(Placed(Toggle(CapabilityProfileScope.Switched, "graphics.toggle"))))
        {
            Outcome = CommandOutcome.Rejected
        };
        using SteamGraphicsService service = new(source);

        var result = await service.SetAsync("wsgm.test-gpu/graphics.toggle", Json("true"), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("Refused by the driver.", result.Error);
    }

    [Fact]
    public async Task AnUnknownOrMalformedSetWritesNothing()
    {
        FakeSource source = new(Snapshot(Placed(Choice("graphics.choice"))));
        using SteamGraphicsService service = new(source);

        Assert.False((await service.SetAsync("wsgm.test-gpu/graphics.gone", Json("true"), CancellationToken.None))
            .Succeeded);
        Assert.False((await service.SetAsync("wsgm.test-gpu/graphics.choice", Json("\"boost\""),
            CancellationToken.None)).Succeeded);
        Assert.Empty(source.Writes);
    }

    [Fact]
    public void TheSourceChangingRepublishesThePage()
    {
        FakeSource source = new(GraphicsOverlaySnapshot.Empty);
        using SteamGraphicsService service = new(source);
        var changed = 0;
        service.Changed += () => changed++;

        source.Raise();

        Assert.Equal(1, changed);
        Assert.False(service.Visible);
    }

    [Fact]
    public void TheMenuOffersGraphicsOnlyWhileItsPageIsReady()
    {
        var both = WsgmSteamSettingsService.ReadMenu(true, true).Items;
        Assert.Equal([WsgmSteamSettingsService.MenuItemId, SteamGraphicsService.MenuItemId],
            both.Select(item => item.Id));
        Assert.Equal(SteamGraphicsSurface.Route, both[1].Route);
        Assert.Single(WsgmSteamSettingsService.ReadMenu(true).Items);
        Assert.Equal(SteamGraphicsService.MenuItemId,
            Assert.Single(WsgmSteamSettingsService.ReadMenu(false, true).Items).Id);
    }

    [Fact]
    public void ThePayloadReadersAcceptExactlyTheirShapes()
    {
        Assert.True(SteamGraphicsSurface.TryReadSet(Json("""{"key":"a/b","value":3}"""), out var set));
        Assert.Equal("a/b", set.Key);
        Assert.False(SteamGraphicsSurface.TryReadSet(Json("""{"key":"a/b","value":{}}"""), out _));
        Assert.False(SteamGraphicsSurface.TryReadSet(Json("""{"key":"a/b"}"""), out _));
    }

    private sealed class FakeSource(GraphicsOverlaySnapshot snapshot) : IGraphicsOverlaySource
    {
        internal List<(DeviceOverlayCapability Capability, CapabilityValue? Value)> Writes { get; } = [];
        internal List<string> Cleared { get; } = [];
        internal CommandOutcome Outcome { get; init; } = CommandOutcome.AppliedVerified;

        public event Action? Changed;

        public GraphicsOverlaySnapshot Snapshot()
        {
            return snapshot;
        }

        public Task<CapabilityCommandResult?> WriteAsync(DeviceOverlayCapability capability, CapabilityValue? value,
            CancellationToken cancellationToken = default)
        {
            Writes.Add((capability, value));
            return Task.FromResult<CapabilityCommandResult?>(new CapabilityCommandResult
            {
                CommandId = Guid.NewGuid(),
                Outcome = Outcome,
                Reason = Outcome is CommandOutcome.Rejected
                    ? new CapabilityReason(CapabilityReasonCode.Unsupported, "Refused by the driver.")
                    : null,
                CompletedAt = DateTimeOffset.UtcNow
            });
        }

        public Task<bool> UseGlobalAsync(string overrideId, CancellationToken cancellationToken = default)
        {
            Cleared.Add(overrideId);
            return Task.FromResult(true);
        }

        public void Dispose()
        {
        }

        internal void Raise()
        {
            Changed?.Invoke();
        }
    }
}
