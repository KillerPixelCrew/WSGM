using WSGM.Device.Sdk.Identity;
using WSGM.DeviceLab.Inventory;
using WSGM.DeviceLab.Knowledge;

namespace WSGM.DeviceLab.Tests.Knowledge;

public sealed class DeviceKnowledgeTests
{
    private static DeviceKnowledgeBase Knowledge => DeviceKnowledgeBase.Default;

    [Fact]
    public void EmbeddedRecords_LoadAndCarryTheCuratedSeeds()
    {
        var ids = Knowledge.Records.Select(record => record.Id).ToArray();

        Assert.Contains("wsgm.claw-8-a2vm", ids);
        Assert.Contains("wsgm.rog-ally-x", ids);
        Assert.Contains("wsgm.xbox-rog-ally-x", ids);
        Assert.True(Knowledge.Records.Count(record => record.Status is DeviceKnowledgeStatus.Extracted) > 50);
        Assert.All(Knowledge.Records, record =>
        {
            Assert.True(record.Id.StartsWith(record.Status is DeviceKnowledgeStatus.Curated ? "wsgm." : "hc.",
                StringComparison.Ordinal), record.Id);
            Assert.NotEmpty(record.Provenance);
        });
    }

    [Fact]
    public void ExtractedRecords_NeverCarryWizardMappingsOrReadbacks()
    {
        // Extracted records are unreviewed: a button mapping or a readback claim has to come from a
        // curated record, where a person checked it.
        foreach (var record in Knowledge.Records.Where(record => record.Status is DeviceKnowledgeStatus.Extracted))
        {
            Assert.All(record.Buttons, button => Assert.Null(button.WizardButton));
            Assert.All(record.Mechanisms, mechanism => Assert.False(mechanism.HasReadback, record.Id));
            Assert.All(record.Provenance,
                provenance => Assert.Equal(DeviceKnowledgeSource.HcDerived, provenance.Source));
        }
    }

    [Fact]
    public void Parse_RejectsUnknownMembers()
    {
        const string json = """
                            {
                              "schemaVersion": 1,
                              "id": "wsgm.test",
                              "displayName": "Test",
                              "status": "Curated",
                              "identity": [{ "baseboardManufacturer": "X" }],
                              "unexpected": true
                            }
                            """;

        Assert.Throws<InvalidDataException>(() => DeviceKnowledgeBase.Parse(json));
    }

    [Fact]
    public void Create_RejectsAnExtractedRecordThatSupersedesAnother()
    {
        var extracted = Record("hc.a", DeviceKnowledgeStatus.Extracted) with { Supersedes = ["hc.b"] };

        Assert.Throws<InvalidDataException>(() =>
            DeviceKnowledgeBase.Create([extracted, Record("hc.b", DeviceKnowledgeStatus.Extracted)]));
    }

    [Fact]
    public void Create_RejectsARecordWithoutAnIdentityRule()
    {
        var record = Record("wsgm.a", DeviceKnowledgeStatus.Curated) with { Identity = [] };

        Assert.Throws<InvalidDataException>(() => DeviceKnowledgeBase.Create([record]));
    }

    [Fact]
    public void Match_ClawRanksTheCuratedRecordFirstAndKeepsHcsRecordForComparison()
    {
        var matches = DeviceKnowledgeMatcher.Match(Knowledge, Identity(
            "Micro-Star International Co., Ltd.", "MS-1T52", sku: "1T52.1"));

        Assert.Equal(["wsgm.claw-8-a2vm", "hc.claw-a2-vm"], matches.Select(match => match.RecordId));
        Assert.All(matches, match => Assert.False(match.Fallback));
    }

    [Fact]
    public void Match_CuratedClawRecordAgreesWithTheReadProbeFingerprint()
    {
        var fingerprint = KnownMsiClaw.Create();
        var claw = Knowledge.Records.Single(record => record.Id == "wsgm.claw-8-a2vm");

        var rule = Assert.Single(claw.Identity);
        Assert.Equal(fingerprint.SystemManufacturer, rule.BaseboardManufacturer);
        Assert.Equal(fingerprint.BaseboardProduct, rule.BaseboardProduct);
        Assert.Equal(fingerprint.SystemSku, rule.SystemSku);
    }

    [Fact]
    public void Match_XboxAllyXHidesTheSupersededExtractedRecord()
    {
        var matches = DeviceKnowledgeMatcher.Match(Knowledge, Identity("ASUSTeK COMPUTER INC.", "RC73XA"));

        var match = Assert.Single(matches);
        Assert.Equal("wsgm.xbox-rog-ally-x", match.RecordId);
    }

    [Fact]
    public void Match_ManufacturerComparisonIgnoresCaseLikeHc()
    {
        // HC upper-cases the baseboard manufacturer before its switch.
        var matches = DeviceKnowledgeMatcher.Match(Knowledge, Identity("ASUSTEK COMPUTER INC.", "RC71L"));

        Assert.Equal("hc.rog-ally", Assert.Single(matches).RecordId);
    }

    [Fact]
    public void Match_ProcessorSubstringRuleBeatsItsFallbackSibling()
    {
        var lite4500 = DeviceKnowledgeMatcher.Match(Knowledge, Identity(
            "AYANEO", "NEXT Lite", "AMD Ryzen 5 4500U with Radeon Graphics"));
        var lite = DeviceKnowledgeMatcher.Match(Knowledge, Identity(
            "AYANEO", "NEXT Lite", "AMD Ryzen 7 4800U with Radeon Graphics"));

        Assert.Equal("hc.ayaneonext-lite4500-u", Assert.Single(lite4500).RecordId);
        var fallback = Assert.Single(lite);
        Assert.Equal("hc.ayaneonext-lite", fallback.RecordId);
        Assert.True(fallback.Fallback);
    }

    [Fact]
    public void Match_UnknownProcessorOnAVendorDefaultBranchIsAFallback()
    {
        var matches = DeviceKnowledgeMatcher.Match(Knowledge, Identity(
            "GPD", "G1619-04", "AMD Ryzen 9 Future"));

        var match = Assert.Single(matches);
        Assert.Equal("hc.gpd-win-max2-2024-8840-u", match.RecordId);
        Assert.True(match.Fallback);
    }

    [Fact]
    public void Match_UnknownDeviceMatchesNothing()
    {
        Assert.Empty(DeviceKnowledgeMatcher.Match(Knowledge, Identity("Contoso", "Handheld 1")));
    }

    [Fact]
    public void Match_LenovoMatchesOnSystemModel()
    {
        var matches = DeviceKnowledgeMatcher.Match(Knowledge, Identity("LENOVO", "LNVNB161216", model: "83E1"));

        Assert.Equal("hc.legion-go-tablet", Assert.Single(matches).RecordId);
    }

    [Fact]
    public void ExtractedRecords_RecordHcsUnloadedAndMissingImuConfiguration()
    {
        var win4 = Knowledge.Records.Single(record => record.Id == "hc.gpd-win4-2023");
        var legionS2 = Knowledge.Records.Single(record => record.Id == "hc.legion-go-sz2");
        var apex = Knowledge.Records.Single(record => record.Id == "hc.one-x-player-apex");

        Assert.Null(win4.Motion);
        Assert.Contains(win4.Hazards, hazard => hazard.Contains("GPDWin4-2023.json", StringComparison.Ordinal));
        Assert.Null(legionS2.Motion);
        Assert.Contains(legionS2.Hazards, hazard => hazard.Contains("identity axes", StringComparison.Ordinal));
        Assert.Contains(apex.Hazards, hazard => hazard.Contains("same output", StringComparison.Ordinal));
    }

    [Fact]
    public void Identity_ReadsTheFieldsHcsSwitchUses()
    {
        var inventory = new MachineInventory
        {
            SchemaVersion = 1,
            Firmware = new FirmwareInventory
            {
                BaseboardManufacturer = "GPD",
                BaseboardProduct = "G1618-04",
                BaseboardVersion = "V1",
                SystemProduct = "G1618-04",
                SystemSku = "SKU"
            },
            Processor = new ProcessorInventory { Name = "AMD Ryzen 7 6800U with Radeon Graphics" },
            CapturedAt = DateTimeOffset.UnixEpoch
        };

        var identity = DeviceKnowledgeIdentity.From(inventory);

        Assert.Equal("GPD", identity.BaseboardManufacturer);
        Assert.Equal("G1618-04", identity.BaseboardProduct);
        Assert.Equal("V1", identity.BaseboardVersion);
        Assert.Equal("G1618-04", identity.SystemProduct);
        Assert.Equal("SKU", identity.SystemSku);
        Assert.Equal("hc.gpd-win4", Assert.Single(DeviceKnowledgeMatcher.Match(Knowledge, identity)).RecordId);
    }

    private static DeviceIdentitySnapshot Identity(
        string manufacturer,
        string product,
        string? processor = null,
        string? model = null,
        string? sku = null)
    {
        return new DeviceIdentitySnapshot
        {
            BaseboardManufacturer = manufacturer,
            BaseboardProduct = product,
            ProcessorName = processor,
            SystemProduct = model,
            SystemSku = sku
        };
    }

    private static DeviceKnowledgeRecord Record(string id, DeviceKnowledgeStatus status)
    {
        return new DeviceKnowledgeRecord
        {
            SchemaVersion = DeviceKnowledgeBase.SchemaVersion,
            Id = id,
            DisplayName = id,
            Status = status,
            Identity = [new HardwareMatchRule { BaseboardManufacturer = "Contoso" }]
        };
    }
}
