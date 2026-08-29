using System.Buffers.Binary;
using WSGM.Device.Msi.Claw8A2Vm;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Identity;
using WSGM.Device.Sdk.Input;
using WSGM.Device.Sdk.Plugin;
using WSGM.Device.Sdk.Testing;

namespace WSGM.Device.Tests;

public sealed class ClawPluginTests
{
    private const long CycleGeneration = 7;

    [Fact]
    public async Task DetectAsync_ExactBaseboardAndSku_MatchesWithoutMarketingName()
    {
        await using Claw8A2VmPlugin plugin = new(CreateServices());

        PluginDetectionResult result = await plugin.DetectAsync(
            new PluginDetectionContext
            {
                Identity = ExactIdentity() with { SystemProduct = "localized marketing name" },
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
        DeviceIdentitySnapshot identity = changedSignal switch
        {
            "manufacturer" => ExactIdentity() with { SystemManufacturer = "Other vendor" },
            "baseboard" => ExactIdentity() with { BaseboardProduct = "MS-1T42" },
            "sku" => ExactIdentity() with { SystemSku = "1T42.1" },
            _ => throw new ArgumentOutOfRangeException(nameof(changedSignal)),
        };
        await using Claw8A2VmPlugin plugin = new(CreateServices());

        PluginDetectionResult result = await plugin.DetectAsync(
            new PluginDetectionContext { Identity = identity },
            CancellationToken.None);

        Assert.False(result.Matched);
        Assert.Null(result.DeviceDefinitionId);
        Assert.Equal(CapabilityReasonCode.Unsupported, result.Reason?.Code);
    }

    [Fact]
    public void Decode_DirectInputReport_MapsMeasuredRearPaddlesAndDiagonalHat()
    {
        byte[] report = new byte[64];
        report[0] = 0x01;
        report[1] = report[2] = report[3] = report[4] = 0x80;
        report[5] = 0x01;
        report[7] = 0x18;

        CanonicalControllerSample sample = ClawControllerCodec.Decode(
            report,
            1,
            CycleGeneration,
            DateTimeOffset.UnixEpoch);

        Assert.True(sample.Buttons.HasFlag(CanonicalButtons.RearPaddle1));
        Assert.True(sample.Buttons.HasFlag(CanonicalButtons.RearPaddle2));
        Assert.True(sample.Buttons.HasFlag(CanonicalButtons.DPadUp));
        Assert.True(sample.Buttons.HasFlag(CanonicalButtons.DPadRight));
    }

    [Fact]
    public void Encode_Lighting_ReplicatesThreeLogicalZonesAcrossNineProtocolIndices()
    {
        byte[] payload = ClawA2VmLightingCapability.Encode(new LightingState(
            60,
            0x112233,
            0x445566,
            0x778899));

        Assert.Equal(32, payload.Length);
        Assert.Equal([0x11, 0x22, 0x33], payload[5..8]);
        Assert.Equal([0x11, 0x22, 0x33], payload[14..17]);
        Assert.Equal([0x44, 0x55, 0x66], payload[17..20]);
        Assert.Equal([0x44, 0x55, 0x66], payload[26..29]);
        Assert.Equal([0x77, 0x88, 0x99], payload[29..32]);
    }

    [Fact]
    public void Observe_FirmwareOrphanGUp_SuppressesButRealAndModifiedChordsPass()
    {
        FirmwareChordStateMachine firmware = new();
        _ = firmware.Observe(NativeKeyboard.VK_LWIN, keyDown: true, injected: false);
        ChordDecision orphan = firmware.Observe(NativeKeyboard.VK_G, keyDown: false, injected: false);

        Assert.True(orphan.Suppress);
        Assert.True(orphan.ReleaseLeftWindows);
        firmware.CommitSyntheticReleases(leftAccepted: true, rightAccepted: false);
        Assert.True(firmware.Observe(NativeKeyboard.VK_LWIN, keyDown: false, injected: false).Suppress);

        FirmwareChordStateMachine physical = new();
        _ = physical.Observe(NativeKeyboard.VK_LWIN, keyDown: true, injected: false);
        _ = physical.Observe(NativeKeyboard.VK_G, keyDown: true, injected: false);
        Assert.False(physical.Observe(NativeKeyboard.VK_G, keyDown: false, injected: false).Suppress);

        FirmwareChordStateMachine modified = new();
        _ = modified.Observe(NativeKeyboard.VK_CONTROL, keyDown: true, injected: false);
        _ = modified.Observe(NativeKeyboard.VK_LWIN, keyDown: true, injected: false);
        Assert.False(modified.Observe(NativeKeyboard.VK_G, keyDown: false, injected: false).Suppress);
    }

    [Fact]
    public async Task ApplyCurveAsync_UsesMeasuredSixOffsetsAndPreservesUnknownBytes()
    {
        FakeWmiTransport wmi = new();
        ClawA2VmFanCapability fan = new(wmi);
        CapabilityCommand command = Command(
            CapabilityIds.FanCurve,
            CapabilityInstances.Left,
            new CapabilityValue
            {
                Kind = CapabilityValueKind.Curve,
                CurveValue =
                [
                    new CurvePoint(0, 0),
                    new CurvePoint(50, 40),
                    new CurvePoint(60, 50),
                    new CurvePoint(70, 60),
                    new CurvePoint(80, 70),
                    new CurvePoint(90, 80),
                ],
            });

        CapabilityCommandResult result = await fan.ApplyCurveAsync(
            command,
            1,
            command.RequestedValue!.CurveValue,
            CancellationToken.None);

        Assert.Equal(CommandOutcome.AppliedVerified, result.Outcome);
        byte[] dutyWrite = Assert.Single(wmi.Writes, write => write.Method == "Set_Fan").Package;
        byte[] temperatureWrite = Assert.Single(
            wmi.Writes,
            write => write.Method == "Set_Temperature").Package;
        Assert.Equal([0, 40, 50, 60, 70, 80], dutyWrite[2..8]);
        Assert.Equal(0xA1, dutyWrite[1]);
        Assert.Equal(0xA8, dutyWrite[8]);
        Assert.Equal(0, temperatureWrite[1]);
        Assert.Equal([50, 60, 70, 80, 90], temperatureWrite[4..9]);
        Assert.Equal(0xB2, temperatureWrite[2]);
        Assert.Equal(0xB3, temperatureWrite[3]);
    }

    [Fact]
    public async Task StartAsync_FakeHardware_PublishesDirectCapabilityAndOemSurfaces()
    {
        using TemporaryDirectory state = new();
        FakeOemEventSource oem = new();
        await using Claw8A2VmPlugin plugin = new(CreateServices(oemEvents: oem));
        TestPluginHostAdapter host = new(CycleGeneration);

        PluginStartResult result = await plugin.StartAsync(
            StartContext(host, state.Root),
            CancellationToken.None);
        await oem.EmitAsync(0x2A, DateTimeOffset.UnixEpoch);

        Assert.Equal(PluginOperationalState.Degraded, result.State);
        CapabilityDescriptorSet descriptors = Assert.Single(host.DescriptorSets);
        Assert.Equal(CycleGeneration, descriptors.CycleGeneration);
        Assert.Contains(descriptors.Descriptors, descriptor =>
            descriptor.CapabilityId == CapabilityIds.PowerSustained);
        Assert.Contains(descriptors.Descriptors, descriptor =>
            descriptor.CapabilityId == CapabilityIds.LightingColor
            && descriptor.InstanceId == CapabilityInstances.Buttons);
        Assert.Equal(descriptors.Descriptors.Count, host.CapabilityStates.Count);
        Assert.Contains(host.CapabilityStates, capability =>
            capability.CapabilityId == CapabilityIds.PowerSustained
            && capability.Available
            && capability.ObservedValue?.IntegerValue == 30);
        Assert.Contains(host.CapabilityStates, capability =>
            capability.CapabilityId == CapabilityIds.Controller
            && !capability.Available);
        Assert.Equal(4, Assert.Single(host.OemControlSets).Count);
        OemControlEvent controlEvent = Assert.Single(host.OemEvents);
        Assert.Equal("oem2", controlEvent.ControlId);
        Assert.Equal(OemPressKind.Long, controlEvent.Press);
        Assert.Equal(CycleGeneration, controlEvent.SourceGeneration);
    }

    [Fact]
    public async Task ExecuteCommandAsync_PowerWrite_ReadsBackAndStopRestoresCompactJournal()
    {
        using TemporaryDirectory state = new();
        FakeWmiTransport wmi = new();
        await using Claw8A2VmPlugin plugin = new(CreateServices(wmi));
        TestPluginHostAdapter host = new(CycleGeneration);
        _ = await plugin.StartAsync(StartContext(host, state.Root), CancellationToken.None);
        CapabilityCommand command = Command(
            CapabilityIds.PowerSustained,
            instanceId: null,
            new CapabilityValue
            {
                Kind = CapabilityValueKind.Integer,
                IntegerValue = 25,
            });

        CapabilityCommandResult result = await plugin.ExecuteCommandAsync(command, CancellationToken.None);

        Assert.Equal(CommandOutcome.AppliedVerified, result.Outcome);
        Assert.Equal(25, result.ReadbackValue?.IntegerValue);
        Assert.Equal(25, wmi.ReadData(ClawHardwareFacts.PowerSustainedAddress));
        await using (ClawRecoveryJournal pending = await ClawRecoveryJournal.OpenAsync(
            state.Root,
            CancellationToken.None))
        {
            ClawRecoveryEntry entry = Assert.Single(pending.OutstandingEntries);
            Assert.Equal(ServiceIds.Power, entry.ServiceId);
            Assert.Equal(ClawRecoveryStatus.Pending, entry.Status);
            Assert.True(ClawRecoveryValues.TryPower(entry.OriginalState, out PowerPair? original));
            Assert.Equal(new PowerPair(30, 37, 0xC1), original);
        }

        PluginStopResult stop = await plugin.StopAsync(
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
        await using ClawRecoveryJournal completed = await ClawRecoveryJournal.OpenAsync(
            state.Root,
            CancellationToken.None);
        Assert.Empty(completed.OutstandingEntries);
    }

    [Fact]
    public async Task BeginAsync_UnfinishedWrite_RetainsFirstOriginalAcrossReopen()
    {
        using TemporaryDirectory state = new();
        await using (ClawRecoveryJournal journal = await ClawRecoveryJournal.OpenAsync(
            state.Root,
            CancellationToken.None))
        {
            ClawRecoveryOperation operation = await journal.BeginAsync(
                ServiceIds.Power,
                CapabilityIds.PowerSustained,
                ClawFirmwareIdentities.Wmi,
                ClawRecoveryValues.Power(new PowerPair(30, 37, 0xC1)),
                CancellationToken.None);

            Assert.True(operation.Opened);
            Assert.Single(journal.OutstandingEntries);
        }

        await using ClawRecoveryJournal reopened = await ClawRecoveryJournal.OpenAsync(
            state.Root,
            CancellationToken.None);
        ClawRecoveryEntry entry = Assert.Single(reopened.OutstandingEntries);
        Assert.True(ClawRecoveryValues.TryPower(entry.OriginalState, out PowerPair? original));
        Assert.Equal(new PowerPair(30, 37, 0xC1), original);

        ClawRecoveryOperation existing = await reopened.BeginAsync(
            ServiceIds.Power,
            CapabilityIds.PowerSustained,
            ClawFirmwareIdentities.Wmi,
            ClawRecoveryValues.Power(new PowerPair(25, 37, 0xC1)),
            CancellationToken.None);
        Assert.False(existing.Opened);
        Assert.True(ClawRecoveryValues.TryPower(existing.Entry.OriginalState, out PowerPair? retained));
        Assert.Equal(new PowerPair(30, 37, 0xC1), retained);
    }

    [Fact]
    public async Task StartAsync_OutstandingCompactPowerEntry_RestoresBeforeNewOwnership()
    {
        using TemporaryDirectory state = new();
        await using (ClawRecoveryJournal journal = await ClawRecoveryJournal.OpenAsync(
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
            capability.CapabilityId == CapabilityIds.PowerSustained
            && capability.Available
            && capability.ObservedValue?.IntegerValue == 30);
        await using ClawRecoveryJournal reconciled = await ClawRecoveryJournal.OpenAsync(
            state.Root,
            CancellationToken.None);
        Assert.Empty(reconciled.OutstandingEntries);
    }

    private static CapabilityCommand Command(
        string capabilityId,
        string? instanceId,
        CapabilityValue value) => new()
        {
            CommandId = Guid.NewGuid(),
            CapabilityId = capabilityId,
            InstanceId = instanceId,
            RequestedValue = value,
            ExpectedDescriptorGeneration = 1,
            ExpectedCycleGeneration = CycleGeneration,
            Deadline = DateTimeOffset.UtcNow.AddMinutes(1),
        };

    private static PluginStartContext StartContext(TestPluginHostAdapter host, string stateDirectory) => new()
    {
        Host = host,
        CycleGeneration = CycleGeneration,
        DeviceDefinitionId = ClawHardwareFacts.DeviceDefinitionId,
        StateDirectory = stateDirectory,
        ControllerManagementEnabled = false,
    };

    private static ClawHardwareServices CreateServices(
        FakeWmiTransport? wmi = null,
        FakeOemEventSource? oemEvents = null) => new(
            new FakeIdentityReader(),
            wmi ?? new FakeWmiTransport(),
            oemEvents ?? new FakeOemEventSource(),
            new FakeMcuTransport(),
            new FakeControllerSource(),
            new FakeMotionSource(),
            new FakeChordSuppressor());

    private static DeviceIdentitySnapshot ExactIdentity() => new()
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
                DeviceRelease = ClawHardwareFacts.McuFirmware,
            },
        ],
    };
}

internal sealed class FakeIdentityReader : IClawIdentityReader
{
    public ValueTask<ClawIdentityState> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new ClawIdentityState
        {
            Snapshot = new DeviceIdentitySnapshot
            {
                SystemManufacturer = ClawHardwareFacts.Manufacturer,
                BaseboardProduct = ClawHardwareFacts.BoardProduct,
                SystemSku = ClawHardwareFacts.SystemSku,
                EcFirmwareVersion = ClawHardwareFacts.EcFirmware,
                UsbEndpoints =
                [
                    new UsbEndpointObservation
                    {
                        VendorId = ClawHardwareFacts.UsbVendorId,
                        ProductId = ClawHardwareFacts.XInputProductId,
                        DeviceRelease = ClawHardwareFacts.McuFirmware,
                    },
                ],
            },
            ExactMachineMatch = true,
            WmiFirmwareVerified = true,
            McuFirmwareVerified = true,
            OnAcPower = true,
        });
    }
}

internal sealed class FakeWmiTransport : IMsiWmiTransport
{
    private readonly Dictionary<(string Method, byte Selector), byte[]> _responses = [];

    public FakeWmiTransport()
    {
        SetData(ClawHardwareFacts.PowerSustainedAddress, 30);
        SetData(ClawHardwareFacts.PowerBoostAddress, 37);
        SetData(ClawHardwareFacts.ScenarioAddress, 0xC1);
        SetData(ClawHardwareFacts.FanCustomAddress, 0);
        SetData(ClawHardwareFacts.FanFullSpeedAddress, 2);
        _responses[("Get_Fan", 0)] = Response(0, 0xC7, 0, 0xCF);
        _responses[("Get_Temperature", 0)] = Response(52);
        _responses[("Get_Fan", 1)] = Table(0xA1, 0, 40, 49, 58, 67, 75, 0xA8);
        _responses[("Get_Fan", 2)] = Table(0x91, 0, 40, 49, 58, 67, 75, 0x98);
        _responses[("Get_Temperature", 1)] = Table(0, 0xB2, 0xB3, 50, 60, 70, 80, 88);
        _responses[("Get_Temperature", 2)] = Table(0, 0xC2, 0xC3, 50, 60, 70, 80, 88);
    }

    public List<(string Method, byte[] Package)> Writes { get; } = [];

    public int ReadData(byte address) =>
        BinaryPrimitives.ReadInt32LittleEndian(_responses[("Get_Data", address)].AsSpan(1, sizeof(int)));

    public void SetData(byte address, int value)
    {
        _responses[("Get_Data", address)] = Data(value);
    }

    public ValueTask<bool> IsProviderAvailableAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(true);
    }

    public ValueTask<byte[]> InvokeGetterAsync(
        string methodName,
        byte selector,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult((byte[])[.. _responses[(methodName, selector)]]);
    }

    public ValueTask InvokeSetterAsync(
        string methodName,
        byte[] package,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Writes.Add((methodName, [.. package]));
        byte selector = package[0];
        if (methodName == "Set_Data")
        {
            _responses[("Get_Data", selector)] = Response(package[1], package[2], package[3], package[4]);
        }
        else
        {
            string getter = methodName == "Set_Fan" ? "Get_Fan" : "Get_Temperature";
            byte[] response = [.. package];
            response[0] = 1;
            _responses[(getter, selector)] = response;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static byte[] Data(int value)
    {
        byte[] response = new byte[32];
        response[0] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(response.AsSpan(1, sizeof(int)), value);
        return response;
    }

    private static byte[] Response(params byte[] payload)
    {
        byte[] response = new byte[32];
        response[0] = 1;
        payload.CopyTo(response, 1);
        return response;
    }

    private static byte[] Table(params byte[] payload) => Response(payload);
}

internal sealed class FakeOemEventSource : IMsiOemEventSource
{
    private Func<byte, DateTimeOffset, ValueTask>? _callback;

    public ValueTask<bool> StartAsync(
        Func<byte, DateTimeOffset, ValueTask> callback,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _callback = callback;
        return ValueTask.FromResult(true);
    }

    public ValueTask EmitAsync(byte code, DateTimeOffset timestamp) =>
        _callback?.Invoke(code, timestamp) ?? ValueTask.CompletedTask;

    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _callback = null;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeMcuTransport : IClawMcuTransport
{
    private byte[] _profile = ClawA2VmLightingCapability.Encode(new LightingState(50, 0, 0, 0));

    public ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(true);
    }

    public ValueTask<byte[]> ReadProfileAsync(
        ushort address,
        byte length,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult((byte[])[.. _profile]);
    }

    public ValueTask WriteProfileAsync(
        ushort address,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _profile = payload.ToArray();
        return ValueTask.CompletedTask;
    }

    public ValueTask<ClawControllerMode> ReadModeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ClawControllerMode.XInput);
    }

    public ValueTask<ControllerTopology> SwitchModeAsync(
        ClawControllerMode mode,
        string physicalLocation,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new ControllerTopology(
            mode,
            mode == ClawControllerMode.XInput
                ? ClawHardwareFacts.XInputProductId
                : ClawHardwareFacts.DirectInputProductId,
            physicalLocation,
            []));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeControllerSource : IClawControllerSource
{
    public ValueTask<ControllerTopology?> DiscoverAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<ControllerTopology?>(new ControllerTopology(
            ClawControllerMode.XInput,
            ClawHardwareFacts.XInputProductId,
            "PCIROOT(0)#USBROOT(0)#USB(2)",
            []));
    }

    public ValueTask StartAsync(
        long cycleGeneration,
        Func<CanonicalControllerSample, ValueTask> publish,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public ValueTask WriteRumbleAsync(byte weak, byte strong, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeMotionSource : IClawMotionSource
{
    public ValueTask<bool> StartAsync(
        Func<MotionSample, ValueTask> publish,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(true);
    }

    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeChordSuppressor : IFirmwareChordSuppressor
{
    public ValueTask<bool> StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(true);
    }

    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
