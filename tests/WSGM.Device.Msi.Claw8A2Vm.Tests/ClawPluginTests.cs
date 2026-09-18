using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using WSGM.Device.Msi.Claw8A2Vm.Tests.Fakes;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Identity;
using WSGM.Device.Sdk.Input;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Device.Sdk.Plugin;
using WSGM.Device.Sdk.Testing;
using WSGM.Device.Tests;
using static WSGM.Device.Msi.Claw8A2Vm.Tests.Builders.ClawCommands;

namespace WSGM.Device.Msi.Claw8A2Vm.Tests;

// PluginTrace is a process-wide static and the plugin installs its own sink in StartAsync, so any
// class that drives the lifecycle has to be serialized against one that asserts on traces.
[Collection("plugin-trace")]
public sealed class ClawPluginTests
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    [Fact]
    public async Task DetectAsync_ExactBaseboardAndSku_MatchesWithoutMarketingName()
    {
        await using Claw8A2VmPlugin plugin = new(CreateServices());

        var result = await plugin.DetectAsync(
            new PluginDetectionContext
            {
                Identity = ExactIdentity() with { SystemProduct = "localized marketing name" }
            },
            CancellationToken.None);

        Assert.True(result.Matched);
        Assert.Equal(ClawHardwareFacts.DeviceDefinitionId, result.DeviceDefinitionId);
    }

    [Theory]
    [InlineData("manufacturer")]
    [InlineData("baseboard")]
    [InlineData("sku")]
    public async Task DetectAsync_AnyExactIdentitySignalDiffers_FailsClosed(string changedSignal)
    {
        var identity = changedSignal switch
        {
            "manufacturer" => ExactIdentity() with { SystemManufacturer = "Other vendor" },
            "baseboard" => ExactIdentity() with { BaseboardProduct = "MS-1T42" },
            "sku" => ExactIdentity() with { SystemSku = "1T42.1" },
            _ => throw new ArgumentOutOfRangeException(nameof(changedSignal))
        };
        await using Claw8A2VmPlugin plugin = new(CreateServices());

        var result = await plugin.DetectAsync(
            new PluginDetectionContext { Identity = identity },
            CancellationToken.None);

        Assert.False(result.Matched);
        Assert.Null(result.DeviceDefinitionId);
        Assert.Equal(CapabilityReasonCode.Unsupported, result.Reason?.Code);
    }

    [Fact]
    public async Task WindowsIdentityReader_NonClawStopsBeforeEveryEcAndControllerProbe()
    {
        FakeWmiTransport wmi = new();
        var controllerInventoryCalled = false;
        var acPowerCalled = false;
        WindowsClawIdentityReader reader = new(
            wmi,
            () => ExactIdentity() with { BaseboardProduct = "not-a-claw" },
            () =>
            {
                controllerInventoryCalled = true;
                throw new InvalidOperationException("The controller inventory must remain unreachable.");
            },
            () =>
            {
                acPowerCalled = true;
                throw new InvalidOperationException("The AC-power query must remain unreachable.");
            });

        var result = await reader.ReadAsync(CancellationToken.None);

        Assert.False(result.ExactMachineMatch);
        Assert.False(result.WmiFirmwareVerified);
        Assert.False(result.OnAcPower);
        Assert.Equal(0, wmi.ProviderAvailabilityChecks);
        Assert.False(controllerInventoryCalled);
        Assert.False(acPowerCalled);
    }

    [Fact]
    public async Task StartAsync_ControllerManagementOffIsAnIntentionalActiveState()
    {
        using TemporaryDirectory state = new();
        await using Claw8A2VmPlugin plugin = new(CreateServices());
        TestPluginHostAdapter host = new(CycleGeneration);

        var result = await plugin.StartAsync(
            StartContext(host, state.Root),
            CancellationToken.None);

        Assert.Equal(PluginOperationalState.Active, result.State);
        Assert.Contains(host.CapabilityStates, capability =>
            capability is
            {
                CapabilityId: CapabilityIds.Controller,
                Available: false,
                Reason.Code: CapabilityReasonCode.ResourceReleased
            });
    }

    // The lifecycle order is safety-relevant: stop releases the controller and motion before the
    // power, fan and charge state is restored, and chord suppression needs the OEM source first.
    // Reordering either array must be a deliberate change to this test.
    [Fact]
    public async Task StartAsync_OrdersServicesForAcquisitionAndSuspension()
    {
        using TemporaryDirectory state = new();
        await using Claw8A2VmPlugin plugin = new(CreateServices());
        TestPluginHostAdapter host = new(CycleGeneration);

        await plugin.StartAsync(StartContext(host, state.Root), CancellationToken.None);

        Assert.Equal(
            [
                "msi-oem-events",
                "msi-power",
                "msi-charge-limit",
                "msi-fans",
                "msi-telemetry",
                "claw-lighting",
                "claw-motion",
                "physical-controller",
                "firmware-chord-suppressor"
            ],
            ServiceOrder(plugin, "_cycleServices"));
        Assert.Equal(
            ["msi-oem-events", "claw-motion", "physical-controller", "firmware-chord-suppressor"],
            ServiceOrder(plugin, "_suspendableServices"));
    }

    [Fact]
    public async Task StartAsync_FakeHardware_PublishesDirectCapabilityAndOemSurfaces()
    {
        using TemporaryDirectory state = new();
        FakeOemEventSource oem = new();
        await using Claw8A2VmPlugin plugin = new(CreateServices(oemEvents: oem));
        TestPluginHostAdapter host = new(CycleGeneration);

        var result = await plugin.StartAsync(
            StartContext(host, state.Root),
            CancellationToken.None);
        await oem.EmitAsync(0x2A, DateTimeOffset.UnixEpoch);

        Assert.Equal(PluginOperationalState.Active, result.State);
        var descriptors = Assert.Single(host.DescriptorSets);

        // Offline publication for the host's full Device-page visual fixture. No live hardware is used.
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "claw-ui-publication.json"),
            JsonSerializer.Serialize(new { Descriptors = descriptors, States = host.CapabilityStates }, IndentedJson));

        // The overlay layout ships with the set, and a dangling reference would silently strand a
        // row in a WSGM fallback group. Cooling was folded into Power, then Display's single
        // variable-refresh toggle joined it and the three ownership rows became Info
        // (maintainer-directed), so the Device overlay is three sections.
        Assert.Equal(3, descriptors.Sections.Count);
        Assert.All(descriptors.Sections,
            section => { Assert.True(section.TryValidate(out var sectionError), sectionError); });
        Assert.All(
            descriptors.Descriptors,
            descriptor =>
            {
                Assert.NotNull(descriptor.SectionId);
                var home = Assert.Single(
                    descriptors.Sections,
                    section => section.SectionId == descriptor.SectionId);
                if (descriptor.CategoryId is { } categoryId)
                {
                    Assert.Single(home.Categories, category => category.CategoryId == categoryId);
                }
            });
        Assert.Equal(CycleGeneration, descriptors.CycleGeneration);
        Assert.Contains(descriptors.Descriptors, descriptor =>
            descriptor.CapabilityId == CapabilityIds.PowerSustained);
        var sustained = Assert.Single(descriptors.Descriptors,
            descriptor => descriptor.CapabilityId == CapabilityIds.PowerSustained);
        Assert.True(DevicePowerPreset.TryValidate(descriptors.Descriptors, out var presetError), presetError);
        Assert.Equal([
            new DevicePowerPreset("super-battery", "Super Battery", 8, 9, DevicePowerMode.BetterBattery)
                { ScenarioOnAc = "eco", ScenarioOnDc = "comfort" },
            new DevicePowerPreset("balanced", "Balanced", 17, 18, DevicePowerMode.Balanced)
                { ScenarioOnAc = "green", ScenarioOnDc = "comfort" },
            new DevicePowerPreset("extreme-performance", "Extreme Performance", 30, 31, DevicePowerMode.BestPerformance)
                { ScenarioOnAc = "sport", ScenarioOnDc = "comfort" },
            new DevicePowerPreset("full-power", "Full Power", 37, 37, DevicePowerMode.BestPerformance)
                { ScenarioOnAc = "sport", ScenarioOnDc = "comfort" }
        ], sustained.PowerPresets);
        Assert.Contains(descriptors.Descriptors, descriptor =>
            descriptor is
            {
                CapabilityId: CapabilityIds.ChargeLimit,
                Role: CapabilityRole.ChargeLimit,
                Persistence: CapabilityPersistence.DevicePersistent
            });
        Assert.Contains(descriptors.Descriptors, descriptor =>
            descriptor is { CapabilityId: CapabilityIds.LightingColor, InstanceId: CapabilityInstances.Buttons });
        Assert.Equal(descriptors.Descriptors.Count, host.CapabilityStates.Count);
        Assert.Contains(host.CapabilityStates, capability =>
            capability is
                { CapabilityId: CapabilityIds.PowerSustained, Available: true, ObservedValue.IntegerValue: 30 });
        Assert.Contains(host.CapabilityStates, capability =>
            capability is { CapabilityId: CapabilityIds.ChargeLimit, Available: true, ObservedValue.IntegerValue: 80 });
        Assert.Contains(host.CapabilityStates, capability =>
            capability is { CapabilityId: CapabilityIds.Controller, Available: false });
        Assert.Equal(4, Assert.Single(host.OemControlSets).Count);
        var controlEvent = Assert.Single(host.OemEvents);
        Assert.Equal("oem2", controlEvent.ControlId);
        Assert.Equal(OemPressKind.Long, controlEvent.Press);
        Assert.Equal(CycleGeneration, controlEvent.SourceGeneration);
    }

    [Fact]
    public async Task StartAsync_PublicationFailure_RetractsEveryAcceptedSurface()
    {
        using TemporaryDirectory state = new();
        await using Claw8A2VmPlugin plugin = new(CreateServices());
        ControllablePluginHostAdapter host = new(CycleGeneration)
        {
            FailNextNonEmptyOemPublication = true
        };

        await Assert.ThrowsAsync<IOException>(async () =>
            await plugin.StartAsync(StartContext(host, state.Root), CancellationToken.None));

        Assert.Equal(2, host.DescriptorSets.Count);
        Assert.NotEmpty(host.DescriptorSets[0].Descriptors);
        Assert.Empty(host.DescriptorSets[1].Descriptors);
        Assert.True(host.DescriptorSets[1].Generation > host.DescriptorSets[0].Generation);
        Assert.Equal(2, host.OemControlSets.Count);
        Assert.NotEmpty(host.OemControlSets[0]);
        Assert.Empty(host.OemControlSets[1]);
        Assert.Empty(Assert.Single(host.PhysicalDeviceSets));

        var diagnostics = await plugin.GetDiagnosticsAsync(CancellationToken.None);
        Assert.Equal("stopped", diagnostics.Values["cycle"]);
        Assert.Equal("unavailable", diagnostics.Values["recovery"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ControllerStop_CancelsAndUnblocksAnInFlightHostPublication(bool rearOemEvent)
    {
        using TemporaryDirectory state = new();
        FakeControllerSource source = new() { Topology = DirectInputTopology() };
        ControllablePluginHostAdapter host = new(CycleGeneration)
        {
            BlockControllerSamples = !rearOemEvent,
            BlockOemEvents = rearOemEvent
        };
        await using var journal = await ClawRecoveryJournal.OpenAsync(
            state.Root,
            CancellationToken.None);
        ControllerService controller = new(
            new FakeIdentityReader(),
            new FakeMcuTransport(),
            source,
            new MotionService(new FakeMotionSource()),
            host,
            journal)
        {
            Enabled = true
        };
        _ = await controller.AcquireAsync(
            new ClawCycleContext(CycleGeneration, DateTimeOffset.UtcNow.AddSeconds(10)),
            CancellationToken.None);
        var publication = source.EmitAsync(new CanonicalControllerSample
        {
            Sequence = 1,
            CycleGeneration = CycleGeneration,
            Timestamp = DateTimeOffset.UtcNow,
            Buttons = rearOemEvent ? CanonicalButtons.RearPaddle1 : CanonicalButtons.None
        }).AsTask();
        var blockedPublication = rearOemEvent ? host.OemEventEntered : host.ControllerSampleEntered;
        await blockedPublication.WaitAsync(TimeSpan.FromSeconds(2));

        var result = await controller.ReleaseControllerAsync(
            DateTimeOffset.UtcNow.AddSeconds(10),
            CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(ControllerHandoffResult.ReleasedVerified, result);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await publication);
    }

    [Fact]
    public async Task ChordSuppressor_BackgroundFaultPropagatesAsBoundedServiceFailure()
    {
        FakeOemEventSource oemSource = new();
        ControllablePluginHostAdapter host = new(CycleGeneration);
        OemEventService oem = new(oemSource, host, new ClawOemButtonLatch());
        _ = await oem.AcquireAsync(
            new ClawCycleContext(CycleGeneration, DateTimeOffset.UtcNow.AddSeconds(10)),
            CancellationToken.None);
        FakeChordSuppressor hook = new();
        ChordSuppressorService suppressor = new(hook, oem, host);
        _ = await suppressor.AcquireAsync(
            new ClawCycleContext(CycleGeneration, DateTimeOffset.UtcNow.AddSeconds(10)),
            CancellationToken.None);

        hook.TriggerFault(new IOException(new string('x', 1500) + "\nsecond line"));

        Assert.Equal(ClawServiceState.Faulted, suppressor.State);
        var (scope, message) = Assert.Single(host.Faults);
        Assert.Equal(ServiceIds.ChordSuppressor, scope);
        Assert.True(message.Length <= PluginTrace.MaxMessageLength);
        Assert.DoesNotContain('\n', message);
    }

    [Fact]
    public async Task ExecuteCommandAsync_PowerWrite_ReadsBackAndStopRestoresCompactJournal()
    {
        using TemporaryDirectory state = new();
        FakeWmiTransport wmi = new();
        await using Claw8A2VmPlugin plugin = new(CreateServices(wmi));
        TestPluginHostAdapter host = new(CycleGeneration);
        _ = await plugin.StartAsync(StartContext(host, state.Root), CancellationToken.None);
        var command = Command(
            CapabilityIds.PowerSustained,
            null,
            CapabilityValue.Integer(25));

        var result = await plugin.ExecuteCommandAsync(command, CancellationToken.None);

        Assert.Equal(CommandOutcome.AppliedVerified, result.Outcome);
        Assert.Equal(25, result.ReadbackValue?.IntegerValue);
        Assert.Equal(25, wmi.ReadData(ClawHardwareFacts.PowerSustainedAddress));
        await using (var pending = await ClawRecoveryJournal.OpenAsync(
                         state.Root,
                         CancellationToken.None))
        {
            var entry = Assert.Single(pending.OutstandingEntries);
            Assert.Equal(ServiceIds.Power, entry.ServiceId);
            Assert.Equal(ClawRecoveryStatus.Pending, entry.Status);
            Assert.True(ClawRecoveryValues.TryPower(entry.OriginalState, out var original));
            Assert.Equal(new PowerPair(30, 37, 0xC1), original);
        }

        var stop = await plugin.StopAsync(
            new PluginStopContext(
                PluginStopReason.IntegrationDisabled,
                DateTimeOffset.UtcNow.AddSeconds(10)),
            CancellationToken.None);

        Assert.Equal(PluginStopStatus.Clean, stop.Status);
        Assert.Equal(30, wmi.ReadData(ClawHardwareFacts.PowerSustainedAddress));
        Assert.Equal(
            [25, 30],
            wmi.Writes
                .Where(write => write.Method == "Set_Data"
                                && write.Package[0] == ClawHardwareFacts.PowerSustainedAddress)
                .Select(write => BinaryPrimitives.ReadInt32LittleEndian(write.Package.AsSpan(1, sizeof(int)))));
        await using var completed = await ClawRecoveryJournal.OpenAsync(
            state.Root,
            CancellationToken.None);
        Assert.Empty(completed.OutstandingEntries);
    }

    [Theory]
    [InlineData("comfort", 0xC0)]
    [InlineData("green", 0xC1)]
    [InlineData("eco", 0xC2)]
    [InlineData("user", 0xC3)]
    [InlineData("sport", 0xC4)]
    public async Task ScenarioSelectionVerifiesAndStopRestoresFirstOriginal(string scenario, int expected)
    {
        using TemporaryDirectory state = new();
        FakeWmiTransport wmi = new();
        wmi.SetData(ClawHardwareFacts.ScenarioAddress, 0x81);
        wmi.AfterSetter = (method, package) =>
        {
            if (method != "Set_Data" || package[0] != ClawHardwareFacts.ScenarioAddress)
            {
                return;
            }

            wmi.SetData(ClawHardwareFacts.PowerSustainedAddress, 8);
            wmi.SetData(ClawHardwareFacts.PowerBoostAddress, 9);
        };
        await using Claw8A2VmPlugin plugin = new(CreateServices(wmi));
        TestPluginHostAdapter host = new(CycleGeneration);
        await plugin.StartAsync(StartContext(host, state.Root), CancellationToken.None);
        Assert.Equal("inactive",
            host.CapabilityStates.Last(s => s.CapabilityId == CapabilityIds.Scenario).ObservedValue?.ChoiceValue);
        var result = await plugin.ExecuteCommandAsync(Command(CapabilityIds.Scenario, null,
            CapabilityValue.Choice(scenario)), CancellationToken.None);
        Assert.Equal(CommandOutcome.AppliedVerified, result.Outcome);
        Assert.Equal(expected, wmi.ReadData(ClawHardwareFacts.ScenarioAddress));
        Assert.Equal(scenario, result.ReadbackValue?.ChoiceValue);
        Assert.Equal(8,
            host.CapabilityStates.Last(s => s.CapabilityId == CapabilityIds.PowerSustained).ObservedValue
                ?.IntegerValue);
        var stop = await plugin.StopAsync(new PluginStopContext(PluginStopReason.IntegrationDisabled,
            DateTimeOffset.UtcNow.AddSeconds(10)), CancellationToken.None);
        Assert.Equal(PluginStopStatus.Clean, stop.Status);
        Assert.Equal(0x81, wmi.ReadData(ClawHardwareFacts.ScenarioAddress));
        Assert.Equal(30, wmi.ReadData(ClawHardwareFacts.PowerSustainedAddress));
        Assert.Equal(37, wmi.ReadData(ClawHardwareFacts.PowerBoostAddress));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BlockedPostCommandPublicationHonorsTimeoutOrStopWithoutInventingRollback(bool stop)
    {
        using TemporaryDirectory state = new();
        FakeWmiTransport wmi = new();
        await using Claw8A2VmPlugin plugin = new(CreateServices(wmi));
        ControllablePluginHostAdapter host = new(CycleGeneration);
        await plugin.StartAsync(StartContext(host, state.Root), CancellationToken.None);
        TaskCompletionSource blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        wmi.AfterSetter = (method, package) =>
        {
            if (method == "Set_Data" && package[0] == ClawHardwareFacts.ScenarioAddress && package[1] == 0xC4)
            {
                host.CapabilityPublicationBlock = blocked;
            }
        };
        var command = Command(CapabilityIds.Scenario, null,
                CapabilityValue.Choice("sport")) with
            {
                Deadline = DateTimeOffset.UtcNow.AddSeconds(10)
            };
        var applying = plugin.ExecuteCommandAsync(command, CancellationToken.None).AsTask();
        try
        {
            await host.CapabilityPublicationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            host.CapabilityPublicationBlock = null;
            var stopping = stop
                ? plugin.StopAsync(new PluginStopContext(
                            PluginStopReason.IntegrationDisabled, DateTimeOffset.UtcNow.AddSeconds(5)),
                        CancellationToken.None)
                    .AsTask()
                : null;
            var result = await applying.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(CommandOutcome.Indeterminate, result.Outcome);
            Assert.Null(result.ReadbackValue);
            Assert.Equal(RollbackResult.NotRequired, result.Rollback);
            if (stopping is not null)
            {
                Assert.Equal(PluginStopStatus.Clean, (await stopping).Status);
            }
        }
        finally
        {
            blocked.TrySetResult();
        }
    }

    [Fact]
    public async Task ExecuteCommandAsync_ChargeLimitPersistsAcrossPluginStop()
    {
        using TemporaryDirectory state = new();
        FakeWmiTransport wmi = new();
        await using Claw8A2VmPlugin plugin = new(CreateServices(wmi));
        TestPluginHostAdapter host = new(CycleGeneration);
        _ = await plugin.StartAsync(StartContext(host, state.Root), CancellationToken.None);
        var command = Command(
            CapabilityIds.ChargeLimit,
            null,
            CapabilityValue.Integer(60));

        var result = await plugin.ExecuteCommandAsync(command, CancellationToken.None);
        var stop = await plugin.StopAsync(
            new PluginStopContext(
                PluginStopReason.IntegrationDisabled,
                DateTimeOffset.UtcNow.AddSeconds(10)),
            CancellationToken.None);

        Assert.Equal(CommandOutcome.AppliedVerified, result.Outcome);
        Assert.Equal(PluginStopStatus.Clean, stop.Status);
        Assert.Equal(60, wmi.ReadData(ClawHardwareFacts.ChargeLimitAddress));
    }

    [Fact]
    public async Task StopAsync_RestoresStateCapturedImmediatelyBeforeFirstMutation()
    {
        using TemporaryDirectory state = new();
        FakeWmiTransport wmi = new();
        await using Claw8A2VmPlugin plugin = new(CreateServices(wmi));
        TestPluginHostAdapter host = new(CycleGeneration);
        _ = await plugin.StartAsync(StartContext(host, state.Root), CancellationToken.None);

        // Another manager may legitimately change the resource after plugin acquisition but before
        // WSGM's first write. The command journal, not the stale acquisition observation, owns the
        // value that handoff must restore.
        wmi.SetData(ClawHardwareFacts.PowerSustainedAddress, 28);
        var command = Command(
            CapabilityIds.PowerSustained,
            null,
            CapabilityValue.Integer(25));

        var result = await plugin.ExecuteCommandAsync(command, CancellationToken.None);
        var stop = await plugin.StopAsync(
            new PluginStopContext(
                PluginStopReason.IntegrationDisabled,
                DateTimeOffset.UtcNow.AddSeconds(10)),
            CancellationToken.None);

        Assert.Equal(CommandOutcome.AppliedVerified, result.Outcome);
        Assert.Equal(PluginStopStatus.Clean, stop.Status);
        Assert.Equal(28, wmi.ReadData(ClawHardwareFacts.PowerSustainedAddress));
    }

    [Fact]
    public async Task ApplyHaptics_FailedWriteDoesNotSuppressIdenticalRetry()
    {
        using TemporaryDirectory state = new();
        FakeControllerSource source = new() { Topology = DirectInputTopology(), FailNextRumble = true };
        TestPluginHostAdapter host = new(CycleGeneration);
        await using var journal = await ClawRecoveryJournal.OpenAsync(
            state.Root,
            CancellationToken.None);
        ControllerService controller = new(
            new FakeIdentityReader(),
            new FakeMcuTransport(),
            source,
            new MotionService(new FakeMotionSource()),
            host,
            journal)
        {
            Enabled = true
        };
        _ = await controller.AcquireAsync(
            new ClawCycleContext(CycleGeneration, DateTimeOffset.UtcNow.AddSeconds(10)),
            CancellationToken.None);
        HapticOutputFrame frame = new()
        {
            TargetGeneration = 1,
            LowFrequency = 0.5f,
            HighFrequency = 0.25f,
            Timestamp = DateTimeOffset.UtcNow
        };

        await Assert.ThrowsAsync<IOException>(async () =>
            await controller.ApplyHapticsAsync(frame, CancellationToken.None));
        await controller.ApplyHapticsAsync(frame, CancellationToken.None);

        Assert.Equal(2, source.RumbleWriteAttempts);
    }

    [Fact]
    public async Task ReleaseController_SourceStopFailureCannotReportVerifiedHandoff()
    {
        using TemporaryDirectory state = new();
        FakeControllerSource source = new() { Topology = DirectInputTopology(), FailStop = true };
        TestPluginHostAdapter host = new(CycleGeneration);
        await using var journal = await ClawRecoveryJournal.OpenAsync(
            state.Root,
            CancellationToken.None);
        ControllerService controller = new(
            new FakeIdentityReader(),
            new FakeMcuTransport(),
            source,
            new MotionService(new FakeMotionSource()),
            host,
            journal)
        {
            Enabled = true
        };
        _ = await controller.AcquireAsync(
            new ClawCycleContext(CycleGeneration, DateTimeOffset.UtcNow.AddSeconds(10)),
            CancellationToken.None);

        var result = await controller.ReleaseControllerAsync(
            DateTimeOffset.UtcNow.AddSeconds(10),
            CancellationToken.None);

        Assert.Equal(ControllerHandoffResult.ReleasedUnverified, result);
        Assert.Equal(ClawServiceState.ReleasedUnverified, controller.State);
    }

    [Fact]
    public async Task BeginAsync_UnfinishedWrite_RetainsFirstOriginalAcrossReopen()
    {
        using TemporaryDirectory state = new();
        await using (var journal = await ClawRecoveryJournal.OpenAsync(
                         state.Root,
                         CancellationToken.None))
        {
            var operation = await journal.BeginAsync(
                ServiceIds.Power,
                CapabilityIds.PowerSustained,
                ClawFirmwareIdentities.Wmi,
                ClawRecoveryValues.Power(new PowerPair(30, 37, 0xC1)),
                CancellationToken.None);

            Assert.True(operation.Opened);
            Assert.Single(journal.OutstandingEntries);
        }

        await using var reopened = await ClawRecoveryJournal.OpenAsync(
            state.Root,
            CancellationToken.None);
        var entry = Assert.Single(reopened.OutstandingEntries);
        Assert.True(ClawRecoveryValues.TryPower(entry.OriginalState, out var original));
        Assert.Equal(new PowerPair(30, 37, 0xC1), original);

        var existing = await reopened.BeginAsync(
            ServiceIds.Power,
            CapabilityIds.PowerSustained,
            ClawFirmwareIdentities.Wmi,
            ClawRecoveryValues.Power(new PowerPair(25, 37, 0xC1)),
            CancellationToken.None);
        Assert.False(existing.Opened);
        Assert.True(ClawRecoveryValues.TryPower(existing.Entry.OriginalState, out var retained));
        Assert.Equal(new PowerPair(30, 37, 0xC1), retained);
    }

    // Journals written before 2026-09-18 bound the controller entry to MCU revision 0229. The mode
    // restore is valid on any revision, so the entry must load as the current identity and stay
    // restorable instead of blocking the controller behind an identity nothing can match again.
    [Fact]
    public async Task OpenAsync_LegacyRevisionBoundControllerEntry_LoadsAsRestorable()
    {
        using TemporaryDirectory state = new();
        await File.WriteAllTextAsync(
            Path.Combine(state.Root, "temporary-state.v1.json"),
            """
            {"version":1,"entries":[{"serviceId":"physical-controller","capabilityId":"controller.source","firmwareIdentity":"mcu:0229","originalState":{"kind":"ControllerMode","sustainedWatts":null,"boostWatts":null,"scenario":null,"leftDuty":"","leftTemperature":"","rightDuty":"","rightTemperature":"","customFlag":null,"fullSpeedFlag":null,"controllerMode":1},"status":"Pending"}]}
            """);

        await using var journal = await ClawRecoveryJournal.OpenAsync(state.Root, CancellationToken.None);

        Assert.Null(journal.FailureReason);
        var entry = Assert.Single(journal.OutstandingEntries);
        Assert.Equal(ClawFirmwareIdentities.Mcu, entry.FirmwareIdentity);
        Assert.Equal(
            ClawReconciliationAction.Restore,
            ClawRecoveryJournal.Decide(entry, ClawFirmwareIdentities.Mcu));
        Assert.True(ClawRecoveryValues.TryControllerMode(entry.OriginalState, out var mode));
        Assert.Equal(ClawControllerMode.XInput, mode);
    }

    [Fact]
    public async Task StartAsync_OutstandingCompactPowerEntry_RestoresBeforeNewOwnership()
    {
        using TemporaryDirectory state = new();
        await using (var journal = await ClawRecoveryJournal.OpenAsync(
                         state.Root,
                         CancellationToken.None))
        {
            _ = await journal.BeginAsync(
                ServiceIds.Power,
                CapabilityIds.PowerSustained,
                ClawFirmwareIdentities.Wmi,
                ClawRecoveryValues.Power(new PowerPair(30, 37, 0xC1)),
                CancellationToken.None);
        }

        FakeWmiTransport wmi = new();
        wmi.SetData(ClawHardwareFacts.PowerSustainedAddress, 25);
        await using Claw8A2VmPlugin plugin = new(CreateServices(wmi));
        TestPluginHostAdapter host = new(CycleGeneration);

        _ = await plugin.StartAsync(StartContext(host, state.Root), CancellationToken.None);

        Assert.Equal(30, wmi.ReadData(ClawHardwareFacts.PowerSustainedAddress));
        Assert.Contains(host.CapabilityStates, capability =>
            capability is
                { CapabilityId: CapabilityIds.PowerSustained, Available: true, ObservedValue.IntegerValue: 30 });
        await using var reconciled = await ClawRecoveryJournal.OpenAsync(
            state.Root,
            CancellationToken.None);
        Assert.Empty(reconciled.OutstandingEntries);
    }

    [Fact]
    public async Task StartAsync_RestoreFailureIsRetriedAndRecoveredInTheNextCycle()
    {
        using TemporaryDirectory state = new();
        await using (var journal = await ClawRecoveryJournal.OpenAsync(
                         state.Root,
                         CancellationToken.None))
        {
            _ = await journal.BeginAsync(
                ServiceIds.Power,
                CapabilityIds.PowerSustained,
                ClawFirmwareIdentities.Wmi,
                ClawRecoveryValues.Power(new PowerPair(30, 37, 0xC1)),
                CancellationToken.None);
        }

        FakeWmiTransport wmi = new() { FailNextSetter = true };
        wmi.SetData(ClawHardwareFacts.PowerSustainedAddress, 25);
        await using (Claw8A2VmPlugin firstCycle = new(CreateServices(wmi)))
        {
            TestPluginHostAdapter firstHost = new(CycleGeneration);
            _ = await firstCycle.StartAsync(
                StartContext(firstHost, state.Root),
                CancellationToken.None);
            var diagnostics = await firstCycle.GetDiagnosticsAsync(CancellationToken.None);

            Assert.Equal("pending", diagnostics.Values["recovery"]);
            Assert.Equal(nameof(ClawServiceState.Faulted), diagnostics.Values[ServiceIds.Power]);
            _ = await firstCycle.StopAsync(
                new PluginStopContext(
                    PluginStopReason.IntegrationDisabled,
                    DateTimeOffset.UtcNow.AddSeconds(10)),
                CancellationToken.None);
        }

        await using (Claw8A2VmPlugin secondCycle = new(CreateServices(wmi)))
        {
            TestPluginHostAdapter secondHost = new(CycleGeneration + 1);
            _ = await secondCycle.StartAsync(
                StartContext(secondHost, state.Root),
                CancellationToken.None);
            var diagnostics = await secondCycle.GetDiagnosticsAsync(CancellationToken.None);

            Assert.Equal(30, wmi.ReadData(ClawHardwareFacts.PowerSustainedAddress));
            Assert.Equal("healthy", diagnostics.Values["recovery"]);
            await using var reconciled = await ClawRecoveryJournal.OpenAsync(
                state.Root,
                CancellationToken.None);
            Assert.Empty(reconciled.OutstandingEntries);

            _ = await secondCycle.StopAsync(
                new PluginStopContext(
                    PluginStopReason.IntegrationDisabled,
                    DateTimeOffset.UtcNow.AddSeconds(10)),
                CancellationToken.None);
        }
    }

    private static string[] ServiceOrder(Claw8A2VmPlugin plugin, string field)
    {
        return
        [
            .. Assert.IsType<IEnumerable<ClawServiceStatus>>(typeof(Claw8A2VmPlugin)
                .GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(plugin), false).Select(service => service.ServiceId)
        ];
    }

    private static PluginStartContext StartContext(IPluginHostAdapter host, string stateDirectory)
    {
        return new PluginStartContext
        {
            Host = host,
            CycleGeneration = host.CycleGeneration,
            DeviceDefinitionId = ClawHardwareFacts.DeviceDefinitionId,
            StateDirectory = stateDirectory,
            ControllerManagementEnabled = false
        };
    }

    private static ClawHardwareServices CreateServices(
        FakeWmiTransport? wmi = null,
        FakeOemEventSource? oemEvents = null,
        FakeMcuTransport? mcu = null,
        FakeControllerSource? controller = null,
        FakeMotionSource? motion = null,
        FakeChordSuppressor? chordSuppressor = null)
    {
        return new ClawHardwareServices(
            new FakeIdentityReader(),
            wmi ?? new FakeWmiTransport(),
            oemEvents ?? new FakeOemEventSource(),
            mcu ?? new FakeMcuTransport(),
            controller ?? new FakeControllerSource(),
            motion ?? new FakeMotionSource(),
            chordSuppressor ?? new FakeChordSuppressor(),
            new ClawOemButtonLatch());
    }

    private static DeviceIdentitySnapshot ExactIdentity()
    {
        return new DeviceIdentitySnapshot
        {
            SystemManufacturer = ClawHardwareFacts.Manufacturer,
            BaseboardProduct = ClawHardwareFacts.BoardProduct,
            SystemSku = ClawHardwareFacts.SystemSku,
            UsbEndpoints =
            [
                new UsbEndpointObservation
                {
                    VendorId = ClawHardwareFacts.UsbVendorId,
                    ProductId = ClawHardwareFacts.XInputProductId,
                    DeviceRelease = "0229"
                }
            ]
        };
    }

    private static ControllerTopology DirectInputTopology()
    {
        return new ControllerTopology(
            ClawControllerMode.DirectInput,
            ClawHardwareFacts.DirectInputProductId,
            "PCIROOT(0)#USBROOT(0)#USB(2)",
            [
                new PhysicalDeviceIdentity
                {
                    InstancePath = @"HID\VID_0DB0&PID_1902\TEST",
                    LocationPath = "PCIROOT(0)#USBROOT(0)#USB(2)",
                    VendorId = ClawHardwareFacts.UsbVendorId,
                    ProductId = ClawHardwareFacts.DirectInputProductId,
                    RequiresHiding = true
                }
            ]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcPowerFailureKeepsTheDeviceAvailableWithAConservativePowerSource(bool comFailure)
    {
        var reader = Reader(() => throw (comFailure
            ? new COMException("test failure")
            : new UnauthorizedAccessException("test refusal")));

        var identity = await reader.ReadAsync(CancellationToken.None);

        Assert.True(identity.ExactMachineMatch);
        Assert.False(identity.OnAcPower);
    }

    [Fact]
    public async Task CancellationDoesNotWaitForABlockedAcPowerQuery()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = Reader(() =>
        {
            entered.SetResult();
            release.Task.Wait();
            exited.SetResult();
            return true;
        });
        using CancellationTokenSource cancellation = new();
        try
        {
            var read = reader.ReadAsync(cancellation.Token).AsTask();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                read.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None));
        }
        finally
        {
            release.TrySetResult();
            await exited.Task.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
        }
    }

    private static WindowsClawIdentityReader Reader(Func<bool> readPower)
    {
        FakeWmiTransport wmi = new();
        wmi.SetResponse("Get_WMI", 0, new byte[32]);
        wmi.SetResponse("Get_EC", 0, new byte[32]);
        return new WindowsClawIdentityReader(wmi, () => new DeviceIdentitySnapshot
        {
            SystemManufacturer = ClawHardwareFacts.Manufacturer,
            BaseboardProduct = ClawHardwareFacts.BoardProduct,
            SystemSku = ClawHardwareFacts.SystemSku
        }, () => [], readPower);
    }
}
