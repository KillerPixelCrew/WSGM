using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using WSGM.Device.Contracts.Identity;
using WSGM.Device.Contracts.Modules;
using WSGM.Device.Contracts.Packaging;
using WSGM.DeviceLab.Core.Evidence;
using WSGM.DeviceLab.Core.Preflight;
using WSGM.DeviceLab.Core.Probes;

namespace WSGM.DeviceLab.Core.Catalog;

/// <summary>Reviewed implementation knowledge shipped with Device Lab, never consulted at runtime.</summary>
public static class BuiltInKnownImplementationCatalog
{
    /// <summary>Creates the deterministic catalog and pins read probes to an installed ProbeHost.</summary>
    /// <param name="probeHostPath">Local reviewed ProbeHost executable.</param>
    /// <returns>Current built-in developer catalog.</returns>
    public static IReadOnlyList<CatalogEntry> Create(string? probeHostPath = null)
    {
        string hash = probeHostPath is { Length: > 0 } && File.Exists(probeHostPath)
            ? HashFile(probeHostPath)
            : new string('0', 64);
        return
        [
            new CatalogEntry
            {
                Module = Module(
                    "MsiWmiPlatform",
                    ModuleLayer.Transport,
                    "MSI named-method WMI transport",
                    writes: true,
                    elevation: true),
                CandidateMatching =
                [
                    Required(IdentitySignal.SmbiosSystemManufacturer, "Micro-Star International Co., Ltd."),
                    Required(IdentitySignal.WmiProviderSignature, "root\\WMI:MSI_ACPI"),
                    Weighted(IdentitySignal.CpuIdentity, 20, "6-189-1"),
                ],
                ReadProbes = MsiReadProbes(hash),
                NonInheritableValues =
                [
                    "named method response layouts",
                    "provider interface-version gates",
                    "addresses and subfeatures",
                ],
            },
            new CatalogEntry
            {
                Module = Module(
                    "MsiClawMcuProtocol",
                    ModuleLayer.Protocol,
                    "MSI Claw MCU HID protocol",
                    writes: true,
                    reenumeration: true),
                CandidateMatching =
                [
                    Required(IdentitySignal.UsbVendorId, "0DB0"),
                    Required(IdentitySignal.UsbProductId, ["1901", "1902"]),
                    Required(IdentitySignal.UsbDeviceRelease, "0229"),
                ],
                NonInheritableValues =
                [
                    "profile-memory addresses",
                    "RGB zone order and persistence",
                    "mode-specific interface layout",
                ],
            },
            new CatalogEntry
            {
                Module = Module(
                    "MsiClawA2VmLayout",
                    ModuleLayer.Layout,
                    "MS-1T52 endpoint and register layout",
                    writes: true,
                    verifiedDeviceIds: ["ms-1t52"]),
                CandidateMatching = A2VmIdentity(),
                Claims = A2VmClaims(),
                NonInheritableValues =
                [
                    "WMI addresses and response offsets",
                    "fan table width and conversion",
                    "controller profile-memory offsets",
                    "RGB zone order",
                ],
            },
            new CatalogEntry
            {
                Module = Module(
                    "MsiClawA2VmPowerPolicy",
                    ModuleLayer.Policy,
                    "MS-1T52 power and scenario policy",
                    writes: true,
                    acRequired: true,
                    verifiedDeviceIds: ["ms-1t52"],
                    capabilities: ["power.primary-limit", "power.scenario"]),
                CandidateMatching = A2VmIdentity(),
                Claims = A2VmClaims(),
                NonInheritableValues =
                [
                    "PL1 and PL2 ceilings",
                    "desired power pairs",
                    "scenario encoding",
                    "rollback ordering",
                ],
            },
            new CatalogEntry
            {
                Module = Module(
                    "MsiClawA2VmFanPolicy",
                    ModuleLayer.Policy,
                    "MS-1T52 two-channel fan policy",
                    writes: true,
                    acRequired: true,
                    verifiedDeviceIds: ["ms-1t52"],
                    capabilities: ["thermal.fan-control", "thermal.fan-rpm"]),
                CandidateMatching = A2VmIdentity(),
                Claims = A2VmClaims(),
                NonInheritableValues =
                [
                    "fan duty conversion",
                    "six-point temperature table",
                    "firmware release sequence",
                    "safe minimum duty",
                ],
            },
        ];
    }

    private static IReadOnlyList<ReadProbeMetadata> MsiReadProbes(string hostHash) =>
    [
        Probe("msi.claw-a2vm.wmi-version", ReadProbeFamily.Version,
            "root/WMI:MSI_ACPI.Get_WMI", "vendor-wmi", ReadProbeValueKind.Version, 4, 4, hostHash, 0, 255),
        Probe("msi.claw-a2vm.ec-version", ReadProbeFamily.EmbeddedController,
            "root/WMI:MSI_ACPI.Get_EC", "vendor-wmi", ReadProbeValueKind.Bytes, 32, 32, hostHash),
        Probe("msi.claw-a2vm.scenario-status", ReadProbeFamily.WmiStatus,
            "root/WMI:MSI_ACPI.Get_Data:0xd2", "power-policy", ReadProbeValueKind.Integer, 2, 2, hostHash, 0, 255),
        Probe("msi.claw-a2vm.fan-rpm", ReadProbeFamily.FanRpm,
            "root/WMI:MSI_ACPI.Get_Fan:0", "fan-control", ReadProbeValueKind.Text, 5, 5, hostHash),
        Probe("msi.claw-a2vm.charge-limit", ReadProbeFamily.ChargeState,
            "root/WMI:MSI_ACPI.Get_Data:0xd7", "charge-policy", ReadProbeValueKind.Integer, 2, 2, hostHash, 0, 100),
    ];

    private static ReadProbeMetadata Probe(
        string id,
        ReadProbeFamily family,
        string endpoint,
        string resource,
        ReadProbeValueKind kind,
        int minimumLength,
        int maximumLength,
        string hostHash,
        long? minimum = null,
        long? maximum = null) => new()
    {
        Id = id,
        Version = 1,
        FamilyId = "msi.claw-a2vm.ms-1t52",
        EndpointId = endpoint,
        ResourceId = resource,
        Family = family,
        Origin = DeviceLabOperationOrigin.ReviewedBuiltInCatalog,
        ImplementationSha256 = hostHash,
        MaximumReadsPerSecond = 2,
        TimeoutMilliseconds = 5_000,
        Repetitions = 2,
        ExpectedResponse = new ReadProbeResponseExpectation
        {
            ValueKind = kind,
            MinimumLength = minimumLength,
            MaximumLength = maximumLength,
            AllowedStatusCodes = [1],
            MinimumValue = minimum,
            MaximumValue = maximum,
        },
        CrossCheck = new ReadProbeCrossCheck
        {
            Id = $"{id}.repeat-read",
            Kind = ReadProbeCrossCheckKind.Equal,
        },
        EvidenceOutputId = $"probe/{id}/v1",
        RequiresElevation = true,
    };

    private static IReadOnlyList<IdentityObservation> A2VmIdentity() =>
    [
        Required(IdentitySignal.SmbiosSystemManufacturer, "Micro-Star International Co., Ltd."),
        Required(IdentitySignal.SmbiosBaseboardProduct, "MS-1T52"),
        Required(IdentitySignal.UsbVendorId, "0DB0"),
        Required(IdentitySignal.UsbProductId, ["1901", "1902"]),
        Required(IdentitySignal.UsbDeviceRelease, "0229"),
    ];

    private static IReadOnlyList<EvidenceClaim> A2VmClaims() =>
    [
        Claim("ms-1t52.wmi.version", "Get_WMI", null, "MSI WMI provider version 8.0"),
        Claim("ms-1t52.power.pl1", "Get_Data", 0x50, "current PL1 watt value"),
        Claim("ms-1t52.power.pl2", "Get_Data", 0x51, "current PL2 watt value"),
        Claim("ms-1t52.power.scenario", "Get_Data", 0xd2, "current scenario byte"),
        Claim("ms-1t52.fan.rpm", "Get_Fan", 0, "two big-endian fan tachometer divisors"),
        Claim("ms-1t52.charge.limit", "Get_Data", 0xd7, "current charge threshold percent"),
    ];

    private static EvidenceClaim Claim(string id, string selector, int? offset, string meaning) => new()
    {
        ClaimId = id,
        Scope = new ClaimScope
        {
            BaseboardProduct = "MS-1T52",
            BiosVersion = "E1T52IMS.112",
            EcFirmwareVersion = "1T52EMS1.109",
            ControllerFirmware = "0229",
        },
        Transport = "msi-wmi",
        Endpoint = "root\\WMI:MSI_ACPI",
        Selector = selector,
        Offset = offset,
        ProposedMeaning = meaning,
        State = ClaimState.HardwareVerified,
        Provenance = new ClaimProvenance
        {
            Source = "WSGM reference-unit capture 2026-08-27",
            Kind = ProvenanceKind.IndependentCapture,
        },
    };

    private static ImplementationModule Module(
        string id,
        ModuleLayer layer,
        string displayName,
        bool writes,
        bool elevation = false,
        bool acRequired = false,
        bool reenumeration = false,
        IReadOnlyList<string>? verifiedDeviceIds = null,
        IReadOnlyList<string>? capabilities = null) => new()
    {
        Id = id,
        Version = 1,
        Layer = layer,
        DisplayName = displayName,
        VerifiedDeviceIds = verifiedDeviceIds ?? [],
        Capabilities = capabilities ?? [],
        Safety = new ModuleSafety
        {
            Writes = writes,
            Persistence = PersistenceClass.Unknown,
            RequiresElevation = elevation,
            RequiresAcPower = acRequired,
            CausesReenumeration = reenumeration,
        },
        Recovery = new ModuleRecovery
        {
            SnapshotRequired = writes,
            RollbackVerifiedOnHardware = false,
        },
        Provenance = new PackageProvenance
        {
            Source = "WSGM independent reference-unit capture",
            SourceRevision = "2026-08-27",
            License = "GPL-3.0-or-later",
            ProvenanceClass = ProvenanceClass.IndependentCapture,
        },
    };

    private static IdentityObservation Required(
        IdentitySignal signal,
        string value,
        string? endpoint = null) => Required(signal, [value], endpoint);

    private static IdentityObservation Required(
        IdentitySignal signal,
        IReadOnlyList<string> values,
        string? endpoint = null) => new()
    {
        Signal = signal,
        Strength = IdentityStrength.Required,
        Values = values,
        EndpointId = endpoint,
    };

    private static IdentityObservation Weighted(IdentitySignal signal, int weight, string value) => new()
    {
        Signal = signal,
        Strength = IdentityStrength.Weighted,
        Weight = weight,
        Values = [value],
    };

    private static string HashFile(string path)
    {
        using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
    }
}
