using WSGM.Device.Msi.Claw.Tests.Fakes;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Input;
using WSGM.Device.Sdk.Plugin;
using WSGM.Device.Sdk.Testing;
using WSGM.Device.Tests;
using static WSGM.Device.Msi.Claw.Tests.Builders.ClawCommands;

namespace WSGM.Device.Msi.Claw.Tests;

/// <summary>Per-model behaviour through the plugin's own lifecycle and services.</summary>
[Collection("plugin-trace")]
public sealed class ClawModelLifecycleTests
{
    [Fact]
    public async Task StartAsync_PublishesTheModelsPowerRangesAndPresets()
    {
        using TemporaryDirectory state = new();
        FakeIdentityReader identity = new() { Model = ClawModels.Claw8ExCg3Em };
        await using ClawPlugin plugin = new(Services(identity));
        TestPluginHostAdapter host = new(CycleGeneration);

        _ = await plugin.StartAsync(Context(host, state.Root, ClawModels.Claw8ExCg3Em), CancellationToken.None);

        var descriptors = host.DescriptorSets.Last().Descriptors;
        var sustained = descriptors.Single(item => item.CapabilityId == CapabilityIds.PowerSustained);
        var boost = descriptors.Single(item => item.CapabilityId == CapabilityIds.PowerBoost);
        Assert.Equal((15, 37), (sustained.Minimum, sustained.Maximum));
        Assert.Equal((20, 37), (boost.Minimum, boost.Maximum));
        Assert.Equal(ClawModels.Claw8ExCg3Em.PowerPresets, sustained.PowerPresets);
    }

    [Fact]
    public async Task StartAsync_RefusesAnIdentityOfAnotherModel()
    {
        using TemporaryDirectory state = new();
        FakeIdentityReader identity = new() { Model = ClawModels.A1M };
        await using ClawPlugin plugin = new(Services(identity));

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await plugin.StartAsync(
            Context(new TestPluginHostAdapter(CycleGeneration), state.Root, ClawModels.Claw8A2Vm),
            CancellationToken.None));
    }

    [Theory]
    [InlineData("0211", 0x01FA)]
    [InlineData("0230", 0x024A)]
    public async Task StartAsync_ReadsLightingAtTheAddressForTheMcuRevision(string revision, int address)
    {
        using TemporaryDirectory state = new();
        FakeMcuTransport mcu = new();
        await using ClawPlugin plugin = new(Services(new FakeIdentityReader { McuRevision = revision }, mcu: mcu));

        _ = await plugin.StartAsync(
            Context(new TestPluginHostAdapter(CycleGeneration), state.Root, ClawModels.Claw8A2Vm),
            CancellationToken.None);

        Assert.NotEmpty(mcu.ReadAddresses);
        Assert.All(mcu.ReadAddresses, read => Assert.Equal(address, read));
    }

    [Fact]
    public async Task ExecuteCommand_ChangedModelIsRefusedBeforeAnyHardwareRead()
    {
        using TemporaryDirectory state = new();
        FakeIdentityReader identity = new();
        FakeWmiTransport wmi = new();
        await using ClawPlugin plugin = new(Services(identity, wmi));
        _ = await plugin.StartAsync(
            Context(new TestPluginHostAdapter(CycleGeneration), state.Root, ClawModels.Claw8A2Vm),
            CancellationToken.None);
        identity.Model = ClawModels.A1M;
        var reads = wmi.Reads;

        var result = await plugin.ExecuteCommandAsync(
            Command(CapabilityIds.PowerSustained, null, CapabilityValue.Integer(25)), CancellationToken.None);

        Assert.Equal(CommandOutcome.Rejected, result.Outcome);
        Assert.Equal(CapabilityReasonCode.GenerationChanged, result.Reason?.Code);
        Assert.Equal(reads, wmi.Reads);
        Assert.Empty(wmi.Writes);
    }

    [Theory]
    [InlineData(0xC6, "user")]
    [InlineData(0xC3, "unknown")]
    [InlineData(0xC4, "sport")]
    [InlineData(0x86, "inactive")]
    public void ScenarioDecode_UsesTheCg3EmUserValue(int raw, string expected)
    {
        Assert.Equal(expected, ClawPlugin.Scenario((byte)raw, ClawModels.Claw8ExCg3Em).ChoiceValue);
    }

    [Fact]
    public void ScenarioDecode_KeepsUserAtThreeElsewhere()
    {
        Assert.Equal("user", ClawPlugin.Scenario(0xC3, ClawModels.Claw8A2Vm).ChoiceValue);
    }

    [Fact]
    public async Task A1MRumble_IsOnOffAt193AndPacedTo100Milliseconds()
    {
        using TemporaryDirectory state = new();
        FakeControllerSource source = new();
        TestPluginHostAdapter host = new(CycleGeneration);
        await using var journal = await ClawRecoveryJournal.OpenAsync(state.Root, CancellationToken.None);
        var controller = await AcquireControllerAsync(source, host, journal, ClawModels.A1M);

        Assert.Equal(10, host.PublishedOutput?.MaxFramesPerSecond);
        Assert.Equal(ClawModels.BinaryRumbleInterval, host.PublishedOutput?.MinimumPulse);

        await controller.ApplyHapticsAsync(Frame(0.5f, 0.25f), CancellationToken.None);
        await controller.ApplyHapticsAsync(Frame(0.9f, 0.1f), CancellationToken.None);
        Assert.Equal([(193, 193)], source.RumbleWrites.Select(write => ((int)write.Weak, (int)write.Strong)));

        // A stop is never delayed.
        await controller.ApplyHapticsAsync(Frame(0, 0), CancellationToken.None);
        Assert.Equal((0, 0), ((int)source.RumbleWrites[^1].Weak, (int)source.RumbleWrites[^1].Strong));

        // Motors back on inside the interval wait for it, then land.
        await controller.ApplyHapticsAsync(Frame(0.3f, 0), CancellationToken.None);
        Assert.Equal(2, source.RumbleWrites.Count);
        await WaitUntilAsync(() => source.RumbleWrites.Count == 3);
        Assert.Equal((0, 193), ((int)source.RumbleWrites[^1].Weak, (int)source.RumbleWrites[^1].Strong));
    }

    [Fact]
    public async Task A1MRumble_StopCancelsAPendingPacedWrite()
    {
        using TemporaryDirectory state = new();
        FakeControllerSource source = new() { Topology = DirectInputTopology() };
        TestPluginHostAdapter host = new(CycleGeneration);
        await using var journal = await ClawRecoveryJournal.OpenAsync(state.Root, CancellationToken.None);
        var controller = await AcquireControllerAsync(source, host, journal, ClawModels.A1M);
        await controller.ApplyHapticsAsync(Frame(1, 1), CancellationToken.None);
        await controller.ApplyHapticsAsync(Frame(0, 0), CancellationToken.None);
        await controller.ApplyHapticsAsync(Frame(1, 1), CancellationToken.None);

        _ = await controller.ReleaseControllerAsync(DateTimeOffset.UtcNow.AddSeconds(10), CancellationToken.None);
        await Task.Delay(ClawModels.BinaryRumbleInterval * 2);

        Assert.Equal((0, 0), ((int)source.RumbleWrites[^1].Weak, (int)source.RumbleWrites[^1].Strong));
    }

    [Fact]
    public async Task A2VmRumble_StaysProportional()
    {
        using TemporaryDirectory state = new();
        FakeControllerSource source = new();
        TestPluginHostAdapter host = new(CycleGeneration);
        await using var journal = await ClawRecoveryJournal.OpenAsync(state.Root, CancellationToken.None);
        var controller = await AcquireControllerAsync(source, host, journal, ClawModels.Claw8A2Vm);

        await controller.ApplyHapticsAsync(Frame(0.5f, 0.25f), CancellationToken.None);

        Assert.Equal(250, host.PublishedOutput?.MaxFramesPerSecond);
        Assert.NotEqual(193, source.RumbleWrites.Single().Strong);
    }

    [Theory]
    [InlineData(15, 15, 20)]
    [InlineData(25, 25, 25)]
    public async Task Cg3EmPair_HoldsBoostAtItsFloor(int target, int sustained, int boost)
    {
        FakeWmiTransport wmi = new();
        ClawPowerCapability power = new(wmi, ClawModels.Claw8ExCg3Em);
        var command = Command(CapabilityIds.PowerSustained, null, CapabilityValue.Integer(target)) with
        {
            ApplyPowerPair = true
        };

        var result = await power.ApplySustainedAsync(command, target, CancellationToken.None);

        Assert.Equal(CommandOutcome.AppliedVerified, result.Outcome);
        Assert.Equal(sustained, wmi.ReadData(ClawHardwareFacts.PowerSustainedAddress));
        Assert.Equal(boost, wmi.ReadData(ClawHardwareFacts.PowerBoostAddress));
    }

    [Fact]
    public async Task Cg3EmBoost_BelowItsFloorIsRejected()
    {
        FakeWmiTransport wmi = new();
        ClawPowerCapability power = new(wmi, ClawModels.Claw8ExCg3Em);

        var result = await power.ApplyBoostAsync(
            Command(CapabilityIds.PowerBoost, null, CapabilityValue.Integer(19)), 19, CancellationToken.None);

        Assert.Equal(CommandOutcome.Rejected, result.Outcome);
        Assert.Empty(wmi.Writes);
    }

    [Fact]
    public async Task Bz2EmSustainedChange_RewritesTheFastRegisterWithAnUnchangedBoost()
    {
        FakeWmiTransport wmi = new();
        wmi.SetData(ClawHardwareFacts.PowerSustainedAddress, 20);
        wmi.SetData(ClawHardwareFacts.PowerBoostAddress, 28);
        wmi.SetData(ClawHardwareFacts.PowerFastAddress, 35);
        ClawPowerCapability power = new(wmi, ClawModels.A8Bz2Em);

        _ = await power.ApplySustainedAsync(
            Command(CapabilityIds.PowerSustained, null, CapabilityValue.Integer(18)), 18, CancellationToken.None);

        Assert.Equal(28, wmi.ReadData(ClawHardwareFacts.PowerFastAddress));
    }

    [Fact]
    public async Task Bz2EmRestore_PutsTheCapturedFastValueBack()
    {
        FakeWmiTransport wmi = new();
        wmi.SetData(ClawHardwareFacts.PowerFastAddress, 35);
        ClawPowerCapability power = new(wmi, ClawModels.A8Bz2Em);
        var original = await power.ReadAsync(CancellationToken.None);
        _ = await power.ApplyBoostAsync(
            Command(CapabilityIds.PowerBoost, null, CapabilityValue.Integer(20)), 20, CancellationToken.None);

        Assert.True(await power.RestoreAsync(original, CancellationToken.None));

        Assert.Equal(35, original.FastWatts);
        Assert.Equal(35, wmi.ReadData(ClawHardwareFacts.PowerFastAddress));
    }

    [Fact]
    public async Task Bz2EmFastRead_FailureLeavesTheValueUnknown()
    {
        FakeWmiTransport wmi = new();
        ClawPowerCapability power = new(wmi, ClawModels.A8Bz2Em);

        var pair = await power.ReadAsync(CancellationToken.None);

        Assert.Null(pair.FastWatts);
    }

    [Theory]
    [InlineData("ec:1T52EMS1.109;msi-acpi:8.0", "ec:1T52EMS1.109;msi-acpi:8.0", "Restore")]
    [InlineData("ec:1T52EMS1.109;msi-acpi:8.0", "ec:1T52EMS1.110;msi-acpi:8.0", "Discard")]
    [InlineData("ec:unknown;msi-acpi:8.0", "ec:unknown;msi-acpi:8.0", "Discard")]
    [InlineData("ec:1T52EMS1.109;msi-acpi:8.0", null, "ReportOnly")]
    public void Decide_RestoresOnlyOnTheSameKnownEc(string bound, string? current, string action)
    {
        Assert.Equal(
            Enum.Parse<ClawReconciliationAction>(action),
            ClawRecoveryJournal.Decide(PowerEntry(bound, ClawRecoveryStatus.Pending), current));
    }

    [Fact]
    public void Decide_AFailedRestoreStillBlocksAcrossAnEcChange()
    {
        Assert.Equal(
            ClawReconciliationAction.Block,
            ClawRecoveryJournal.Decide(
                PowerEntry("ec:1T52EMS1.109;msi-acpi:8.0", ClawRecoveryStatus.RestoreFailed),
                "ec:1T52EMS1.110;msi-acpi:8.0"));
    }

    [Fact]
    public async Task StartAsync_EntryFromAnotherEcIsDroppedAndPowerStaysAvailable()
    {
        using TemporaryDirectory state = new();
        await using (var journal = await ClawRecoveryJournal.OpenAsync(state.Root, CancellationToken.None))
        {
            _ = await journal.BeginAsync(ServiceIds.Power, CapabilityIds.PowerSustained,
                "ec:1T52EMS1.108;msi-acpi:8.0", ClawRecoveryValues.Power(new PowerPair(20, 30, 0xC1)),
                CancellationToken.None);
        }

        FakeWmiTransport wmi = new();
        await using ClawPlugin plugin = new(Services(new FakeIdentityReader(), wmi));
        TestPluginHostAdapter host = new(CycleGeneration);

        _ = await plugin.StartAsync(Context(host, state.Root, ClawModels.Claw8A2Vm), CancellationToken.None);

        Assert.Equal(30, wmi.ReadData(ClawHardwareFacts.PowerSustainedAddress));
        Assert.Contains(host.CapabilityStates,
            capability => capability is { CapabilityId: CapabilityIds.PowerSustained, Available: true });
        await using var reopened = await ClawRecoveryJournal.OpenAsync(state.Root, CancellationToken.None);
        Assert.Empty(reopened.OutstandingEntries);
    }

    [Theory]
    [InlineData("ec:1T52EMS1.109;msi-acpi:8.0", true)]
    [InlineData("ec:unknown;msi-acpi:1.0", true)]
    [InlineData("mcu", false)]
    [InlineData("msi-acpi:8.0", false)]
    public void IsWmi_AcceptsAnyEcBinding(string identity, bool expected)
    {
        Assert.Equal(expected, ClawFirmwareIdentities.IsWmi(identity));
    }

    [Fact]
    public async Task OpenAsync_AdoptsTheJournalLeftUnderTheRetiredPackageId()
    {
        using TemporaryDirectory root = new();
        var retired = Path.Combine(root.Root, ClawHardwareFacts.RetiredPackageId);
        var current = Path.Combine(root.Root, ClawHardwareFacts.PackageId);
        await using (var old = await ClawRecoveryJournal.OpenAsync(retired, CancellationToken.None))
        {
            _ = await old.BeginAsync(ServiceIds.Power, CapabilityIds.PowerSustained,
                "ec:1T52EMS1.109;msi-acpi:8.0", ClawRecoveryValues.Power(new PowerPair(30, 37, 0xC1)),
                CancellationToken.None);
        }

        await using var journal = await ClawRecoveryJournal.OpenAsync(current, CancellationToken.None);

        Assert.Equal(ServiceIds.Power, Assert.Single(journal.OutstandingEntries).ServiceId);
        Assert.False(File.Exists(Path.Combine(retired, "temporary-state.v1.json")));
    }

    [Theory]
    [InlineData(0, CanonicalButtons.X)]
    [InlineData(1, CanonicalButtons.A)]
    [InlineData(2, CanonicalButtons.B)]
    [InlineData(3, CanonicalButtons.Y)]
    [InlineData(4, CanonicalButtons.LeftShoulder)]
    [InlineData(5, CanonicalButtons.RightShoulder)]
    [InlineData(6, CanonicalButtons.None)]
    [InlineData(8, CanonicalButtons.View)]
    [InlineData(9, CanonicalButtons.Menu)]
    [InlineData(10, CanonicalButtons.LeftStick)]
    [InlineData(11, CanonicalButtons.RightStick)]
    [InlineData(15, CanonicalButtons.RearPaddle2)]
    [InlineData(16, CanonicalButtons.RearPaddle1)]
    public void DescriptorButtons_FollowHcsDirectInputIndices(int index, CanonicalButtons expected)
    {
        Assert.Equal(expected, HidDescriptorGamepad.Button(index));
    }

    [Fact]
    public void DescriptorButtons_AgreeWithTheMeasuredReportLayout()
    {
        // The measured decoder's bits are DirectInput's button indices in order from byte 5 bit 4, so
        // both decoders must name the same button for every index they share.
        for (var index = 0; index < 20; index++)
        {
            if (index is 6 or 7)
            {
                continue;
            }

            var report = new byte[64];
            report[0] = 0x01;
            report[1] = report[2] = report[3] = report[4] = 0x80;
            report[5] = 0x08;
            var bit = index + 4;
            report[5 + bit / 8] |= (byte)(1 << (bit % 8));
            var measured = ClawControllerCodec.Decode(report, 1, 1, DateTimeOffset.UtcNow).Buttons;
            Assert.Equal(HidDescriptorGamepad.Button(index), measured);
        }
    }

    private static HapticOutputFrame Frame(float low, float high)
    {
        return new HapticOutputFrame
        {
            TargetGeneration = 1,
            LowFrequency = low,
            HighFrequency = high,
            Timestamp = DateTimeOffset.UtcNow
        };
    }

    private static async Task<ControllerService> AcquireControllerAsync(
        FakeControllerSource source,
        TestPluginHostAdapter host,
        ClawRecoveryJournal journal,
        ClawModel model)
    {
        ControllerService controller = new(
            new FakeMcuTransport(),
            source,
            new MotionService(new FakeMotionSource(), model),
            host,
            journal,
            model)
        {
            Enabled = true
        };
        _ = await controller.AcquireAsync(
            new ClawCycleContext(CycleGeneration, DateTimeOffset.UtcNow.AddSeconds(10),
                FakeIdentityReader.CreateState() with { Model = model }),
            CancellationToken.None);
        return controller;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private static ControllerTopology DirectInputTopology()
    {
        return new ControllerTopology(
            ClawControllerMode.DirectInput,
            ClawHardwareFacts.DirectInputProductId,
            "PCIROOT(0)#USBROOT(0)#USB(2)",
            []);
    }

    private static ClawRecoveryEntry PowerEntry(string firmware, ClawRecoveryStatus status)
    {
        return new ClawRecoveryEntry
        {
            ServiceId = ServiceIds.Power,
            CapabilityId = CapabilityIds.PowerSustained,
            FirmwareIdentity = firmware,
            OriginalState = ClawRecoveryValues.Power(new PowerPair(30, 37, 0xC1)),
            Status = status
        };
    }

    private static PluginStartContext Context(IPluginHostAdapter host, string stateDirectory, ClawModel model)
    {
        return new PluginStartContext
        {
            Host = host,
            CycleGeneration = host.CycleGeneration,
            DeviceDefinitionId = model.DefinitionId,
            StateDirectory = stateDirectory,
            ControllerManagementEnabled = false
        };
    }

    private static ClawHardwareServices Services(
        FakeIdentityReader identity,
        FakeWmiTransport? wmi = null,
        FakeMcuTransport? mcu = null)
    {
        return new ClawHardwareServices(
            identity,
            wmi ?? new FakeWmiTransport(),
            new FakeOemEventSource(),
            mcu ?? new FakeMcuTransport(),
            new FakeControllerSource(),
            new FakeMotionSource(),
            new FakeChordSuppressor(),
            new ClawOemButtonLatch());
    }
}
