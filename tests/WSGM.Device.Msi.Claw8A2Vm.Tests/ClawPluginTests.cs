using WSGM.Device.Contracts.Capabilities;
using WSGM.Device.Contracts.Identity;
using WSGM.Device.Contracts.Input;
using WSGM.Device.Contracts.Lifecycle;
using WSGM.Device.Sdk.Plugin;
using WSGM.Device.Sdk.Testing;
using Xunit;

namespace WSGM.Device.Msi.Claw8A2Vm.Tests;

public sealed class ClawPluginTests
{
    [Fact]
    public async Task DetectAsync_ExactBaseboardAndSku_MatchesWithoutMarketingName()
    {
        await using Claw8A2VmPlugin plugin = new(CreateServices());

        PluginDetectionResult result = await plugin.DetectAsync(
            new PluginDetectionContext
            {
                Identity = ExactIdentity() with { SystemProduct = "localized marketing name" },
                HostGeneration = 1,
            },
            CancellationToken.None);

        Assert.True(result.Matched);
        Assert.Equal(ClawHardwareFacts.DeviceDefinitionId, result.DeviceDefinitionId);
    }

    [Fact]
    public async Task DetectAsync_Claw7A2VmBoard_FailsClosed()
    {
        await using Claw8A2VmPlugin plugin = new(CreateServices());

        PluginDetectionResult result = await plugin.DetectAsync(
            new PluginDetectionContext
            {
                Identity = ExactIdentity() with { BaseboardProduct = "MS-1T42" },
                HostGeneration = 1,
            },
            CancellationToken.None);

        Assert.False(result.Matched);
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
            2,
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
        byte[] dutyWrite = wmi.Writes.Single(write => write.Method == "Set_Fan").Package;
        byte[] temperatureWrite = wmi.Writes.Single(write => write.Method == "Set_Temperature").Package;
        Assert.Equal([0, 40, 50, 60, 70, 80], dutyWrite[2..8]);
        Assert.Equal(0xA1, dutyWrite[1]);
        Assert.Equal(0xA8, dutyWrite[8]);
        Assert.Equal(0, temperatureWrite[1]);
        Assert.Equal([50, 60, 70, 80, 90], temperatureWrite[4..9]);
        Assert.Equal(0xB2, temperatureWrite[2]);
        Assert.Equal(0xB3, temperatureWrite[3]);
    }

    [Fact]
    public async Task ActivateAsync_FakeHardware_PublishesIndependentCapabilityAndOemSurfaces()
    {
        ClawHardwareServices services = CreateServices();
        await using Claw8A2VmPlugin plugin = new(services);
        TestPluginHostAdapter host = new(1, 7);

        await plugin.ActivateAsync(
            new PluginActivationContext
            {
                Host = host,
                HostGeneration = 1,
                DeviceGeneration = 7,
                DeviceDefinitionId = ClawHardwareFacts.DeviceDefinitionId,
                ControllerManagementEnabled = false,
            },
            CancellationToken.None);

        CapabilityDescriptorSet descriptors = Assert.Single(host.DescriptorSets);
        Assert.Contains(descriptors.Descriptors, descriptor =>
            descriptor.CapabilityId == CapabilityIds.PowerSustained);
        Assert.Contains(descriptors.Descriptors, descriptor =>
            descriptor.CapabilityId == CapabilityIds.LightingColor
            && descriptor.InstanceId == CapabilityInstances.Buttons);
        Assert.Equal(4, Assert.Single(host.OemControlSets).Count);
        Assert.Contains(host.ResourceStates, state =>
            state.ResourceId == ResourceIds.Controller && state.State == WSGM.Device.Contracts.Lifecycle.ResourceState.Passive);
        Assert.Contains(host.ResourceStates, state =>
            state.ResourceId == ResourceIds.Power && state.State == WSGM.Device.Contracts.Lifecycle.ResourceState.Owned);
    }

    [Fact]
    public async Task ExecuteCommandAsync_VolatilePowerWrite_JournalsBeforeWriteAndClosesOnRelease()
    {
        await using Claw8A2VmPlugin plugin = new(CreateServices());
        TestPluginHostAdapter host = new(1, 7);
        await plugin.ActivateAsync(
            Activation(host),
            CancellationToken.None);
        CapabilityCommand command = Command(
            CapabilityIds.PowerSustained,
            null,
            new CapabilityValue
            {
                Kind = CapabilityValueKind.Integer,
                IntegerValue = 25,
            });

        CapabilityCommandResult result = await plugin.ExecuteCommandAsync(command, CancellationToken.None);

        Assert.Equal(CommandOutcome.AppliedVerified, result.Outcome);
        RecoveryJournalEntry[] operation = host.JournalEntries
            .Where(entry => entry.CapabilityId == CapabilityIds.PowerSustained)
            .ToArray();
        Assert.Equal(
            [JournalEntryStatus.Planned, JournalEntryStatus.Applying, JournalEntryStatus.AppliedVerified],
            operation.Select(entry => entry.Status));
        Assert.Single(operation.Select(entry => entry.Sequence).Distinct());

        await plugin.DeactivateAsync(
            new PluginDeactivationContext(
                PluginDeactivationReason.IntegrationDisabled,
                DateTimeOffset.UtcNow.AddSeconds(10)),
            CancellationToken.None);

        Assert.Equal(
            JournalEntryStatus.RestoredVerified,
            host.JournalEntries.Last(entry => entry.Sequence == operation[0].Sequence).Status);
    }

    [Fact]
    public async Task ActivateAsync_OutstandingPowerEntry_RestoresBeforeNewOwnership()
    {
        await using Claw8A2VmPlugin plugin = new(CreateServices());
        TestPluginHostAdapter host = new(2, 8);
        RecoveryJournalEntry outstanding = new()
        {
            Sequence = DateTimeOffset.UtcNow.UtcTicks - 1,
            PackageId = ClawHardwareFacts.PackageId,
            DeviceId = ClawHardwareFacts.DeviceDefinitionId,
            HostGeneration = 1,
            DeviceGeneration = 7,
            ResourceId = ResourceIds.Power,
            CapabilityId = CapabilityIds.PowerSustained,
            FirmwareIdentity = ClawFirmwareIdentities.Wmi,
            OriginalValue = ClawRecoveryValues.Power(new PowerPair(30, 37, 0xC1)),
            PlannedValue = new CapabilityValue
            {
                Kind = CapabilityValueKind.Integer,
                IntegerValue = 25,
            },
            AppliedValue = new CapabilityValue
            {
                Kind = CapabilityValueKind.Integer,
                IntegerValue = 25,
            },
            Status = JournalEntryStatus.AppliedVerified,
            OpenedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        };

        await plugin.ActivateAsync(
            Activation(host) with
            {
                HostGeneration = 2,
                DeviceGeneration = 8,
                OutstandingJournalEntries = [outstanding],
            },
            CancellationToken.None);

        RecoveryJournalEntry restored = Assert.Single(host.JournalEntries);
        Assert.Equal(outstanding.Sequence, restored.Sequence);
        Assert.Equal(JournalEntryStatus.RestoredVerified, restored.Status);
    }

    private static CapabilityCommand Command(
        string capabilityId,
        string? instanceId,
        CapabilityValue value) => new()
        {
            CommandId = Guid.NewGuid(),
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            CapabilityId = capabilityId,
            InstanceId = instanceId,
            RequestedValue = value,
            ExpectedDescriptorGeneration = 1,
            ExpectedDeviceGeneration = 7,
            Deadline = DateTimeOffset.UtcNow.AddMinutes(1),
        };

    private static PluginActivationContext Activation(TestPluginHostAdapter host) => new()
    {
        Host = host,
        HostGeneration = host.HostGeneration,
        DeviceGeneration = host.DeviceGeneration,
        DeviceDefinitionId = ClawHardwareFacts.DeviceDefinitionId,
        ControllerManagementEnabled = false,
    };

    private static ClawHardwareServices CreateServices()
    {
        FakeWmiTransport wmi = new();
        FakeMcuTransport mcu = new();
        return new ClawHardwareServices(
            new FakeIdentityReader(),
            wmi,
            new FakeOemEventSource(),
            mcu,
            new FakeControllerSource(),
            new FakeMotionSource(),
            new FakeChordSuppressor());
    }

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
        _responses[("Get_Data", ClawHardwareFacts.PowerSustainedAddress)] = Data(30);
        _responses[("Get_Data", ClawHardwareFacts.PowerBoostAddress)] = Data(37);
        _responses[("Get_Data", ClawHardwareFacts.ScenarioAddress)] = Data(0xC1);
        _responses[("Get_Data", ClawHardwareFacts.FanCustomAddress)] = Data(0);
        _responses[("Get_Data", ClawHardwareFacts.FanFullSpeedAddress)] = Data(2);
        _responses[("Get_Fan", 0)] = Response(0, 0xC7, 0, 0xCF);
        _responses[("Get_Temperature", 0)] = Response(52);
        _responses[("Get_Fan", 1)] = Table(0xA1, 0, 40, 49, 58, 67, 75, 0xA8);
        _responses[("Get_Fan", 2)] = Table(0x91, 0, 40, 49, 58, 67, 75, 0x98);
        _responses[("Get_Temperature", 1)] = Table(0, 0xB2, 0xB3, 50, 60, 70, 80, 88);
        _responses[("Get_Temperature", 2)] = Table(0, 0xC2, 0xC3, 50, 60, 70, 80, 88);
    }

    public List<(string Method, byte[] Package)> Writes { get; } = [];

    public ValueTask<bool> IsProviderAvailableAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(true);

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
        BitConverter.GetBytes(value).CopyTo(response, 1);
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
    public ValueTask<bool> StartAsync(
        Func<byte, DateTimeOffset, ValueTask> callback,
        CancellationToken cancellationToken) => ValueTask.FromResult(true);

    public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeMcuTransport : IClawMcuTransport
{
    private byte[] _profile = ClawA2VmLightingCapability.Encode(new LightingState(50, 0, 0, 0));

    public ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken) => ValueTask.FromResult(true);

    public ValueTask<byte[]> ReadProfileAsync(
        ushort address,
        byte length,
        CancellationToken cancellationToken) => ValueTask.FromResult((byte[])[.. _profile]);

    public ValueTask WriteProfileAsync(
        ushort address,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        _profile = payload.ToArray();
        return ValueTask.CompletedTask;
    }

    public ValueTask<ClawControllerMode> ReadModeAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(ClawControllerMode.XInput);

    public ValueTask<ControllerTopology> SwitchModeAsync(
        ClawControllerMode mode,
        string physicalLocation,
        DateTimeOffset deadline,
        CancellationToken cancellationToken) => ValueTask.FromResult(new ControllerTopology(
            mode,
            mode == ClawControllerMode.XInput ? "1901" : "1902",
            physicalLocation,
            []));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeControllerSource : IClawControllerSource
{
    public ValueTask<ControllerTopology?> DiscoverAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult<ControllerTopology?>(new ControllerTopology(
            ClawControllerMode.XInput,
            "1901",
            "PCIROOT(0)#USBROOT(0)#USB(2)",
            []));

    public ValueTask StartAsync(
        long deviceGeneration,
        Func<CanonicalControllerSample, ValueTask> publish,
        CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask WriteRumbleAsync(byte weak, byte strong, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeMotionSource : IClawMotionSource
{
    public ValueTask<bool> StartAsync(
        Func<MotionSample, ValueTask> publish,
        CancellationToken cancellationToken) => ValueTask.FromResult(true);

    public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeChordSuppressor : IFirmwareChordSuppressor
{
    public ValueTask<bool> StartAsync(CancellationToken cancellationToken) => ValueTask.FromResult(true);

    public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
