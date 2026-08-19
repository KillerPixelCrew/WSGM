using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Tests;

public class DisplayMuteTests
{
    [Fact]
    public void ActionFor_DisplayOff_Mutes()
    {
        Assert.Equal(DisplayMuteAction.Mute, DisplayMuteDecider.ActionFor(DisplayMuteDecider.DisplayOff));
    }

    [Theory]
    [InlineData(DisplayMuteDecider.DisplayOn)]
    [InlineData(DisplayMuteDecider.DisplayDimmed)]
    public void ActionFor_LitDisplay_Restores(int state)
    {
        Assert.Equal(DisplayMuteAction.Restore, DisplayMuteDecider.ActionFor(state));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(99)]
    [InlineData(-1)]
    public void ActionFor_UnknownState_RestoresRatherThanLeavingTheDeviceSilent(int state)
    {
        Assert.Equal(DisplayMuteAction.Restore, DisplayMuteDecider.ActionFor(state));
    }

    [Fact]
    public void MayReportDark_SessionDisplayStatus_IsTrusted()
    {
        Assert.True(DisplayMuteDecider.MayReportDark(DisplayStateSource.Session));
    }

    [Theory]
    [InlineData(DisplayStateSource.Console)]
    [InlineData(DisplayStateSource.LegacyMonitor)]
    public void MayReportDark_RedundantWakeSources_NeverStartAMute(DisplayStateSource source)
    {
        // They exist so a missed wake still restores; a cross-session or stale "off" from
        // them must not be able to silence a device whose own display is lit.
        Assert.False(DisplayMuteDecider.MayReportDark(source));
    }

    [Fact]
    public void HasInputSince_NoNewInput_IsFalse()
    {
        Assert.False(DisplayMuteDecider.HasInputSince(1_000, 1_000));
    }

    [Fact]
    public void HasInputSince_LaterTick_IsTrue()
    {
        Assert.True(DisplayMuteDecider.HasInputSince(1_000, 1_001));
    }

    [Fact]
    public void HasInputSince_TickCountWrapAround_StillDetectsNewInput()
    {
        // GetLastInputInfo reports a 32-bit tick count that wraps roughly every 49 days;
        // a plain > comparison would report "no input" for the whole wrap.
        Assert.True(DisplayMuteDecider.HasInputSince(uint.MaxValue - 500, 250));
    }

    [Fact]
    public void HasInputSince_StaleReadBeforeTheBaseline_IsFalse()
    {
        Assert.False(DisplayMuteDecider.HasInputSince(5_000, 4_000));
    }
}
