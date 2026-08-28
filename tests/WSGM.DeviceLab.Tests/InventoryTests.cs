using WSGM.Device.Contracts.Identity;
using WSGM.Device.Contracts.Packaging;
using WSGM.DeviceLab.Core;
using WSGM.DeviceLab.Core.Inventory;

namespace WSGM.DeviceLab.Tests;

/// <summary>
/// The executable specification of the inventory schema, using values captured from the reference
/// unit so the shape is checked against real hardware output rather than an invented example.
/// </summary>
public class InventoryTests
{
    [Fact]
    public void TheReferenceUnitInventory_FeedsTheIdentityMatcherDirectly()
    {
        // Inventory and matching are separate stages, and this is the seam between them: a capture
        // taken today must be re-matchable against a catalog that grows later.
        DeviceIdentitySnapshot snapshot = ToSnapshot(ReferenceUnitInventory());

        IdentityMatchResult result = IdentityMatcher.Match(ClawDefinition(), snapshot);

        Assert.Equal(IdentityMatchOutcome.Matched, result.Outcome);
    }

    [Fact]
    public void TheBoardAndSystemProductAreKeptApart()
    {
        // The captured values are genuinely different strings, which is the whole reason the schema
        // has two fields. Merging them would make the exact board unmatchable.
        FirmwareInventory firmware = ReferenceUnitInventory().Firmware;

        Assert.Equal("MS-1T52", firmware.BaseboardProduct);
        Assert.Equal("Claw 8 AI+ A2VM", firmware.SystemProduct);
        Assert.NotEqual(firmware.BaseboardProduct, firmware.SystemProduct);
    }

    [Fact]
    public void TheUselessSmbiosEcVersionIsRecordedRatherThanDropped()
    {
        // 255.255 is the SMBIOS "unknown" encoding, which this unit really returns. Keeping it lets a
        // matcher tell "firmware says it does not know" from "nobody looked"; the usable version comes
        // from the vendor provider instead.
        Assert.Equal("255.255", ReferenceUnitInventory().Firmware.EmbeddedControllerVersion);
    }

    [Fact]
    public void AccessDeniedIsDistinctFromNotFound()
    {
        // Measured on this unit: MSI_ACPI denies instance enumeration unelevated while its schema
        // reads fine. Recording both as "absent" would diagnose a rights problem as a missing
        // provider, and a de-elevated host would misreport a present provider as unsupported.
        MachineInventory inventory = ReferenceUnitInventory();

        WmiClassInventory msiAcpi = inventory.WmiClasses.Single(c => c.ClassName == "MSI_ACPI");

        Assert.Equal(WmiAccess.AccessDenied, msiAcpi.Access);
        Assert.NotEmpty(msiAcpi.MethodNames);
        Assert.Null(msiAcpi.InstanceCount);
    }

    [Fact]
    public void APresentProviderIsStillDetectableWhileDenied()
    {
        // The consequence that matters for the trust-tiered spawn decision: capability detection must
        // work without elevation, or an untrusted de-elevated package reports the wrong reason.
        WmiClassInventory msiAcpi = ReferenceUnitInventory()
            .WmiClasses.Single(c => c.ClassName == "MSI_ACPI");

        Assert.NotEqual(WmiAccess.NotFound, msiAcpi.Access);
        Assert.Contains("Get_Data", msiAcpi.MethodNames);
    }

    [Fact]
    public void SerializationIsDeterministic()
    {
        // Two runs over the same machine must produce byte-identical output, or a capture cannot be
        // diffed or hashed meaningfully.
        MachineInventory inventory = ReferenceUnitInventory();

        Assert.Equal(DeviceLabJson.Serialize(inventory), DeviceLabJson.Serialize(inventory));
    }

    [Fact]
    public void CpuIdentityIsNormalizedForMatching()
    {
        Assert.Equal("6-189-1", ReferenceUnitInventory().Processor!.NormalizedIdentity);
    }

    [Fact]
    public void AnIncompleteCpuReadYieldsNoIdentityRatherThanAPartialOne()
    {
        // A partial identity would match a predicate it should not. Absent is the honest answer.
        ProcessorInventory partial = new() { Name = "Unknown CPU", Family = 6 };

        Assert.Null(partial.NormalizedIdentity);
    }

    private static DeviceIdentitySnapshot ToSnapshot(MachineInventory inventory) => new()
    {
        SystemManufacturer = inventory.Firmware.SystemManufacturer,
        SystemProduct = inventory.Firmware.SystemProduct,
        SystemSku = inventory.Firmware.SystemSku,
        SystemFamily = inventory.Firmware.SystemFamily,
        BaseboardProduct = inventory.Firmware.BaseboardProduct,
        BaseboardVersion = inventory.Firmware.BaseboardVersion,
        BiosVersion = inventory.Firmware.BiosVersion,
        CpuIdentity = inventory.Processor?.NormalizedIdentity,
        UsbEndpoints = [.. inventory.UsbInterfaces
            .Where(i => i.VendorId is not null && i.ProductId is not null)
            .Select(i => new UsbEndpointObservation
            {
                VendorId = i.VendorId!,
                ProductId = i.ProductId!,
                InterfaceNumber = i.InterfaceNumber,
                LocationPath = i.LocationPath,
            })],
        WmiProviderSignatures = [.. inventory.WmiClasses
            .Where(c => c.Access is not WmiAccess.NotFound)
            .Select(c => $"{c.Namespace}:{c.ClassName}")],
    };

    /// <summary>
    /// Values as read from the reference unit by <c>wsgm-device inventory</c>, unelevated.
    /// </summary>
    private static MachineInventory ReferenceUnitInventory() => new()
    {
        SchemaVersion = WindowsInventoryCollector.CurrentSchemaVersion,
        Firmware = new FirmwareInventory
        {
            SystemManufacturer = "Micro-Star International Co., Ltd.",
            SystemProduct = "Claw 8 AI+ A2VM",
            SystemSku = "1T52.1",
            SystemFamily = "Claw",
            BaseboardProduct = "MS-1T52",
            BaseboardVersion = "REV:1.0",
            BiosVersion = "E1T52IMS.112",
            EmbeddedControllerVersion = "255.255",
        },
        Processor = new ProcessorInventory
        {
            Name = "Intel(R) Core(TM) Ultra 7 258V",
            Family = 6,
            Model = 189,
            Stepping = 1,
            Cores = 8,
        },
        UsbInterfaces =
        [
            new UsbInterfaceInventory
            {
                InstanceId = @"USB\VID_0DB0&PID_1901\00006F64096B22E7",
                DeviceClass = "USB",
                VendorId = "0DB0",
                ProductId = "1901",
                LocationPath = "PCIROOT(0)#PCI(1400)#USBROOT(0)#USB(2)",
                Present = true,
            },
            new UsbInterfaceInventory
            {
                InstanceId = @"USB\VID_0DB0&PID_1901&MI_00\6&2B02AE9F&0&0000",
                DeviceClass = "XnaComposite",
                VendorId = "0DB0",
                ProductId = "1901",
                InterfaceNumber = 0,
                Present = true,
            },
        ],
        WmiClasses =
        [
            new WmiClassInventory
            {
                Namespace = "root\\WMI",
                ClassName = "MSI_ACPI",
                Access = WmiAccess.AccessDenied,
                MethodNames = ["Get_Data", "Set_Data"],
            },
            new WmiClassInventory
            {
                Namespace = "root\\WMI",
                ClassName = "MSI_Event",
                Access = WmiAccess.Available,
                InstanceCount = 0,
            },
        ],
        CapturedAt = new DateTimeOffset(2026, 8, 27, 12, 0, 0, TimeSpan.Zero),
    };

    private static DeviceDefinition ClawDefinition() => new()
    {
        Id = "ms-1t52",
        DisplayName = "MSI Claw 8 AI+ A2VM",
        Identity =
        [
            new IdentityObservation
            {
                Signal = IdentitySignal.SmbiosSystemManufacturer,
                Strength = IdentityStrength.Required,
                Values = ["Micro-Star International Co., Ltd."],
            },
            new IdentityObservation
            {
                Signal = IdentitySignal.SmbiosBaseboardProduct,
                Strength = IdentityStrength.Required,
                Values = ["MS-1T52"],
            },
            new IdentityObservation
            {
                Signal = IdentitySignal.SmbiosSystemSku,
                Strength = IdentityStrength.Required,
                Values = ["1T52.1"],
            },
            new IdentityObservation
            {
                Signal = IdentitySignal.WmiProviderSignature,
                Strength = IdentityStrength.Weighted,
                Weight = 30,
                Values = ["root\\WMI:MSI_ACPI"],
            },
        ],
    };
}
