using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Input;
using WSGM.Device.Sdk.Testing;
using WSGM.DeviceLab.Testing;

namespace WSGM.DeviceLab.Tests.Testing;

public sealed class SyntheticPluginFixtureTests
{
    [Fact]
    public async Task PublicationSummary_UsesTheSdkOwnedTestHostAdapterForEveryChannel()
    {
        TestPluginHostAdapter host = new(3);
        await host.PublishDescriptorsAsync(
            new CapabilityDescriptorSet { Generation = 1, CycleGeneration = 3 },
            CancellationToken.None);
        await host.PublishCapabilityStateAsync(
            new CapabilityState
            {
                CapabilityId = "dock.beacon",
                Available = true,
                Quality = HardwareStateQuality.Verified,
                DescriptorGeneration = 1,
                CycleGeneration = 3
            },
            CancellationToken.None);
        await host.PublishPhysicalDevicesAsync([], null, CancellationToken.None);
        await host.PublishControllerSampleAsync(
            CanonicalControllerSample.Neutral(1, 3, DateTimeOffset.UnixEpoch),
            CancellationToken.None);
        await host.PublishOemControlsAsync([], CancellationToken.None);
        await host.PublishOemEventAsync(
            new OemControlEvent(
                "dock-button",
                OemPressKind.Short,
                3,
                DateTimeOffset.UnixEpoch,
                "synthetic-press"),
            CancellationToken.None);

        var summary = PluginPublicationSummary.From(host);

        Assert.Equal(1, summary.DescriptorSets);
        Assert.Equal(1, summary.CapabilityStates);
        Assert.Equal(1, summary.PhysicalDeviceSets);
        Assert.Equal(1, summary.ControllerSamples);
        Assert.Equal(1, summary.OemControlSets);
        Assert.Equal(1, summary.OemEvents);
    }

    [Fact]
    public async Task SyntheticDockFixture_ExercisesTheMateriallyDifferentPluginLifecycle()
    {
        var report = await SyntheticPluginFixture.RunAsync(
            CancellationToken.None);
        string[] expected =
        [
            "different-device-rejected",
            "synthetic-dock-exact-match",
            "partial-capability-availability",
            "canonical-input-published",
            "boolean-command-readback",
            "canonical-output-applied",
            "cancellation-observed",
            "stale-generation-rejected",
            "stop-restores-original-state-and-output",
            "cleanup-diagnostics-reported"
        ];

        Assert.True(report.Passed);
        Assert.Equal(expected, report.Checks);
    }

    [Fact]
    public void SettingsManifest_IsValid()
    {
        Assert.True(
            SyntheticDockPlugin.SettingsManifest.TryValidate(out var error),
            error);
    }

    [Fact]
    public void SettingsManifest_CoversEveryValueKindTheSdkAllows()
    {
        var kinds = SyntheticDockPlugin.SettingsManifest.Settings
            .Select(setting => setting.ValueKind)
            .ToHashSet();

        // Curve is excluded by design: a curve is authored as a named profile, not toggled as a
        // setting, and the SDK refuses one here.
        Assert.Equal(
            [
                CapabilityValueKind.Boolean,
                CapabilityValueKind.Integer,
                CapabilityValueKind.Choice,
                CapabilityValueKind.Color,
                CapabilityValueKind.Text
            ],
            kinds.OrderBy(kind => (int)kind));
    }

    [Fact]
    public void SettingsManifest_KeepsOneSettingInAnUndeclaredSection()
    {
        var orphan = Assert.Single(
            SyntheticDockPlugin.SettingsManifest.Settings,
            setting => setting.SettingId == SyntheticDockPlugin.OrphanSettingId);

        Assert.DoesNotContain(
            SyntheticDockPlugin.SettingsManifest.Sections,
            section => section.SectionId == orphan.SectionId);
    }

    [Fact]
    public void SettingsManifest_EveryDefaultSatisfiesItsOwnDeclaration()
    {
        foreach (var setting in SyntheticDockPlugin.SettingsManifest.Settings)
        {
            Assert.True(
                setting.TryValidateValue(setting.Default, out var error),
                $"{setting.SettingId}: {error}");
        }
    }
}
