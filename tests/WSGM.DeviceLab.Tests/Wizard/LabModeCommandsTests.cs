using System.Text.RegularExpressions;
using WSGM.DeviceLab.Knowledge;
using WSGM.DeviceLab.Wizard;

namespace WSGM.DeviceLab.Tests.Wizard;

public sealed class LabModeCommandsTests
{
    private static IReadOnlyList<LabModeCommand> All => LabModeCommands.All;

    [Fact]
    public void Ids_AreUnique()
    {
        Assert.Equal(All.Count, All.Select(command => command.Id).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void ClassBoundCommands_HaveOneEntryPerGroupAndClass()
    {
        var duplicates = All.Where(command => command.HcClass is not null)
            .GroupBy(command => (command.Group, command.HcClass))
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        Assert.Empty(duplicates);
    }

    [Fact]
    public void ControllerCommands_DoNotOverlapWithinAGroup()
    {
        var controllers = All.Where(command => command.HcClass is null).ToList();
        foreach (var first in controllers)
        {
            foreach (var second in controllers.Where(item =>
                         item.Group == first.Group && string.CompareOrdinal(item.Id, first.Id) > 0))
            {
                var overlap = first.Endpoint.VendorId == second.Endpoint.VendorId
                              && (first.Endpoint.ProductIds.Count == 0 || second.Endpoint.ProductIds.Count == 0
                                  || first.Endpoint.ProductIds.Intersect(second.Endpoint.ProductIds).Any());
                Assert.False(overlap, $"{first.Id} and {second.Id} can match the same controller.");
            }
        }
    }

    [Fact]
    public void Groups_AreEitherClassBoundOrControllerCommands()
    {
        Assert.All(All.GroupBy(command => command.Group), group =>
            Assert.Single(group.Select(command => command.HcClass is null).Distinct()));
    }

    [Fact]
    public void EveryCommand_HasFileAndLineProvenanceIncludingHc()
    {
        var reference = new Regex(@"\.(cs|py|config):\d+", RegexOptions.CultureInvariant);
        Assert.All(All, command =>
        {
            Assert.NotEmpty(command.Provenance);
            Assert.All(command.Provenance, item => Assert.Matches(reference, item));
            Assert.Contains(command.Provenance,
                item => item.StartsWith("_ref/HandheldCompanion 1.3.1.6: ", StringComparison.Ordinal));
            Assert.False(string.IsNullOrWhiteSpace(command.Description));
            Assert.False(string.IsNullOrWhiteSpace(command.Device));
        });
    }

    [Fact]
    public void Reports_AreWellFormedAndFitTheirFraming()
    {
        foreach (var command in All.Where(command => command.Withheld is null))
        {
            if (command.Write == LabModeWrite.ControllerMode)
            {
                Assert.Empty(command.Reports);
                Assert.Contains("<mode>", command.ModeParameters["report"], StringComparison.Ordinal);
                Assert.True(int.TryParse(command.ModeParameters["testMode"], out _));
                continue;
            }

            Assert.NotEmpty(command.Reports);
            foreach (var report in command.Reports.Concat(command.Restore))
            {
                if (command.Framing == LabModeFraming.OxpVendor)
                {
                    Assert.Matches(new Regex("^[0-9A-F]{2}: ", RegexOptions.CultureInvariant), report);
                    Assert.NotNull(LabModeCommands.Frame(command, report, 64));
                    Assert.NotNull(LabModeCommands.Frame(command, report, 65));
                    continue;
                }

                Assert.Matches(new Regex("^([0-9A-F]{2})( [0-9A-F]{2})*$", RegexOptions.CultureInvariant), report);
                var bytes = LabModeCommands.ParseHex(report);
                Assert.InRange(bytes.Length, 2, 65);
                if (command.Write == LabModeWrite.Feature)
                {
                    Assert.Equal(0x00, bytes[0]);
                }

                if (command.Endpoint.OutputLength is { } length && command.Write == LabModeWrite.Output)
                {
                    Assert.NotNull(LabModeCommands.Frame(command, report, length));
                }
            }
        }
    }

    [Fact]
    public void WithheldCommands_SendNothing()
    {
        Assert.All(All.Where(command => command.Withheld is not null), command =>
        {
            Assert.Empty(command.Reports);
            Assert.Empty(command.Restore);
        });
    }

    [Fact]
    public void Descriptions_SayWhetherTheChangeIsUndone()
    {
        Assert.All(All.Where(command => command.Withheld is null), command =>
        {
            if (command.Reversible)
            {
                Assert.Contains("at the end", command.Description, StringComparison.Ordinal);
            }
            else
            {
                Assert.Contains("cannot", command.Description, StringComparison.Ordinal);
            }
        });
    }

    [Fact]
    public void HcClasses_ExistInTheKnowledgeBase()
    {
        var classes = DeviceKnowledgeBase.Default.Records.Select(record => record.HcClass)
            .Concat(DeviceKnowledgeBase.Default.Records.SelectMany(record => record.HcBaseClasses))
            .ToHashSet(StringComparer.Ordinal);

        Assert.All(All.Where(command => command.HcClass is not null),
            command => Assert.Contains(command.HcClass, classes));
    }

    [Fact]
    public void LegionModeProducts_AreInItsEndpoint()
    {
        var command = Assert.Single(All, item => item.Write == LabModeWrite.ControllerMode);
        foreach (var pair in command.ModeParameters["modeFromProductId"].Split(',', StringSplitOptions.TrimEntries))
        {
            var product = Convert.ToUInt16(pair.Split(' ')[0], 16);
            Assert.Contains(product, command.Endpoint.ProductIds);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ZotacReports_CarryHcsCrc(int index)
    {
        var command = Assert.Single(All, item => item.Id == "zotac-m1-m2-remap");
        var bytes = LabModeCommands.ParseHex(command.Reports[index]);

        Assert.Equal(65, bytes.Length);
        var crc = ZotacCrc(bytes, 5, 62);
        Assert.Equal((byte)(crc >> 8), bytes[63]);
        Assert.Equal((byte)(crc & 0xFF), bytes[64]);
    }

    [Fact]
    public void Frame_PadsToTheCollectionAndRefusesWhatDoesNotFit()
    {
        var command = Assert.Single(All, item => item.Id == "legion-go-touchpad-passthrough-off");

        var frame = LabModeCommands.Frame(command, command.Reports[0], 64);

        Assert.NotNull(frame);
        Assert.Equal(64, frame.Length);
        Assert.Equal(new byte[] { 0x05, 0x06, 0x6B, 0x02, 0x04, 0x00, 0x01, 0x00 }, frame[..8]);
        Assert.Null(LabModeCommands.Frame(command, command.Reports[0], 4));
    }

    [Fact]
    public void Frame_LaysOutOxpVendorCommandsAsHcDoes()
    {
        var command = Assert.Single(All, item => item.Id == "oxp-x2-mini-pro-remap");
        var intercept = command.Reports[^1];

        var sixtyFive = LabModeCommands.Frame(command, intercept, 65);
        var sixtyFour = LabModeCommands.Frame(command, intercept, 64);

        Assert.NotNull(sixtyFive);
        Assert.Equal(65, sixtyFive.Length);
        Assert.Equal(new byte[] { 0x00, 0xB2, 0x3F, 0x01, 0x00, 0x01, 0x02, 0x00 }, sixtyFive[..8]);
        Assert.Equal(new byte[] { 0x3F, 0xB2 }, sixtyFive[^2..]);
        Assert.NotNull(sixtyFour);
        Assert.Equal(64, sixtyFour.Length);
        Assert.Equal(0xB2, sixtyFour[0]);
        Assert.Equal(new byte[] { 0x3F, 0xB2 }, sixtyFour[^2..]);
        Assert.Null(LabModeCommands.Frame(command, command.Reports[0], 33));
    }

    [Fact]
    public void For_CuratedRecord_OffersNothing()
    {
        var curated = DeviceKnowledgeBase.Default.Records.First(record =>
            record.Status is DeviceKnowledgeStatus.Curated);

        Assert.Empty(LabModeCommands.For(curated, LabModeStages.Buttons));
        Assert.Empty(LabModeCommands.For(curated, LabModeStages.Motion));
    }

    [Theory]
    [InlineData("hc.one-x-player-x1-pro", "oxp-x1-remap")]
    [InlineData("hc.one-x-player-x1-amd", "oxp-x1-remap")]
    [InlineData("hc.one-x-player-x1-mini", "oxp-x1-mini-setup")]
    [InlineData("hc.one-x-player-x2", "oxp-x2-remap")]
    [InlineData("hc.one-x-player3", "oxp-3-remap")]
    [InlineData("hc.one-x-player-x2-mini-pro", "oxp-x2-mini-pro-remap")]
    [InlineData("hc.one-x-player-apex", "oxp-apex-remap")]
    [InlineData("hc.gaming-zone", "zotac-m1-m2-remap")]
    public void For_PicksTheCommandOfTheNearestHcClass(string recordId, string commandId)
    {
        var record = Record(recordId);

        var offered = LabModeCommands.For(record, LabModeStages.Buttons);

        var remap = Assert.Single(offered, command => command.HcClass is not null);
        Assert.Equal(commandId, remap.Id);
    }

    [Fact]
    public void For_LegionGo2_OffersModeAndTouchpadForButtonsAndTheGyroForMotion()
    {
        var record = Record("hc.legion-go-tablet2");

        var buttons = LabModeCommands.For(record, LabModeStages.Buttons).Where(item => item.HcClass is not null);
        var motion = LabModeCommands.For(record, LabModeStages.Motion).Where(item => item.HcClass is not null);

        Assert.Equal(new[] { "legion-go-controller-mode", "legion-go-touchpad-passthrough-off" },
            buttons.Select(command => command.Id));
        Assert.Equal(new[] { "legion-go-gyro-on" }, motion.Select(command => command.Id));
    }

    [Fact]
    public void For_LegionGoS_OffersItsOwnCommands()
    {
        var record = Record("hc.legion-go-sz2");

        Assert.Equal(new[] { "legion-go-s-touchpad-passthrough-off" },
            LabModeCommands.For(record, LabModeStages.Buttons).Where(item => item.HcClass is not null)
                .Select(command => command.Id));
        Assert.Equal(new[] { "legion-go-s-gyro-on" },
            LabModeCommands.For(record, LabModeStages.Motion).Where(item => item.HcClass is not null)
                .Select(command => command.Id));
    }

    [Fact]
    public void For_UnknownDevice_OffersOnlyControllersHcDrivesByUsbId()
    {
        var buttons = LabModeCommands.For(null, LabModeStages.Buttons);
        var motion = LabModeCommands.For(null, LabModeStages.Motion);

        Assert.All(buttons.Concat(motion), command => Assert.Null(command.HcClass));
        Assert.Contains(buttons, command => command.Id == "steam-deck-lizard-off");
        Assert.Contains(motion, command => command.Id == "steam-controller-gyro-on");
        Assert.Contains(motion, command => command.Id == "gamesir-test-mode");
    }

    [Fact]
    public void Matches_ChecksEverySetField()
    {
        var goS = Assert.Single(All, item => item.Id == "legion-go-s-gyro-on").Endpoint;
        var gameSir = Assert.Single(All, item => item.Id == "gamesir-test-mode").Endpoint;

        Assert.True(LabModeCommands.Matches(goS, 0x1A86, 0xE310, 0xFFA0, 0x0001, 3, 65, 65));
        Assert.False(LabModeCommands.Matches(goS, 0x1A86, 0xE310, 0xFFA0, 0x0001, 6, 65, 65));
        Assert.False(LabModeCommands.Matches(goS, 0x1A86, 0xFE00, 0xFFA0, 0x0001, 3, 65, 65));
        Assert.False(LabModeCommands.Matches(goS, 0x1A86, 0xE310, 0xFFA0, 0x0001, 3, 65, 64));
        Assert.True(LabModeCommands.Matches(gameSir, 0x3537, 0x1234, 0x0001, 0x0005, null, 64, 64));
        Assert.False(LabModeCommands.Matches(gameSir, 0x3537, 0x1234, 0x0001, 0x0005, null, 33, 64));
    }

    [Theory]
    [InlineData(@"\\?\hid#vid_1a86&pid_e310&mi_03#8&1&0000#{4d1e55b2}", 3)]
    [InlineData(@"\\?\hid#vid_28de&pid_1205&mi_02&col01#9&2#{4d1e55b2}", 2)]
    [InlineData(@"\\?\hid#vid_3537&pid_1099#7&3#{4d1e55b2}", null)]
    public void InterfaceOf_ReadsTheUsbInterface(string path, int? expected)
    {
        Assert.Equal(expected, LabModeCommands.InterfaceOf(path));
    }

    private static DeviceKnowledgeRecord Record(string id)
    {
        return DeviceKnowledgeBase.Default.Records.Single(record => record.Id == id);
    }

    // HandheldCompanion.Devices.Zotac/GamingZone.cs:847, ported.
    private static ushort ZotacCrc(byte[] data, int start, int end)
    {
        var seed = Fast(0, data[start]);
        for (var i = start + 1; i <= end; i++)
        {
            seed = Fast(seed, data[i]);
        }

        return seed;

        static ushort Fast(ushort seed, byte c)
        {
            var num1 = (uint)((seed ^ c) & 0xFF);
            var num2 = num1 & 0xF;
            var num3 = (int)((num2 << 4) ^ num1);
            var num4 = (uint)num3 >> 4;
            var intermediate = (((uint)(num3 << 1) ^ num4) << 4) ^ num2;
            return (ushort)(((intermediate << 3) ^ num4 ^ (uint)(seed >>> 8)) & 0xFFFF);
        }
    }
}
