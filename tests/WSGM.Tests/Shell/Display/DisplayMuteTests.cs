using WSGM.Shell;

namespace WSGM.Tests.Shell;

public class DisplayMuteTests
{
    [Fact]
    public void DownloadCompletionRestoreDelay_IsTenSeconds()
    {
        Assert.Equal(
            TimeSpan.FromSeconds(10),
            DisplayMuteDecider.DownloadCompletionRestoreDelay);
    }

    [Fact]
    public void Reconcile_DarkDisplayWithActiveDownload_Mutes()
    {
        var action = DisplayMuteDecider.Reconcile(
            true,
            true,
            true,
            false);

        Assert.Equal(DisplayMuteAction.Mute, action);
    }

    [Fact]
    public void Reconcile_DarkDisplayWithoutActiveDownload_DoesNothing()
    {
        var action = DisplayMuteDecider.Reconcile(
            true,
            true,
            false,
            false);

        Assert.Equal(DisplayMuteAction.NoChange, action);
    }

    [Fact]
    public void Reconcile_LitDisplayWithActiveDownload_DoesNothing()
    {
        var action = DisplayMuteDecider.Reconcile(
            true,
            false,
            true,
            false);

        Assert.Equal(DisplayMuteAction.NoChange, action);
    }

    [Fact]
    public void Reconcile_DisabledSettingWithDarkDownload_DoesNothing()
    {
        var action = DisplayMuteDecider.Reconcile(
            false,
            true,
            true,
            false);

        Assert.Equal(DisplayMuteAction.NoChange, action);
    }

    [Fact]
    public void Reconcile_LastDownloadFinishesWhileDark_DelaysRestore()
    {
        var action = DisplayMuteDecider.Reconcile(
            true,
            true,
            false,
            true);

        Assert.Equal(DisplayMuteAction.DelayRestore, action);
    }

    [Fact]
    public void Reconcile_DisplayReturnsWhileMuted_RestoresImmediately()
    {
        var action = DisplayMuteDecider.Reconcile(
            true,
            false,
            true,
            true);

        Assert.Equal(DisplayMuteAction.Restore, action);
    }

    [Fact]
    public void Reconcile_DownloadRestartsDuringDelayedRestore_KeepsMute()
    {
        var action = DisplayMuteDecider.Reconcile(
            true,
            true,
            true,
            true);

        Assert.Equal(DisplayMuteAction.NoChange, action);
    }

    [Fact]
    public void Reconcile_SettingDisabledWhileMuted_RestoresImmediately()
    {
        var action = DisplayMuteDecider.Reconcile(
            false,
            true,
            true,
            true);

        Assert.Equal(DisplayMuteAction.Restore, action);
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
