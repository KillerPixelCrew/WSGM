using WSGM.Device.Sdk.Input;
using static WSGM.Device.Tests.ClawCommands;

namespace WSGM.Device.Msi.Claw8A2Vm.Tests;

[Collection("plugin-trace")]
public sealed class ClawInputTests
{
    [Fact]
    public void Decode_DirectInputReport_MapsMeasuredRearPaddlesAndDiagonalHat()
    {
        var report = new byte[64];
        report[0] = 0x01;
        report[1] = report[2] = report[3] = report[4] = 0x80;
        report[5] = 0x01;
        report[7] = 0x18;

        var sample = ClawControllerCodec.Decode(
            report,
            1,
            CycleGeneration,
            DateTimeOffset.UnixEpoch);

        Assert.True(sample.Buttons.HasFlag(CanonicalButtons.RearPaddle1));
        Assert.True(sample.Buttons.HasFlag(CanonicalButtons.RearPaddle2));
        Assert.True(sample.Buttons.HasFlag(CanonicalButtons.DPadUp));
        Assert.True(sample.Buttons.HasFlag(CanonicalButtons.DPadRight));
    }

    [Theory]
    [InlineData(0x10, CanonicalButtons.RearPaddle1)]
    [InlineData(0x08, CanonicalButtons.RearPaddle2)]
    public void RearPaddlesHaveIndependentMeasuredBits(byte bit, CanonicalButtons expected)
    {
        var report = new byte[64];
        report[0] = 0x01;
        report[7] = bit;
        var sample = ClawControllerCodec.Decode(report, 1, CycleGeneration, DateTimeOffset.UnixEpoch);
        Assert.Equal(expected, sample.Buttons & (CanonicalButtons.RearPaddle1 | CanonicalButtons.RearPaddle2));
    }

    [Fact]
    public void OemButtons_ReachTheVirtualPadAsSteamAndQuickAccess()
    {
        // The Claw's two front buttons are the virtual target's Steam and Quick Access buttons. They
        // are not in the DirectInput report — the firmware sends them as WMI events — so a latch
        // carries them into the sample stream. Without it the virtual Steam Deck had neither button,
        // Steam listed no such controls, and no glyph could exist for a control Steam did not know
        // about.
        ClawOemButtonLatch latch = new();
        var report = new byte[64];
        report[0] = 0x01;
        report[1] = report[2] = report[3] = report[4] = 0x80;
        report[5] = 0x0F;
        var pressed = DateTimeOffset.UnixEpoch;

        latch.Press(CanonicalButtons.Guide, pressed);
        latch.Press(CanonicalButtons.QuickAccess, pressed);
        var held = ClawControllerCodec.Decode(
            report, 1, CycleGeneration, pressed, SampleQuality.Good, latch);

        Assert.True(held.Buttons.HasFlag(CanonicalButtons.Guide));
        Assert.True(held.Buttons.HasFlag(CanonicalButtons.QuickAccess));

        // One event has to become a press AND a release: the firmware never sends the release.
        var released = ClawControllerCodec.Decode(
            report,
            2,
            CycleGeneration,
            pressed + ClawOemButtonLatch.HoldDuration,
            SampleQuality.Good,
            latch);

        Assert.False(released.Buttons.HasFlag(CanonicalButtons.Guide));
        Assert.False(released.Buttons.HasFlag(CanonicalButtons.QuickAccess));
    }

    [Fact]
    public void OemButtons_StaggeredPressesExpireIndependently()
    {
        ClawOemButtonLatch latch = new();
        var first = DateTimeOffset.UnixEpoch;
        var second = first + TimeSpan.FromMilliseconds(80);

        latch.Press(CanonicalButtons.Guide, first);
        latch.Press(CanonicalButtons.QuickAccess, second);

        var betweenExpiries = latch.Current(
            first + ClawOemButtonLatch.HoldDuration + TimeSpan.FromMilliseconds(1));
        Assert.False(betweenExpiries.HasFlag(CanonicalButtons.Guide));
        Assert.True(betweenExpiries.HasFlag(CanonicalButtons.QuickAccess));

        Assert.Equal(
            CanonicalButtons.None,
            latch.Current(second + ClawOemButtonLatch.HoldDuration));
    }
}
