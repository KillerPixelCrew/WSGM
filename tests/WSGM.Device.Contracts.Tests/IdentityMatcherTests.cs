using WSGM.Device.Contracts.Identity;
using WSGM.Device.Contracts.Packaging;

namespace WSGM.Device.Contracts.Tests;

/// <summary>
/// The executable specification of device selection: which machine a definition claims, and — more
/// importantly — which machines it must refuse.
/// </summary>
public class IdentityMatcherTests
{
    [Fact]
    public void Match_TheReferenceUnit_Matches()
    {
        IdentityMatchResult result = IdentityMatcher.Match(ClawDefinition(), ReferenceUnit());

        Assert.Equal(IdentityMatchOutcome.Matched, result.Outcome);
        Assert.Empty(result.Rejections);
    }

    [Theory]
    [InlineData("MS-1T41")]
    [InlineData("MS-1T42")]
    public void Match_ASiblingBoard_IsRejectedEvenThoughEverythingElseAgrees(string board)
    {
        // MS-1T41 is the A1M and MS-1T42 the 7-inch A2VM. Same vendor, same family, same marketing
        // lineage, different power limits and firmware offsets - so a near miss here would apply one
        // board's limits to another. This is the case the whole exact-gate rule exists for.
        DeviceIdentitySnapshot sibling = ReferenceUnit() with { BaseboardProduct = board };

        IdentityMatchResult result = IdentityMatcher.Match(ClawDefinition(), sibling);

        Assert.Equal(IdentityMatchOutcome.Rejected, result.Outcome);
        Assert.NotEmpty(result.Rejections);
    }

    [Fact]
    public void Match_AHighScoringMachineThatFailsOneHardConstraint_IsStillRejected()
    {
        // Every weighted signal agrees and the score is high; the board does not match. Rank must
        // not be able to overturn that.
        DeviceIdentitySnapshot impostor = ReferenceUnit() with { BaseboardProduct = "MS-9999" };

        IdentityMatchResult result = IdentityMatcher.Match(ClawDefinition(), impostor);

        Assert.Equal(IdentityMatchOutcome.Rejected, result.Outcome);
        Assert.True(result.Score > 0, "the impostor should still score, which is exactly the risk");
    }

    [Fact]
    public void Match_AnUnrelatedMsiDesktop_IsRejected()
    {
        DeviceIdentitySnapshot desktop = new()
        {
            SystemManufacturer = "Micro-Star International Co., Ltd.",
            SystemProduct = "MS-7E01",
            BaseboardProduct = "MS-7E01",
            CpuIdentity = "6-183-1",
        };

        Assert.Equal(IdentityMatchOutcome.Rejected, IdentityMatcher.Match(ClawDefinition(), desktop).Outcome);
    }

    [Fact]
    public void Match_AMachineMissingTheGatedValueEntirely_IsRejected()
    {
        // Absent is not a wildcard. A definition gating on a board must not be satisfied by a machine
        // that reports no board at all.
        DeviceIdentitySnapshot blank = ReferenceUnit() with { BaseboardProduct = null };

        Assert.Equal(IdentityMatchOutcome.Rejected, IdentityMatcher.Match(ClawDefinition(), blank).Outcome);
    }

    [Theory]
    [InlineData("  MS-1T52  ")]
    [InlineData("ms-1t52")]
    [InlineData("MS-1T52")]
    public void Match_VendorWhitespaceAndCasingVariations_StillMatch(string board)
    {
        // Firmware strings are hand-entered and drift across revisions in ways that carry no meaning.
        // A BIOS update adding a trailing space must not silently unmatch the device.
        DeviceIdentitySnapshot unit = ReferenceUnit() with { BaseboardProduct = board };

        Assert.Equal(IdentityMatchOutcome.Matched, IdentityMatcher.Match(ClawDefinition(), unit).Outcome);
    }

    [Fact]
    public void Match_ASpoofedVendorIdWithTheWrongBoard_IsRejected()
    {
        DeviceIdentitySnapshot spoofed = ReferenceUnit() with
        {
            BaseboardProduct = "GENERIC-BOARD",
            UsbEndpoints =
            [
                new UsbEndpointObservation { VendorId = "0DB0", ProductId = "1901", DeviceRelease = "0229" },
            ],
        };

        Assert.Equal(IdentityMatchOutcome.Rejected, IdentityMatcher.Match(ClawDefinition(), spoofed).Outcome);
    }

    [Fact]
    public void Match_AnExcludedFirmware_IsRejected()
    {
        DeviceDefinition gated = ClawDefinition() with
        {
            Identity =
            [
                .. ClawDefinition().Identity,
                new IdentityObservation
                {
                    Signal = IdentitySignal.UsbDeviceRelease,
                    Strength = IdentityStrength.Excluded,
                    Values = ["0100"],
                },
            ],
        };

        DeviceIdentitySnapshot oldFirmware = ReferenceUnit() with
        {
            UsbEndpoints =
            [
                new UsbEndpointObservation { VendorId = "0DB0", ProductId = "1901", DeviceRelease = "0100" },
            ],
        };

        Assert.Equal(IdentityMatchOutcome.Rejected, IdentityMatcher.Match(gated, oldFirmware).Outcome);
    }

    [Fact]
    public void Match_ScoreCountsOnlySatisfiedWeightedObservations()
    {
        DeviceIdentitySnapshot noMarketingName = ReferenceUnit() with { SystemProduct = null };

        IdentityMatchResult full = IdentityMatcher.Match(ClawDefinition(), ReferenceUnit());
        IdentityMatchResult partial = IdentityMatcher.Match(ClawDefinition(), noMarketingName);

        Assert.Equal(IdentityMatchOutcome.Matched, partial.Outcome);
        Assert.True(partial.Score < full.Score);
    }

    [Fact]
    public void Match_ExplainsEveryObservationItEvaluated()
    {
        // Device Lab has to tell a developer why a candidate was rejected. An unexplained rejection
        // sends them reading the matcher instead of their manifest.
        IdentityMatchResult result = IdentityMatcher.Match(
            ClawDefinition(),
            ReferenceUnit() with { BaseboardProduct = "MS-1T41" });

        Assert.Equal(ClawDefinition().Identity.Count, result.Explanations.Count);
        Assert.All(result.Explanations, e => Assert.False(string.IsNullOrWhiteSpace(e.Explanation)));
        Assert.Contains(result.Rejections, r => r.Signal == IdentitySignal.SmbiosBaseboardProduct);
    }

    [Fact]
    public void Match_HardConstraintsContributeNothingToScore()
    {
        IdentityMatchResult result = IdentityMatcher.Match(ClawDefinition(), ReferenceUnit());

        Assert.All(
            result.Explanations.Where(e =>
                e.Strength is IdentityStrength.Required or IdentityStrength.Excluded),
            e => Assert.Equal(0, e.ScoreContribution));
    }

    [Fact]
    public void Match_InformationalObservations_NeverRejectOrScore()
    {
        DeviceDefinition withNote = ClawDefinition() with
        {
            Identity =
            [
                .. ClawDefinition().Identity,
                new IdentityObservation
                {
                    Signal = IdentitySignal.BiosVersion,
                    Strength = IdentityStrength.Informational,
                    Values = ["E1T52IMS.999"],
                },
            ],
        };

        IdentityMatchResult baseline = IdentityMatcher.Match(ClawDefinition(), ReferenceUnit());
        IdentityMatchResult annotated = IdentityMatcher.Match(withNote, ReferenceUnit());

        Assert.Equal(IdentityMatchOutcome.Matched, annotated.Outcome);
        Assert.Equal(baseline.Score, annotated.Score);
    }

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
                Signal = IdentitySignal.SmbiosBaseboardProduct,
                Strength = IdentityStrength.Excluded,
                Values = ["MS-1T41", "MS-1T42"],
            },
            new IdentityObservation
            {
                Signal = IdentitySignal.SmbiosSystemProduct,
                Strength = IdentityStrength.Weighted,
                Weight = 10,
                Values = ["Claw 8 AI+ A2VM"],
            },
            new IdentityObservation
            {
                Signal = IdentitySignal.CpuIdentity,
                Strength = IdentityStrength.Weighted,
                Weight = 20,
                Values = ["6-189-1"],
            },
        ],
    };

    private static DeviceIdentitySnapshot ReferenceUnit() => new()
    {
        SystemManufacturer = "Micro-Star International Co., Ltd.",
        SystemProduct = "Claw 8 AI+ A2VM",
        SystemSku = "1T52.1",
        SystemFamily = "Claw",
        BaseboardProduct = "MS-1T52",
        BiosVersion = "E1T52IMS.112",
        EcFirmwareVersion = "1T52EMS1.109",
        CpuIdentity = "6-189-1",
        UsbEndpoints =
        [
            new UsbEndpointObservation
            {
                VendorId = "0DB0",
                ProductId = "1901",
                InterfaceNumber = 0,
                DeviceRelease = "0229",
                ReportLengths = [64],
                LocationPath = "PCIROOT(0)#PCI(1400)#USBROOT(0)#USB(2)",
            },
        ],
        WmiProviderSignatures = ["root/WMI:MSI_ACPI"],
    };
}
