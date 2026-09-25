using WSGM.DeviceLab.Application;
using WSGM.DeviceLab.Inventory;
using WSGM.DeviceLab.Knowledge;
using WSGM.DeviceLab.Probes;

namespace WSGM.DeviceLab.Tests.Knowledge;

public sealed class DeviceKnowledgeAssessorTests
{
    private static DeviceKnowledgeBase Knowledge => DeviceKnowledgeBase.Default;

    private static CompiledReadProbeFamily Claw => MsiClawReadProbes.Family;

    [Fact]
    public void EveryCompiledFamily_NamesACuratedRecordAndResolvableProbes()
    {
        Assert.All(BuiltInReadProbeRegistry.Families, family =>
        {
            var record = Knowledge.Records.Single(item => item.Id == family.KnowledgeRecordId);
            Assert.Equal(DeviceKnowledgeStatus.Curated, record.Status);
            Assert.NotEmpty(family.Probes);
            Assert.All(family.Probes, probe =>
            {
                Assert.Equal(family.FamilyId, probe.FamilyId);
                Assert.Empty(ReadProbeMetadataPolicy.Validate(probe));
                Assert.True(BuiltInReadProbeRegistry.TryResolve(probe.Id, probe.Version, out var profile));
                var descriptor = profile.Descriptor;
                Assert.Equal(probe.EndpointId, descriptor.EndpointId);
                Assert.Equal(probe.FamilyId, descriptor.FamilyId);
                Assert.Equal(probe.Family, descriptor.Family);
                Assert.Equal(probe.MaximumReadsPerSecond, descriptor.MaximumReadsPerSecond);
                Assert.Equal(probe.TimeoutMilliseconds, descriptor.TimeoutMilliseconds);
                Assert.Equal(probe.Repetitions, descriptor.Repetitions);
            });
        });
    }

    [Fact]
    public void Claw_ExactRecordOwnsExactlyFiveCompiledReadProbes()
    {
        var exact = DeviceKnowledgeAssessor.Assess(Knowledge, Claw, Inventory(), Claw.DeviceId);

        Assert.True(exact.ExactMatch, string.Join(Environment.NewLine, exact.Explanations));
        Assert.Equal("wsgm.claw-8-a2vm", exact.KnowledgeRecordId);
        Assert.Equal("ms-1t52", exact.DeviceId);
        Assert.Equal(5, Claw.Probes.Count);
        Assert.Equal(5, Claw.Probes.Select(probe => probe.Id).Distinct().Count());

        var mismatch = Inventory() with
        {
            Firmware = Inventory().Firmware with { BaseboardProduct = "NOT-MS-1T52" }
        };
        var refused = DeviceKnowledgeAssessor.Assess(Knowledge, Claw, mismatch, Claw.DeviceId);
        Assert.False(refused.ExactMatch);
        Assert.Contains(refused.Explanations, line => line.Contains("baseboard product mismatch",
            StringComparison.Ordinal));
    }

    [Fact]
    public void Claw_WrongLogicalIdSkuOrControllerIsRefused()
    {
        var inventory = Inventory();

        Assert.False(DeviceKnowledgeAssessor.Assess(Knowledge, Claw, inventory, "other").ExactMatch);
        Assert.False(DeviceKnowledgeAssessor.Assess(Knowledge, Claw, inventory with
        {
            Firmware = inventory.Firmware with { SystemSku = "1T52.9" }
        }, Claw.DeviceId).ExactMatch);
        Assert.False(DeviceKnowledgeAssessor.Assess(Knowledge, Claw, inventory with
        {
            UsbInterfaces = [inventory.UsbInterfaces[0] with { ProductId = "1903" }]
        }, Claw.DeviceId).ExactMatch);
        Assert.True(DeviceKnowledgeAssessor.Assess(Knowledge, Claw, inventory with
        {
            UsbInterfaces = [inventory.UsbInterfaces[0] with { ProductId = "1902", DeviceRelease = "0300" }]
        }, Claw.DeviceId).ExactMatch);
    }

    [Fact]
    public void Claw_UnavailableWmiNamespaceDoesNotSatisfyTheExactProviderGate()
    {
        var inventory = Inventory();
        var unavailable = inventory with
        {
            WmiClasses = [inventory.WmiClasses[0] with { Access = WmiAccess.NamespaceUnavailable }]
        };
        var accessDenied = inventory with
        {
            WmiClasses = [inventory.WmiClasses[0] with { Access = WmiAccess.AccessDenied }]
        };

        Assert.False(DeviceKnowledgeAssessor.Assess(Knowledge, Claw, unavailable, Claw.DeviceId).ExactMatch);
        Assert.True(DeviceKnowledgeAssessor.Assess(Knowledge, Claw, accessDenied, Claw.DeviceId).ExactMatch);
    }

    [Fact]
    public void LogicalDeviceId_NamesTheClawOnlyWhenItsRecordMatches()
    {
        Assert.Equal("ms-1t52",
            DeviceKnowledgeAssessor.LogicalDeviceId(Knowledge, BuiltInReadProbeRegistry.Families, Inventory()));
        var other = Inventory() with
        {
            Firmware = new FirmwareInventory { BaseboardManufacturer = "Contoso", BaseboardProduct = "HH-1" }
        };
        Assert.Equal("observed-hh-1",
            DeviceKnowledgeAssessor.LogicalDeviceId(Knowledge, BuiltInReadProbeRegistry.Families, other));
    }

    [Fact]
    public void Assess_RefusesAFamilyThatNamesAnExtractedRecord()
    {
        var family = Claw with { KnowledgeRecordId = "hc.claw-a2-vm" };

        Assert.Throws<InvalidDataException>(() =>
            DeviceKnowledgeAssessor.Assess(Knowledge, family, Inventory(), family.DeviceId));
    }

    [Fact]
    public void PluginIdentity_UnavailableWmiNamespaceDoesNotClaimAProviderSignature()
    {
        var inventory = Inventory();
        var unavailable = inventory with
        {
            WmiClasses = [inventory.WmiClasses[0] with { Access = WmiAccess.NamespaceUnavailable }]
        };
        var accessDenied = inventory with
        {
            WmiClasses = [inventory.WmiClasses[0] with { Access = WmiAccess.AccessDenied }]
        };

        string[] expected = ["root\\WMI:MSI_ACPI"];
        Assert.Empty(DeviceLabApplication.ToPluginIdentity(unavailable).WmiProviderSignatures);
        Assert.Equal(expected, DeviceLabApplication.ToPluginIdentity(accessDenied).WmiProviderSignatures);
    }

    private static MachineInventory Inventory()
    {
        return new MachineInventory
        {
            SchemaVersion = 1,
            Firmware = new FirmwareInventory
            {
                SystemManufacturer = "Micro-Star International Co., Ltd.",
                BaseboardManufacturer = "Micro-Star International Co., Ltd.",
                BaseboardProduct = "MS-1T52",
                SystemSku = "1T52.1"
            },
            UsbInterfaces =
            [
                new UsbInterfaceInventory
                {
                    InstanceId = "synthetic-instance",
                    VendorId = "0DB0",
                    ProductId = "1901",
                    DeviceRelease = Claw.ReferenceUsbDeviceRelease,
                    Present = true
                }
            ],
            WmiClasses =
            [
                new WmiClassInventory
                {
                    Namespace = "root\\WMI",
                    ClassName = "MSI_ACPI",
                    Access = WmiAccess.Available
                }
            ],
            CapturedAt = DateTimeOffset.UnixEpoch
        };
    }
}
