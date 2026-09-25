using System.Text.Json;
using WSGM.DeviceLab.Capture.Live;
using WSGM.DeviceLab.Knowledge;
using WSGM.DeviceLab.Wizard;

namespace WSGM.DeviceLab.Tests.Wizard;

public sealed class LabRumbleRoutesTests
{
    private const string SecretPath =
        @"\\?\hid#vid_0b05&pid_1b4c&mi_02#7&2d7f1a3&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";

    [Fact]
    public void Claw_RumbleUsesTheGamepadCollectionAndPluginReport()
    {
        var record = DeviceKnowledgeBase.Default.Records.Single(item => item.Id == "wsgm.claw-8-a2vm");
        LabRumbleHidEndpoint[] endpoints =
        [
            new(0x0DB0, 0x1902, 1, 0x0001, 0x0005, 64, "gamepad"),
            new(0x0DB0, 0x1902, 1, 0xFFF0, 0x0040, 64, "mcu")
        ];
        var discovery = LabRumbleRoutes.DiscoverHid(record, _ => endpoints);
        var route = Assert.Single(discovery.Routes);
        Assert.Equal("gamepad", route.Target);
        var report = route.Layout!.Encode(new LabRumbleFrame(100, 0), 64);
        Assert.Equal(0x05, report[0]);
        Assert.Equal(0x01, report[1]);
        Assert.Equal(0, report[4]);
        Assert.Equal(255, report[5]);
    }

    [Fact]
    public void UnknownMotorRoutesAreRefused()
    {
        LabRumbleLog log = new();

        Assert.Throws<InvalidOperationException>(() =>
            LabRumbleRoutes.Open(new LabRumbleRoute("bogus:0", "bogus", "Bogus", "Bogus"), log));
        Assert.Throws<InvalidOperationException>(() =>
            LabRumbleRoutes.Open(new LabRumbleRoute("hid:x", LabRumbleRoutes.HidKind, "Device report", "No layout"),
                log));
        Assert.Throws<InvalidOperationException>(() =>
            LabRumbleRoutes.Open(
                new LabRumbleRoute("xinput:7", LabRumbleRoutes.XInputKind, "XInput", "Slot 7") { Target = "7" }, log));
        Assert.Empty(log.Snapshot());
    }

    [Fact]
    public void AllyX_WritesOnlyToTheRecordedMotorCollections()
    {
        LabRumbleHidEndpoint[] endpoints =
        [
            Endpoint(0xFF31, 0x0080, 64, "vendor"),
            Endpoint(0x0001, 0x0005, 64, "gamepad"),
            Endpoint(0x000F, 0x0021, 9, "pid-21"),
            Endpoint(0x000F, 0x0002, 8, "pid-02-short"),
            Endpoint(0x0001, 0x0006, 64, "keyboard")
        ];

        var discovery = LabRumbleRoutes.DiscoverHid(AllyX(), _ => endpoints);

        var targets = discovery.Routes.Select(route => route.Target).Order(StringComparer.Ordinal).ToArray();
        string[] expected = [DevicePath("gamepad"), DevicePath("pid-21")];
        Assert.Equal(expected, targets);
        Assert.All(discovery.Routes, route => Assert.Equal(LabRumbleRoutes.HidKind, route.Kind));
        Assert.Contains(discovery.Notes, note => note.Contains("000F:0002", StringComparison.Ordinal));
    }

    [Fact]
    public void AllyX_IgnoresOtherVendorsAndProducts()
    {
        LabRumbleHidEndpoint[] endpoints =
        [
            new(0x045E, 0x1B4C, 1, 0x0001, 0x0005, 64, DevicePath("other-vendor")),
            new(0x0B05, 0x1ABE, 1, 0x0001, 0x0005, 64, DevicePath("other-product"))
        ];

        Assert.Empty(LabRumbleRoutes.DiscoverHid(AllyX(), _ => endpoints).Routes);
    }

    [Fact]
    public void UnreviewedOrMissingRecords_OfferNoHidRoute()
    {
        var record = AllyX() with { Status = DeviceKnowledgeStatus.Extracted };

        Assert.Empty(LabRumbleRoutes.DiscoverHid(record, _ => [Endpoint(1, 5, 64, "gamepad")]).Routes);
        Assert.Empty(LabRumbleRoutes.DiscoverHid(null, _ => [Endpoint(1, 5, 64, "gamepad")]).Routes);
    }

    [Fact]
    public void DevicePaths_NeverReachTheEvidence()
    {
        LabRumbleHidEndpoint[] endpoints =
        [
            new(0x0B05, 0x1B4C, 1, 0x0001, 0x0005, 64, SecretPath),
            new(0x0B05, 0x1B4C, 1, 0xFF31, 0x0080, 64, SecretPath + "-vendor")
        ];
        var discovery = LabRumbleRoutes.DiscoverHid(AllyX(), _ => endpoints);
        LabRumbleRoute pad = new("wgi:0", LabRumbleRoutes.GamingInputKind, "Windows.Gaming.Input", "Gamepad 1")
        {
            Target = SecretPath
        };

        Assert.NotEmpty(discovery.Routes);
        var json = JsonSerializer.Serialize(new { discovery, Pad = pad }, LabProject.JsonOptions);
        Assert.DoesNotContain("hid#", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("4d1e55b2", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("0D 0F 00 00", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Pad_AnswersOnReleaseOnlyWithOneController()
    {
        LabRumblePad pad = new();

        // A held from before the question never answers.
        Assert.Equal(LabRumblePadAnswer.None, pad.Feed(1, LabRumblePad.ButtonA));
        Assert.Equal(LabRumblePadAnswer.None, pad.Feed(1, 0));
        Assert.Equal(LabRumblePadAnswer.None, pad.Feed(1, LabRumblePad.ButtonA));
        Assert.Equal(LabRumblePadAnswer.Felt, pad.Feed(1, 0));
        Assert.Equal(LabRumblePadAnswer.None, pad.Feed(1, LabRumblePad.ButtonB));
        Assert.Equal(LabRumblePadAnswer.NotFelt, pad.Feed(1, 0));

        // Both together answer nothing; two controllers disarm it.
        Assert.Equal(LabRumblePadAnswer.None, pad.Feed(1, LabRumblePad.ButtonA | LabRumblePad.ButtonB));
        Assert.Equal(LabRumblePadAnswer.None, pad.Feed(1, 0));
        Assert.Equal(LabRumblePadAnswer.None, pad.Feed(2, LabRumblePad.ButtonA));
        Assert.Equal(LabRumblePadAnswer.None, pad.Feed(1, LabRumblePad.ButtonA));
        Assert.Equal(LabRumblePadAnswer.None, pad.Feed(1, 0));
    }

    private static DeviceKnowledgeRecord AllyX()
    {
        return DeviceKnowledgeBase.Default.Records.Single(record => record.Id == "wsgm.rog-ally-x");
    }

    private static LabRumbleHidEndpoint Endpoint(ushort page, ushort usage, ushort output, string name)
    {
        return new LabRumbleHidEndpoint(0x0B05, 0x1B4C, 1, page, usage, output, DevicePath(name));
    }

    private static string DevicePath(string name)
    {
        return $@"\\?\hid#test-{name}";
    }
}
