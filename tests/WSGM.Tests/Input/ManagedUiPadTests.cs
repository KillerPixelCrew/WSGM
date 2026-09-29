using WSGM.Device.Sdk.Input;
using WSGM.Input;
using WSGM.Shell;

namespace WSGM.Tests.Input;

public sealed class ManagedUiPadTests
{
    [Fact]
    public void ThePadReportsTheNewestButtonsInTheUiVocabulary()
    {
        ManagedUiPad pad = new();
        pad.SetActive(true);

        pad.Publish(Sample(CanonicalButtons.A | CanonicalButtons.Guide));
        Assert.Equal(GamepadButtons.A | GamepadButtons.Steam, pad.Buttons);

        pad.Publish(Sample(CanonicalButtons.None));
        Assert.Equal((GamepadButtons)0, pad.Buttons);
    }

    [Fact]
    public void TheManagedPadReachesControlsSdlCannotSee()
    {
        var held = ManagedUiPad.Translate(Sample(
            CanonicalButtons.RearPaddle1 | CanonicalButtons.QuickAccess | CanonicalButtons.RightPadClick));

        Assert.Equal(GamepadButtons.L4 | GamepadButtons.QuickAccess | GamepadButtons.RightPadPress, held);
    }

    [Fact]
    public void TriggersCountAsPressedFromHalfway()
    {
        var held = ManagedUiPad.Translate(Sample(CanonicalButtons.None) with
        {
            LeftTrigger = 0.49f,
            RightTrigger = 0.5f
        });

        Assert.Equal(GamepadButtons.RightTrigger, held);
    }

    [Fact]
    public void DeactivatingHandsTheUiBackToSdlWithNothingHeld()
    {
        ManagedUiPad pad = new();
        pad.SetActive(true);
        pad.Publish(Sample(CanonicalButtons.A));

        pad.SetActive(false);

        Assert.False(pad.IsActive);
        Assert.Equal((GamepadButtons)0, pad.Buttons);
    }

    [Fact]
    public void ReleasingAnUnknownSurfaceDoesNotReportCaptureEnded()
    {
        UiCaptureState capture = new();

        Assert.False(capture.Release("overlay"));
    }

    [Fact]
    public void NestedSurfacesKeepCaptureUntilTheLastKnownClaimCloses()
    {
        UiCaptureState capture = new();
        capture.Claim("overlay", CanonicalButtons.Guide);
        capture.Claim("settings", CanonicalButtons.None);

        Assert.False(capture.Release("overlay"));
        Assert.True(capture.IsCaptured);
        Assert.True(capture.Release("settings"));
        Assert.False(capture.IsCaptured);
    }

    [Fact]
    public void AControlStillHeldWhenCaptureClosesCannotLeakIntoTheGame()
    {
        UiCaptureState capture = new();
        capture.Claim("overlay", CanonicalButtons.None);
        Assert.True(capture.Withholds(CanonicalButtons.A));

        Assert.True(capture.Release("overlay"));
        Assert.True(capture.Withholds(CanonicalButtons.A));
        Assert.False(capture.Withholds(CanonicalButtons.None));
        Assert.False(capture.Withholds(CanonicalButtons.A));
    }

    private static CanonicalControllerSample Sample(CanonicalButtons buttons)
    {
        return new CanonicalControllerSample
        {
            Timestamp = DateTimeOffset.UtcNow,
            Buttons = buttons
        };
    }
}
