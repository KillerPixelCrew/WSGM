using System.Text.Json;
using WSGM.DeviceLab.Core;
using WSGM.DeviceLab.Core.Capture;
using WSGM.DeviceLab.Core.Inventory;

namespace WSGM.DeviceLab.Tests;

/// <summary>Executable failure and determinism boundaries for read-only Stage 1 inventory.</summary>
public class StageOneInventoryTests
{
    [Fact]
    public void DisconnectedFixture_PreservesPresenceAndAccessInsteadOfDroppingEndpoints()
    {
        MachineInventory inventory = Load("disconnected.json");

        SerialEndpointInventory serial = Assert.Single(inventory.SerialEndpoints);
        SensorEndpointInventory sensor = Assert.Single(inventory.Sensors);
        Assert.False(serial.Present);
        Assert.Equal(InventoryAccess.Disconnected, serial.Access);
        Assert.Equal(InventoryAccess.Disconnected, sensor.Access);
    }

    [Fact]
    public void AccessDeniedFixture_KeepsEverySystemLaneDistinctFromAbsent()
    {
        MachineInventory inventory = Load("access-denied.json");

        Assert.Equal(InventoryAccess.AccessDenied, Assert.Single(inventory.InputBackends).Access);
        Assert.Equal(InventoryAccess.ExclusiveAccessDenied, Assert.Single(inventory.NativeBinaries).Access);
        Assert.Equal(InventoryAccess.AccessDenied, Assert.Single(inventory.Processes).Access);
        Assert.Equal(InventoryAccess.AccessDenied, Assert.Single(inventory.Services).Access);
        Assert.Equal(InventoryAccess.AccessDenied, Assert.Single(inventory.ScheduledTasks).Access);
        Assert.Equal(InventoryAccess.AccessDenied, Assert.Single(inventory.Providers).Access);
        Assert.True(Assert.Single(inventory.ResourceConflicts).Demonstrated);
    }

    [Fact]
    public void MultiSensorFixture_KeepsIndependentEndpointsAndCanonicalIntervals()
    {
        MachineInventory inventory = MachineInventoryNormalizer.Normalize(Load("multi-sensor.json"));

        Assert.Equal(2, inventory.Sensors.Count);
        Assert.All(inventory.Sensors, sensor =>
        {
            Assert.Equal(SensorApiKind.Controller, sensor.Api);
            Assert.Equal("controller:physical-a", sensor.AssociationId);
            Assert.Equal(new uint[] { 4, 8, 16 }, sensor.SupportedReportIntervalsMilliseconds.ToArray());
        });
    }

    [Fact]
    public void DetachableFixture_DoesNotCollapseIndependentPhysicalAssociations()
    {
        InputBackendInventory backend = Assert.Single(Load("detachable.json").InputBackends);

        Assert.Equal(InputBackendKind.RawHid, backend.Backend);
        Assert.Equal(InputBackendViewKind.PassiveCompatibility, backend.View);
        Assert.Equal(2, backend.Endpoints.Count);
        Assert.All(backend.Endpoints, endpoint => Assert.True(endpoint.Detachable));
        Assert.Equal(2, backend.Endpoints.Select(endpoint => endpoint.AssociationId).Distinct().Count());
    }

    [Fact]
    public void MalformedFixture_RejectsBadFramingAndLabelsBadDescriptor()
    {
        MachineInventory inventory = MachineInventoryNormalizer.Normalize(Load("malformed.json"));

        SerialEndpointInventory serial = Assert.Single(inventory.SerialEndpoints);
        Assert.Equal(InventoryAccess.Malformed, serial.Access);
        Assert.Equal((uint)9600, Assert.Single(serial.FramingCandidates).BaudRate);
        InputEndpointInventory hid = Assert.Single(Assert.Single(inventory.InputBackends).Endpoints);
        Assert.Equal(InventoryAccess.Malformed, hid.DescriptorAccess);
        Assert.Null(hid.ReportDescriptorSha256);
    }

    [Fact]
    public void TopologyChangeFixture_PreservesGenerationOrderAndPhysicalContinuation()
    {
        MachineInventory inventory = MachineInventoryNormalizer.Normalize(Load("topology-change.json"));

        Assert.Equal(
            new long[] { 1, 2, 3 },
            inventory.TopologyGenerations.Select(item => item.Generation).ToArray());
        Assert.Single(inventory.TopologyGenerations.Select(item => item.AssociationId).Distinct());
        Assert.Equal(TopologyChangeKind.Removal, inventory.TopologyGenerations[1].Change);
        Assert.Equal(TopologyChangeKind.Arrival, inventory.TopologyGenerations[2].Change);
        Assert.NotEqual(
            inventory.TopologyGenerations[1].InstanceId,
            inventory.TopologyGenerations[2].InstanceId);
    }

    [Fact]
    public void FixtureNormalization_IsByteDeterministic()
    {
        MachineInventory first = MachineInventoryNormalizer.Normalize(Load("topology-change.json"));
        MachineInventory restored = JsonSerializer.Deserialize(
            DeviceLabJson.Serialize(first),
            DeviceLabJsonContext.Default.MachineInventory)!;
        MachineInventory second = MachineInventoryNormalizer.Normalize(restored);

        Assert.Equal(DeviceLabJson.Serialize(first), DeviceLabJson.Serialize(second));
    }

    [Fact]
    public void Normalization_BoundsEachBackendBeforeItCanReachCaptureOutput()
    {
        MachineInventory inventory = EmptyInventory() with
        {
            InputBackends =
            [
                new InputBackendInventory
                {
                    Backend = InputBackendKind.RawInput,
                    Access = InventoryAccess.Available,
                    View = InputBackendViewKind.LiveApi,
                    RuntimeAvailable = true,
                    Endpoints = Enumerable.Range(0, InventoryLimits.MaximumEndpointsPerLane + 20)
                        .Select(index => new InputEndpointInventory
                        {
                            EndpointId = $"rawinput:{index:D4}",
                            Connected = true,
                        })
                        .ToArray(),
                },
            ],
        };

        MachineInventory normalized = MachineInventoryNormalizer.Normalize(inventory);

        Assert.Equal(
            InventoryLimits.MaximumEndpointsPerLane,
            Assert.Single(normalized.InputBackends).Endpoints.Count);
    }

    [Fact]
    public void ShareableInventory_ReplacesAllSessionIdentifiersWithCorrelatedTokens()
    {
        MachineInventory privateInventory = Load("access-denied.json") with
        {
            InputBackends =
            [
                new InputBackendInventory
                {
                    Backend = InputBackendKind.RawInput,
                    Access = InventoryAccess.Available,
                    View = InputBackendViewKind.LiveApi,
                    RuntimeAvailable = true,
                    Endpoints =
                    [
                        new InputEndpointInventory
                        {
                            EndpointId = @"\\?\HID#VID_1234&PID_5000#8&PRIVATE&0&0000#{GUID}",
                            InstanceId = @"HID\VID_1234&PID_5000\8&PRIVATE&0&0000",
                            Name = @"\\?\HID#VID_1234&PID_5000#8&PRIVATE&0&0000#{GUID}",
                            AssociationId = "PCIROOT(0)#USBROOT(0)#USB(2)",
                            Connected = true,
                        },
                    ],
                },
            ],
            TopologyGenerations =
            [
                new TopologyGenerationInventory
                {
                    Generation = 1,
                    Change = TopologyChangeKind.Baseline,
                    InstanceId = @"HID\VID_1234&PID_5000\8&PRIVATE&0&0000",
                    AssociationId = "PCIROOT(0)#USBROOT(0)#USB(2)",
                    Present = true,
                },
            ],
        };

        MachineInventory shareable = InventoryRedaction.ToShareable(privateInventory, out var removed);

        ProcessInventory process = Assert.Single(shareable.Processes);
        ServiceInventory service = Assert.Single(shareable.Services);
        InputEndpointInventory endpoint = Assert.Single(Assert.Single(shareable.InputBackends).Endpoints);
        Assert.Null(process.ProcessId);
        Assert.Null(service.ProcessId);
        Assert.Equal(process.SessionToken, service.ProcessToken);
        Assert.DoesNotContain("PRIVATE", endpoint.EndpointId, StringComparison.Ordinal);
        Assert.NotNull(endpoint.Name);
        Assert.DoesNotContain("PRIVATE", endpoint.Name!, StringComparison.Ordinal);
        Assert.Equal(endpoint.AssociationId, Assert.Single(shareable.TopologyGenerations).AssociationId);
        Assert.Contains(removed, item => item.Category == RedactionCategory.SessionIdentifier);
    }

    private static MachineInventory Load(string name)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Inventory", name);
        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize(json, DeviceLabJsonContext.Default.MachineInventory)
            ?? throw new InvalidDataException($"Fixture '{name}' could not be decoded.");
    }

    private static MachineInventory EmptyInventory() => new()
    {
        SchemaVersion = WindowsInventoryCollector.CurrentSchemaVersion,
        Firmware = new FirmwareInventory(),
        CapturedAt = new DateTimeOffset(2026, 8, 28, 0, 0, 0, TimeSpan.Zero),
    };
}
