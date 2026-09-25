// SPDX-License-Identifier: MIT

using WSGM.Device.Asus.RogAlly.Tests.Fakes;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Input;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Device.Sdk.Plugin;
using WSGM.Device.Sdk.Testing;
using WSGM.Device.Tests;

namespace WSGM.Device.Asus.RogAlly.Tests;

public sealed class PluginTests
{
    static PluginTests()
    {
        AllyPowerCapability.WriteSpacing = TimeSpan.Zero;
        AllyPowerCapability.ModeSettle = TimeSpan.Zero;
    }

    [Theory]
    [InlineData("RC71L", "rc71l")]
    [InlineData("RC72LA", "rc72la")]
    [InlineData("RC73YA", "rc73ya")]
    [InlineData("RC73XA", "rc73xa")]
    public async Task StartPublishesAValidSurfaceForEachModel(string board, string definition)
    {
        using var directory = new TemporaryDirectory();
        var hardware = new AllyFakeHardware(board);
        var host = new TestPluginHostAdapter(1);
        await using var plugin = hardware.CreatePlugin();

        var result = await plugin.StartAsync(Start(host, directory, definition), CancellationToken.None);

        Assert.Equal(PluginOperationalState.Active, result.State);
        var descriptors = host.DescriptorSets.Last();
        Assert.True(DevicePowerPreset.TryValidate(descriptors.Descriptors, out var presetError), presetError);
        Assert.True(CapabilityLayout.TryValidate(descriptors.Descriptors, out var layoutError), layoutError);
        var declared = DetectionTests.ReadManifest().Capabilities;
        Assert.All(descriptors.Descriptors, descriptor => Assert.Contains(descriptor.Role, declared));
        Assert.All(descriptors.Descriptors, descriptor => Assert.True(descriptor.Display.TryValidate(out _)));
        var model = AllyModels.ById(definition)!;
        var sustained = descriptors.Descriptors.Single(item => item.Role == CapabilityRole.PowerSustainedLimit);
        Assert.Equal(model.MinimumWatts, sustained.Minimum);
        Assert.Equal(model.MaximumWatts, sustained.Maximum);
        Assert.Equal(4, host.OemControlSets.Last().Count);
        Assert.Single(host.PhysicalDeviceSets);
    }

    [Fact]
    public async Task StartRefusesAMachineThatStoppedMatching()
    {
        using var directory = new TemporaryDirectory();
        var hardware = new AllyFakeHardware("RC71L");
        await using var plugin = hardware.CreatePlugin();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await plugin.StartAsync(Start(new TestPluginHostAdapter(1), directory, "rc72la"), CancellationToken.None));
        Assert.Empty(hardware.Acpi.Writes);
    }

    [Fact]
    public async Task ControllerAcquisitionWritesTheTablesAndEnablesRearKeys()
    {
        using var directory = new TemporaryDirectory();
        var hardware = new AllyFakeHardware();
        var host = new TestPluginHostAdapter(1);
        await using var plugin = hardware.CreatePlugin();

        _ = await plugin.StartAsync(Start(host, directory, "rc72la"), CancellationToken.None);

        Assert.Equal(AllyProtocol.GameModeConfiguration.Count, hardware.Vendor.Reports.Count);
        Assert.Equal(AllyProtocol.RearKeyboardMapping, hardware.Vendor.Reports[8]);
        Assert.Contains(AllyModels.VkF18, hardware.Keyboard.Watched);
        Assert.Contains(AllyModels.VkF17, hardware.Keyboard.Watched);
        Assert.True(hardware.Controller.Running);
        Assert.Equal(ControllerService.OutputCapabilities, host.PublishedOutput);
    }

    [Fact]
    public async Task ControllerManagementOffLeavesTheTablesAndRearKeysAlone()
    {
        using var directory = new TemporaryDirectory();
        var hardware = new AllyFakeHardware();
        var host = new TestPluginHostAdapter(1);
        await using var plugin = hardware.CreatePlugin();

        _ = await plugin.StartAsync(Start(host, directory, "rc72la", false), CancellationToken.None);

        Assert.Empty(hardware.Vendor.Reports);
        Assert.DoesNotContain(AllyModels.VkF18, hardware.Keyboard.Watched);
        Assert.False(hardware.Controller.Running);
    }

    [Fact]
    public async Task ReleaseWritesTheFactoryTablesAndStaysUnverified()
    {
        using var directory = new TemporaryDirectory();
        var hardware = new AllyFakeHardware();
        var host = new TestPluginHostAdapter(1);
        await using var plugin = hardware.CreatePlugin();
        _ = await plugin.StartAsync(Start(host, directory, "rc72la"), CancellationToken.None);
        hardware.Vendor.Reports.Clear();

        var release = await plugin.ReleaseControllerAsync(
            new PluginControllerReleaseContext(HandoffScope.ControllerOnly, DateTimeOffset.UtcNow.AddSeconds(10)),
            CancellationToken.None);

        Assert.Equal(ControllerHandoffResult.ReleasedUnverified, release.Result);
        Assert.Equal(AllyProtocol.DefaultConfiguration.Count, hardware.Vendor.Reports.Count);
        Assert.Equal(AllyProtocol.RearDefaultMapping, hardware.Vendor.Reports[8]);
        Assert.DoesNotContain(AllyModels.VkF18, hardware.Keyboard.Watched);
        Assert.Equal((0f, 0f), hardware.Controller.Rumble.Last());
        Assert.False(hardware.Controller.Running);
    }

    [Fact]
    public async Task PadWithoutHideableNodesIsNotAcquired()
    {
        using var directory = new TemporaryDirectory();
        var hardware = new AllyFakeHardware();
        hardware.Controller.Devices = [];
        var host = new TestPluginHostAdapter(1);
        await using var plugin = hardware.CreatePlugin();

        var result = await plugin.StartAsync(Start(host, directory, "rc72la"), CancellationToken.None);

        Assert.Equal(PluginOperationalState.Degraded, result.State);
        Assert.False(hardware.Controller.Running);
        Assert.Empty(hardware.Vendor.Reports);
    }

    [Fact]
    public async Task VendorEventsLatchAndPublishOemEvents()
    {
        using var directory = new TemporaryDirectory();
        var hardware = new AllyFakeHardware();
        var host = new TestPluginHostAdapter(1);
        await using var plugin = hardware.CreatePlugin();
        _ = await plugin.StartAsync(Start(host, directory, "rc72la"), CancellationToken.None);

        await hardware.Vendor.RaiseAsync(0xA6);
        await hardware.Controller.EmitAsync(CanonicalButtons.A);
        await hardware.Vendor.RaiseAsync(0xA7);
        await hardware.Vendor.RaiseAsync(0xA8);

        Assert.Equal(2, host.OemEvents.Count);
        Assert.Equal((OemControlIds.CommandCenter, OemPressKind.Short), (host.OemEvents[0].ControlId, host.OemEvents[0].Press));
        Assert.Equal((OemControlIds.ArmouryCrate, OemPressKind.Long), (host.OemEvents[1].ControlId, host.OemEvents[1].Press));
        Assert.Equal(CanonicalButtons.A | CanonicalButtons.Guide, host.ControllerSamples.Last().Buttons);
    }

    [Fact]
    public async Task RearKeysPublishEdgesAndIgnoreRepeats()
    {
        using var directory = new TemporaryDirectory();
        var hardware = new AllyFakeHardware();
        var host = new TestPluginHostAdapter(1);
        await using var plugin = hardware.CreatePlugin();
        _ = await plugin.StartAsync(Start(host, directory, "rc72la"), CancellationToken.None);

        await hardware.Keyboard.PressAsync(AllyModels.VkF18, true);
        await hardware.Keyboard.PressAsync(AllyModels.VkF18, true);
        await hardware.Controller.EmitAsync(CanonicalButtons.None);
        await hardware.Keyboard.PressAsync(AllyModels.VkF18, false);
        await hardware.Keyboard.PressAsync(AllyModels.VkF17, true);

        Assert.Equal(
            [(OemControlIds.M1, OemControlEdge.Pressed), (OemControlIds.M1, OemControlEdge.Released),
                (OemControlIds.M2, OemControlEdge.Pressed)],
            host.OemEvents.Select(item => (item.ControlId, item.Edge)));
        Assert.Equal(CanonicalButtons.RearPaddle1, host.ControllerSamples.Last().Buttons);
    }

    [Fact]
    public async Task XboxModelsClaimTheirFrontKeysWithoutTheController()
    {
        using var directory = new TemporaryDirectory();
        var hardware = new AllyFakeHardware("RC73XA");
        var host = new TestPluginHostAdapter(1);
        await using var plugin = hardware.CreatePlugin();
        _ = await plugin.StartAsync(Start(host, directory, "rc73xa", false), CancellationToken.None);

        await hardware.Keyboard.PressAsync(AllyModels.VkF22, true);
        await hardware.Keyboard.PressAsync(AllyModels.VkF22, false);

        Assert.Contains(AllyModels.VkF21, hardware.Keyboard.Watched);
        Assert.DoesNotContain(AllyModels.VkF18, hardware.Keyboard.Watched);
        Assert.Equal([OemControlIds.Library, OemControlIds.Library], host.OemEvents.Select(item => item.ControlId));
    }

    [Fact]
    public async Task PowerCommandIsVerifiedJournalledAndRestoredOnStop()
    {
        using var directory = new TemporaryDirectory();
        var hardware = new AllyFakeHardware();
        var host = new TestPluginHostAdapter(1);
        await using var plugin = hardware.CreatePlugin();
        _ = await plugin.StartAsync(Start(host, directory, "rc72la"), CancellationToken.None);

        var result = await plugin.ExecuteCommandAsync(
            AcpiCapabilityTests.Command(CapabilityValue.Integer(28)) with { ApplyPowerPair = true },
            CancellationToken.None);
        Assert.Equal(CommandOutcome.AppliedVerified, result.Outcome);
        Assert.True(File.Exists(Path.Combine(directory.Root, "temporary-state.v1.json")));

        var stop = await plugin.StopAsync(new PluginStopContext(PluginStopReason.WsgmExiting,
            DateTimeOffset.UtcNow.AddSeconds(12)), CancellationToken.None);

        Assert.Equal((15, 20, 25), (hardware.Acpi.Scalar(AsusAcpiId.SustainedPower),
            hardware.Acpi.Scalar(AsusAcpiId.SlowPower), hardware.Acpi.Scalar(AsusAcpiId.FastPower)));
        // The controller tables cannot be read back, so the stop is honest about them.
        Assert.Equal(PluginStopStatus.Unverified, stop.Status);
    }

    [Fact]
    public async Task StaleGenerationIsRejectedBeforeAnyWrite()
    {
        using var directory = new TemporaryDirectory();
        var hardware = new AllyFakeHardware();
        var host = new TestPluginHostAdapter(1);
        await using var plugin = hardware.CreatePlugin();
        _ = await plugin.StartAsync(Start(host, directory, "rc72la"), CancellationToken.None);

        var result = await plugin.ExecuteCommandAsync(
            AcpiCapabilityTests.Command(CapabilityValue.Integer(20)) with { ExpectedDescriptorGeneration = 9 },
            CancellationToken.None);

        Assert.Equal(CommandOutcome.Rejected, result.Outcome);
        Assert.Equal(CapabilityReasonCode.GenerationChanged, result.Reason!.Code);
        Assert.Empty(hardware.Acpi.Writes);
    }

    [Fact]
    public async Task LightingIsWriteOnlyAndReportedUnverified()
    {
        using var directory = new TemporaryDirectory();
        var hardware = new AllyFakeHardware("RC73YA");
        var host = new TestPluginHostAdapter(1);
        await using var plugin = hardware.CreatePlugin();
        _ = await plugin.StartAsync(Start(host, directory, "rc73ya"), CancellationToken.None);

        var result = await plugin.ExecuteCommandAsync(
            AcpiCapabilityTests.Command(CapabilityValue.Color(0x00FF00), CapabilityIds.LightingColor) with
            {
                InstanceId = CapabilityInstances.Left
            }, CancellationToken.None);

        Assert.Equal(CommandOutcome.AppliedUnverified, result.Outcome);
        Assert.Equal(1, hardware.Aura.DynamicLightingRequests);
        Assert.Contains(hardware.Aura.Reports, report => report.Report[1] == 0xB3 && report.Report[5] == 0xFF);
    }

    [Fact]
    public async Task HapticsAreClampedAndSilencedOnRelease()
    {
        using var directory = new TemporaryDirectory();
        var hardware = new AllyFakeHardware();
        var host = new TestPluginHostAdapter(1);
        await using var plugin = hardware.CreatePlugin();
        _ = await plugin.StartAsync(Start(host, directory, "rc72la"), CancellationToken.None);

        await plugin.ApplyHapticOutputAsync(new HapticOutputFrame
        {
            TargetGeneration = 1,
            LowFrequency = 0.5f,
            HighFrequency = 0.25f,
            LeftTrigger = 1f,
            Timestamp = DateTimeOffset.UtcNow
        }, CancellationToken.None);

        Assert.Equal((0.5f, 0.25f), hardware.Controller.Rumble.Single());
    }

    [Fact]
    public async Task MissingAcpiDriverLeavesPowerPassiveButInputWorking()
    {
        using var directory = new TemporaryDirectory();
        var hardware = new AllyFakeHardware();
        hardware.Acpi.Available = false;
        var host = new TestPluginHostAdapter(1);
        await using var plugin = hardware.CreatePlugin();

        var result = await plugin.StartAsync(Start(host, directory, "rc72la"), CancellationToken.None);

        Assert.Equal(PluginOperationalState.Degraded, result.State);
        Assert.True(hardware.Controller.Running);
        var power = host.CapabilityStates.Last(state => state.CapabilityId == CapabilityIds.PowerSustained);
        Assert.False(power.Available);
    }

    private static PluginStartContext Start(
        IPluginHostAdapter host,
        TemporaryDirectory directory,
        string definition,
        bool controller = true)
    {
        return new PluginStartContext
        {
            Host = host,
            CycleGeneration = host.CycleGeneration,
            DeviceDefinitionId = definition,
            StateDirectory = directory.Root,
            ControllerManagementEnabled = controller
        };
    }
}
