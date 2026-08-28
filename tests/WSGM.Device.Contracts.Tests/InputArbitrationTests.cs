using WSGM.Device.Contracts.Input;

namespace WSGM.Device.Contracts.Tests;

/// <summary>
/// The executable specification of UI capture and source switching — the two places where input can
/// leak into the game behind an open overlay, or stick after a source changes.
/// </summary>
public class InputArbitrationTests
{
    [Fact]
    public void Claim_IsReferenceCountedSoNestedSurfacesDoNotReleaseEarly()
    {
        // The overlay can open the taskbar, which can open a picker. Releasing on the first close
        // would hand input back to the game while a WSGM surface is still on screen.
        UiCaptureState capture = new();

        Assert.True(capture.Claim("overlay", CanonicalButtons.None));
        Assert.False(capture.Claim("taskbar", CanonicalButtons.None));
        Assert.Equal(2, capture.Depth);

        Assert.False(capture.Release("taskbar"));
        Assert.True(capture.IsCaptured);

        Assert.True(capture.Release("overlay"));
        Assert.False(capture.IsCaptured);
    }

    [Fact]
    public void FilterForUi_SuppressesAButtonHeldWhenTheSurfaceOpened()
    {
        // Otherwise the chord that opened the overlay immediately activates whatever control now has
        // focus underneath it.
        UiCaptureState capture = new();
        capture.Claim("overlay", CanonicalButtons.A);

        Assert.Equal(CanonicalButtons.None, capture.FilterForUi(CanonicalButtons.A));
    }

    [Fact]
    public void FilterForUi_ReleasesSuppressionOnceTheButtonComesUp()
    {
        UiCaptureState capture = new();
        capture.Claim("overlay", CanonicalButtons.A);

        capture.FilterForUi(CanonicalButtons.None);

        Assert.Equal(CanonicalButtons.A, capture.FilterForUi(CanonicalButtons.A));
    }

    [Fact]
    public void FilterForUi_ClearsSuppressionPerButtonRatherThanAllAtOnce()
    {
        // A user holding two controls at open regains each one independently.
        UiCaptureState capture = new();
        capture.Claim("overlay", CanonicalButtons.A | CanonicalButtons.B);

        CanonicalButtons visible = capture.FilterForUi(CanonicalButtons.B);

        Assert.Equal(CanonicalButtons.None, visible);
        Assert.Equal(CanonicalButtons.B, capture.SuppressedButtons);
    }

    [Fact]
    public void FilterForUi_PassesThroughAButtonPressedAfterCaptureBegan()
    {
        UiCaptureState capture = new();
        capture.Claim("overlay", CanonicalButtons.A);

        Assert.Equal(CanonicalButtons.B,
            capture.FilterForUi(CanonicalButtons.A | CanonicalButtons.B));
    }

    [Fact]
    public void CanResumeForwarding_IsRefusedWhileASurfaceIsStillOpen()
    {
        UiCaptureState capture = new();
        capture.Claim("overlay", CanonicalButtons.None);

        Assert.False(capture.CanResumeForwarding(CanonicalButtons.None));
    }

    [Fact]
    public void CanResumeForwarding_WaitsForEveryUiUsedControlToBeReleased()
    {
        // Resuming while a button is still down delivers the closing press of a WSGM surface into the
        // game as a fresh input.
        UiCaptureState capture = new();
        capture.Claim("overlay", CanonicalButtons.A);
        capture.Release("overlay");

        Assert.False(capture.CanResumeForwarding(CanonicalButtons.A));
        Assert.True(capture.CanResumeForwarding(CanonicalButtons.None));
    }

    [Fact]
    public void Decide_SwitchesOnlyWhenTheCandidateIsAlreadyDelivering()
    {
        // Switching on "it exists" rather than "it works" produces a gap where no source delivers and
        // the UI appears frozen.
        Assert.Equal(SourceSwitchDecision.Switch, SourceArbitration.Decide(true, true));
        Assert.Equal(SourceSwitchDecision.KeepCurrent, SourceArbitration.Decide(true, false));
    }

    [Fact]
    public void Decide_WithNeitherSourceUsable_KeepsKeyboardAndTouch()
    {
        Assert.Equal(SourceSwitchDecision.FallBackToKeyboardAndTouch,
            SourceArbitration.Decide(false, false));
    }

    [Fact]
    public void Decide_PromotesAHealthyCandidateEvenWhenTheCurrentSourceDied()
    {
        Assert.Equal(SourceSwitchDecision.Switch, SourceArbitration.Decide(false, true));
    }

    [Fact]
    public void Suppressed_KeepsAControlHeldAcrossTheSwitchSuppressed()
    {
        CanonicalButtons suppressed = SourceArbitration.Suppressed(
            heldAtSwitch: CanonicalButtons.A,
            observedNow: CanonicalButtons.A,
            elapsed: TimeSpan.FromMilliseconds(100));

        Assert.Equal(CanonicalButtons.A, suppressed);
    }

    [Fact]
    public void Suppressed_ReleasesOnceTheIncomingSourceSeesTheControlUp()
    {
        Assert.Equal(CanonicalButtons.None, SourceArbitration.Suppressed(
            heldAtSwitch: CanonicalButtons.A,
            observedNow: CanonicalButtons.None,
            elapsed: TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public void Suppressed_ExpiresForAControlTheIncomingSourceCannotSeeAtAll()
    {
        // A managed source exposes rear paddles that the SDL fallback cannot see, so their release
        // would never be observed and they would stay suppressed forever.
        CanonicalButtons suppressed = SourceArbitration.Suppressed(
            heldAtSwitch: CanonicalButtons.RearPaddle1,
            observedNow: CanonicalButtons.RearPaddle1,
            elapsed: SourceSwitch.HeldControlTimeout);

        Assert.Equal(CanonicalButtons.None, suppressed);
    }

    [Theory]
    [InlineData(UiInputSource.ManagedCanonical, UiInputSource.SdlWithSteamLease)]
    [InlineData(UiInputSource.SdlWithSteamLease, UiInputSource.ManagedCanonical)]
    [InlineData(UiInputSource.ManagedCanonical, UiInputSource.ManagedCanonical)]
    public void RequiresNeutralOutput_WheneverTheManagedSourceIsInvolved(
        UiInputSource from,
        UiInputSource to)
    {
        // Whatever was held at the moment of the switch would otherwise stay latched in the game for
        // as long as the swap takes.
        Assert.True(SourceArbitration.RequiresNeutralOutput(from, to));
    }

    [Fact]
    public void RequiresNeutralOutput_IsUnnecessaryBetweenUnmanagedSources()
    {
        Assert.False(SourceArbitration.RequiresNeutralOutput(
            UiInputSource.SdlWithSteamLease, UiInputSource.None));
    }

    [Fact]
    public void Neutral_HasNothingHeldAndEveryAxisCentred()
    {
        CanonicalControllerSample neutral = CanonicalControllerSample.Neutral(
            sequence: 1, deviceGeneration: 7, DateTimeOffset.UnixEpoch);

        Assert.Equal(CanonicalButtons.None, neutral.Buttons);
        Assert.Equal(0f, neutral.LeftStickX);
        Assert.Equal(0f, neutral.LeftStickY);
        Assert.Equal(0f, neutral.RightStickX);
        Assert.Equal(0f, neutral.RightStickY);
        Assert.Equal(0f, neutral.LeftTrigger);
        Assert.Equal(0f, neutral.RightTrigger);
        Assert.Null(neutral.Motion);
    }
}
