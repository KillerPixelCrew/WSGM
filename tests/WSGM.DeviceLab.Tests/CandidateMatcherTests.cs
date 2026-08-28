using WSGM.Device.Contracts.Identity;
using WSGM.Device.Contracts.Modules;
using WSGM.Device.Contracts.Packaging;
using WSGM.DeviceLab.Core.Catalog;
using WSGM.DeviceLab.Core.Evidence;
using WSGM.DeviceLab.Core.Inventory;

namespace WSGM.DeviceLab.Tests;

/// <summary>
/// The executable specification of offline candidate matching, including the negative cases the
/// design names explicitly: sibling boards, unrelated machines from the same vendor, and spoofed USB
/// identifiers.
/// </summary>
public class CandidateMatcherTests
{
    [Fact]
    public void TheMsiWmiTransport_IsReusableOnTheReferenceUnit()
    {
        CandidateAssessment transport = Rank(ReferenceUnit())
            .Single(a => a.ModuleId == "MsiWmiPlatform");

        Assert.True(transport.ReuseRank > 0);
        Assert.NotEqual(WriteEligibility.Quarantined, transport.WriteEligibility);
    }

    [Fact]
    public void TheA1MPowerPolicy_IsRejectedOnTheA2VM()
    {
        // The concrete failure the layer split exists to prevent: A1M limits applied to an A2VM.
        CandidateAssessment a1m = Rank(ReferenceUnit())
            .Single(a => a.ModuleId == "MsiClawA1MPowerPolicy");

        Assert.Equal(0, a1m.ReuseRank);
        Assert.Equal(WriteEligibility.ReadOnly, a1m.WriteEligibility);
        Assert.Contains(a1m.Explanations, e => e.Contains("verified on", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("MS-1T41")]
    [InlineData("MS-1T42")]
    public void ASiblingClawBoard_RejectsTheA2VmPolicy(string board)
    {
        // MS-1T41 is the A1M, MS-1T42 the 7-inch A2VM. Same vendor, same family, different limits.
        MachineInventory sibling = ReferenceUnit() with
        {
            Firmware = ReferenceUnit().Firmware with { BaseboardProduct = board },
        };

        CandidateAssessment policy = Rank(sibling)
            .Single(a => a.ModuleId == "MsiClawA2VmPowerPolicy");

        Assert.Equal(0, policy.ReuseRank);
    }

    [Fact]
    public void AnUnrelatedMsiDesktop_MatchesNothingThatWrites()
    {
        // Same manufacturer string, entirely different hardware. Vendor identity alone must never be
        // enough to reuse a module.
        MachineInventory desktop = ReferenceUnit() with
        {
            Firmware = new FirmwareInventory
            {
                SystemManufacturer = "Micro-Star International Co., Ltd.",
                SystemProduct = "MS-7E01",
                BaseboardProduct = "MS-7E01",
            },
            UsbInterfaces = [],
            WmiClasses = [],
        };

        Assert.All(Rank(desktop), a =>
            Assert.NotEqual(WriteEligibility.Production, a.WriteEligibility));
    }

    [Fact]
    public void ASpoofedUsbIdentifierOnTheWrongBoard_DoesNotUnlockThePolicy()
    {
        // Presenting the right VID/PID from unrelated hardware must not reach a device-scoped module.
        MachineInventory spoofed = ReferenceUnit() with
        {
            Firmware = ReferenceUnit().Firmware with { BaseboardProduct = "GENERIC-BOARD" },
        };

        CandidateAssessment policy = Rank(spoofed)
            .Single(a => a.ModuleId == "MsiClawA2VmPowerPolicy");

        Assert.Equal(0, policy.ReuseRank);
        Assert.Equal(WriteEligibility.ReadOnly, policy.WriteEligibility);
    }

    [Fact]
    public void AProviderThatDeniedAccessStillCountsAsPresent()
    {
        // Measured on the reference unit: MSI_ACPI denies instance enumeration unelevated while its
        // schema reads fine. Treating denied as absent would make a de-elevated sweep reject the
        // transport that actually works once elevated.
        MachineInventory denied = ReferenceUnit();

        Assert.Equal(WmiAccess.AccessDenied,
            denied.WmiClasses.Single(c => c.ClassName == "MSI_ACPI").Access);
        Assert.True(Rank(denied).Single(a => a.ModuleId == "MsiWmiPlatform").ReuseRank > 0);
    }

    [Fact]
    public void AMissingProviderRejectsTheTransport()
    {
        MachineInventory noProvider = ReferenceUnit() with
        {
            WmiClasses =
            [
                new WmiClassInventory
                {
                    Namespace = "root\\WMI",
                    ClassName = "MSI_ACPI",
                    Access = WmiAccess.NotFound,
                },
            ],
        };

        Assert.Equal(0, Rank(noProvider).Single(a => a.ModuleId == "MsiWmiPlatform").ReuseRank);
    }

    [Fact]
    public void RankingIsDeterministicRegardlessOfCatalogOrder()
    {
        // A developer comparing two sweeps needs the difference to mean something about the hardware,
        // not about enumeration order.
        List<CatalogEntry> forward = [.. Catalog()];
        List<CatalogEntry> reversed = [.. Catalog()];
        reversed.Reverse();

        string[] first = [.. CandidateMatcher.Rank(ReferenceUnit(), forward, "ms-1t52")
            .Select(a => a.ModuleId)];
        string[] second = [.. CandidateMatcher.Rank(ReferenceUnit(), reversed, "ms-1t52")
            .Select(a => a.ModuleId)];

        Assert.Equal(first, second);
    }

    [Fact]
    public void EveryAssessmentExplainsItself()
    {
        Assert.All(Rank(ReferenceUnit()), a => Assert.NotEmpty(a.Explanations));
    }

    [Fact]
    public void ANonInheritableValueListIsCarriedToTheDeveloper()
    {
        // "Reuse the transport, not the limits" is only actionable if the limits are named.
        CandidateAssessment policy = Rank(ReferenceUnit())
            .Single(a => a.ModuleId == "MsiClawA2VmPowerPolicy");

        Assert.Contains(policy.NonInheritableValues, v =>
            v.Contains("power limit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AQuarantinedCapabilityBlocksWritesOnAnOtherwiseVerifiedModule()
    {
        IReadOnlyList<CandidateAssessment> assessments = CandidateMatcher.Rank(
            ReferenceUnit(), Catalog(), "ms-1t52",
            new HashSet<string>(StringComparer.Ordinal) { "power.primary-limit" });

        Assert.Equal(WriteEligibility.Quarantined,
            assessments.Single(a => a.ModuleId == "MsiClawA2VmPowerPolicy").WriteEligibility);
    }

    private static IReadOnlyList<CandidateAssessment> Rank(MachineInventory inventory) =>
        CandidateMatcher.Rank(inventory, Catalog(), "ms-1t52");

    private static IReadOnlyList<CatalogEntry> Catalog() =>
    [
        new CatalogEntry
        {
            Module = new ImplementationModule
            {
                Id = "MsiWmiPlatform",
                Version = 1,
                Layer = ModuleLayer.Transport,
                DisplayName = "MSI named-method WMI transport",
                Safety = new ModuleSafety { Writes = true, Persistence = PersistenceClass.Volatile },
                Recovery = new ModuleRecovery(),
                Provenance = Provenance(),
            },
            CandidateMatching =
            [
                new IdentityObservation
                {
                    Signal = IdentitySignal.SmbiosSystemManufacturer,
                    Strength = IdentityStrength.Required,
                    Values = ["Micro-Star International Co., Ltd."],
                },
                new IdentityObservation
                {
                    Signal = IdentitySignal.WmiProviderSignature,
                    Strength = IdentityStrength.Required,
                    Values = ["root\\WMI:MSI_ACPI"],
                },
                new IdentityObservation
                {
                    Signal = IdentitySignal.CpuIdentity,
                    Strength = IdentityStrength.Weighted,
                    Weight = 20,
                    Values = ["6-189-1"],
                },
            ],
        },
        new CatalogEntry
        {
            Module = new ImplementationModule
            {
                Id = "MsiClawA2VmPowerPolicy",
                Version = 1,
                Layer = ModuleLayer.Policy,
                DisplayName = "MS-1T52 power policy",
                VerifiedDeviceIds = ["ms-1t52"],
                Capabilities = ["power.primary-limit"],
                Safety = new ModuleSafety { Writes = true, Persistence = PersistenceClass.Volatile },
                Recovery = new ModuleRecovery { SnapshotRequired = true },
                Provenance = Provenance(),
            },
            CandidateMatching =
            [
                new IdentityObservation
                {
                    Signal = IdentitySignal.SmbiosBaseboardProduct,
                    Strength = IdentityStrength.Required,
                    Values = ["MS-1T52"],
                },
            ],
            Claims = [Claim("MS-1T52", ClaimState.HardwareVerified)],
            NonInheritableValues = ["PL1/PL2 power limit ceilings", "scenario-mode table"],
        },
        new CatalogEntry
        {
            Module = new ImplementationModule
            {
                Id = "MsiClawA1MPowerPolicy",
                Version = 1,
                Layer = ModuleLayer.Policy,
                DisplayName = "MS-1T41 power policy",
                VerifiedDeviceIds = ["ms-1t41"],
                Capabilities = ["power.primary-limit"],
                Safety = new ModuleSafety { Writes = true, Persistence = PersistenceClass.Volatile },
                Recovery = new ModuleRecovery { SnapshotRequired = true },
                Provenance = Provenance(),
            },
            CandidateMatching =
            [
                new IdentityObservation
                {
                    Signal = IdentitySignal.SmbiosSystemManufacturer,
                    Strength = IdentityStrength.Required,
                    Values = ["Micro-Star International Co., Ltd."],
                },
            ],
            Claims = [Claim("MS-1T41", ClaimState.HardwareVerified)],
            NonInheritableValues = ["PL1/PL2 power limit ceilings"],
        },
    ];

    private static EvidenceClaim Claim(string board, ClaimState state) => new()
    {
        ClaimId = $"{board}-power",
        Scope = new ClaimScope { BaseboardProduct = board },
        Transport = "msi-wmi",
        Selector = "Get_Data",
        ProposedMeaning = "sustained power limit",
        State = state,
        Provenance = new ClaimProvenance
        {
            Source = "reference unit capture",
            Kind = ProvenanceKind.IndependentCapture,
        },
    };

    private static PackageProvenance Provenance() => new()
    {
        Source = "WSGM first-party",
        License = "GPL-3.0-or-later",
        ProvenanceClass = WSGM.Device.Contracts.Packaging.ProvenanceClass.IndependentCapture,
    };

    /// <summary>Values as read from the reference unit by <c>wsgm-device inventory</c>.</summary>
    private static MachineInventory ReferenceUnit() => new()
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
                VendorId = "0DB0",
                ProductId = "1901",
                LocationPath = "PCIROOT(0)#PCI(1400)#USBROOT(0)#USB(2)",
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
        ],
        CapturedAt = new DateTimeOffset(2026, 8, 27, 12, 0, 0, TimeSpan.Zero),
    };
}
