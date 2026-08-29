using WSGM.Device.Sdk.Input;
using WSGM.Input;

namespace WSGM.Tests;

/// <summary>
/// Which source drives WSGM's own navigation, and what happens to controls held across a change.
/// </summary>
/// <remarks>
/// The swap itself is easy. The hard part is a button held while the source changes: without
/// explicit handling it produces a press edge on the new source that the user never made, or a
/// release that never arrives and leaves the control latched. These pin both.
/// </remarks>
public sealed class UiInputRouterTests
{
    [Fact]
    public void WithNoManagedSourceEverythingComesFromSdl()
    {
        // Which is every current release: controller management off, behaviour unchanged.
        FakeButtonSource sdl = new();
        using UiInputRouter router = new(sdl);
        List<GamepadButtons> seen = [];
        router.ButtonPressed += seen.Add;

        sdl.Press(GamepadButtons.A);

        Assert.Equal(UiInputSource.SdlWithSteamLease, router.Current);
        Assert.Equal([GamepadButtons.A], seen);
    }

    [Fact]
    public void TheSwitchWaitsForTheManagedSourceToActuallyDeliver()
    {
        // Switching on "a managed source exists" rather than "it is delivering" leaves a gap in
        // which nothing is delivering and the UI looks frozen. The first sample is the proof.
        FakeButtonSource sdl = new();
        using UiInputRouter router = new(sdl);

        Assert.Equal(UiInputSource.SdlWithSteamLease, router.Current);
        router.Submit(Sample(CanonicalButtons.None));

        Assert.Equal(UiInputSource.ManagedCanonical, router.Current);
    }

    [Fact]
    public void OnceManagedIsCurrentSdlIsIgnoredRatherThanDoubled()
    {
        FakeButtonSource sdl = new();
        using UiInputRouter router = new(sdl);
        router.Submit(Sample(CanonicalButtons.None));
        List<GamepadButtons> seen = [];
        router.ButtonPressed += seen.Add;

        // SDL keeps polling — it is the fallback and must stay live — but it must not also drive
        // the UI, or every press would arrive twice.
        sdl.Press(GamepadButtons.A);

        Assert.Empty(seen);
    }

    [Fact]
    public void TheManagedSourceReachesControlsSdlCannotSee()
    {
        // The whole reason it exists: SDL has no rear paddles, no Quick Access and no trackpad
        // clicks on a handheld, so WSGM's own UI could not be driven by them at all.
        FakeButtonSource sdl = new();
        using UiInputRouter router = new(sdl);
        router.Submit(Sample(CanonicalButtons.None));
        List<GamepadButtons> seen = [];
        router.ButtonPressed += seen.Add;

        router.Submit(Sample(CanonicalButtons.QuickAccess | CanonicalButtons.RearPaddle1));

        Assert.Equal([GamepadButtons.QuickAccess | GamepadButtons.L4], seen);
    }

    [Fact]
    public void OnlyPressEdgesAreRaisedRatherThanEveryHeldSample()
    {
        // SDL reports a press once; a canonical stream reports a held button on every sample. The
        // two have to agree, or holding A would activate whatever has focus hundreds of times.
        FakeButtonSource sdl = new();
        using UiInputRouter router = new(sdl);
        router.Submit(Sample(CanonicalButtons.None));
        List<GamepadButtons> seen = [];
        router.ButtonPressed += seen.Add;

        router.Submit(Sample(CanonicalButtons.A));
        router.Submit(Sample(CanonicalButtons.A));
        router.Submit(Sample(CanonicalButtons.A));

        Assert.Equal([GamepadButtons.A], seen);
    }

    [Fact]
    public void AControlHeldAcrossTheSwitchMakesNoPressTheUserDidNotMake()
    {
        FakeButtonSource sdl = new();
        using UiInputRouter router = new(sdl);

        // Held before the managed source is current, so the router knows it was already down.
        router.Submit(Sample(CanonicalButtons.A));
        List<GamepadButtons> seen = [];
        router.ButtonPressed += seen.Add;

        // Still held on the first managed sample after the switch: no edge, because the user never
        // pressed it on this source.
        router.Submit(Sample(CanonicalButtons.A));

        Assert.Empty(seen);
    }

    [Fact]
    public void ReleasingAHeldControlArmsItAgainWithoutEmittingTheRelease()
    {
        FakeButtonSource sdl = new();
        using UiInputRouter router = new(sdl);
        router.Submit(Sample(CanonicalButtons.A));
        List<GamepadButtons> seen = [];
        router.ButtonPressed += seen.Add;

        router.Submit(Sample(CanonicalButtons.A));
        router.Submit(Sample(CanonicalButtons.None));
        Assert.Empty(seen);

        // The next genuine press is the user's, and it lands.
        router.Submit(Sample(CanonicalButtons.A));
        Assert.Equal([GamepadButtons.A], seen);
    }

    [Fact]
    public void AHeldControlTheNewSourceCannotSeeStopsBeingSuppressedOnTheBound()
    {
        // Without a bound, a control the incoming source never reports would stay suppressed
        // forever. Here the timeout expires while A is still held, so the next sample releases the
        // suppression and the following press works.
        FakeTimeProvider time = new(DateTimeOffset.UnixEpoch);
        FakeButtonSource sdl = new();
        using UiInputRouter router = new(sdl, time);
        router.Submit(Sample(CanonicalButtons.A));
        List<GamepadButtons> seen = [];
        router.ButtonPressed += seen.Add;

        router.Submit(Sample(CanonicalButtons.A));
        Assert.Empty(seen);

        time.Advance(SourceSwitch.HeldControlTimeout + TimeSpan.FromSeconds(1));
        router.Submit(Sample(CanonicalButtons.A));
        router.Submit(Sample(CanonicalButtons.None));
        router.Submit(Sample(CanonicalButtons.A));

        Assert.Equal([GamepadButtons.A], seen);
    }

    [Fact]
    public void LosingTheManagedSourceFallsBackToSdlWhichNeverStopped()
    {
        FakeButtonSource sdl = new();
        using UiInputRouter router = new(sdl);
        router.Submit(Sample(CanonicalButtons.None));
        Assert.Equal(UiInputSource.ManagedCanonical, router.Current);
        List<GamepadButtons> seen = [];
        router.ButtonPressed += seen.Add;

        router.ManagedSourceLost();
        sdl.Press(GamepadButtons.B);

        Assert.Equal(UiInputSource.SdlWithSteamLease, router.Current);
        Assert.Equal([GamepadButtons.B], seen);
    }

    [Fact]
    public void ComingBackAfterAFallbackDoesNotSwallowTheFirstPress()
    {
        // The managed source's held state is stale once it stops being current. Keeping it would
        // make the first press after it returns produce no edge at all.
        FakeButtonSource sdl = new();
        using UiInputRouter router = new(sdl);
        router.Submit(Sample(CanonicalButtons.A));
        router.Submit(Sample(CanonicalButtons.None));
        router.ManagedSourceLost();
        List<GamepadButtons> seen = [];
        router.ButtonPressed += seen.Add;

        router.Submit(Sample(CanonicalButtons.A));

        Assert.Equal([GamepadButtons.A], seen);
    }

    [Fact]
    public void DisposeStopsDrivingNavigationFromEitherSource()
    {
        FakeButtonSource sdl = new();
        UiInputRouter router = new(sdl);
        List<GamepadButtons> seen = [];
        router.ButtonPressed += seen.Add;

        router.Dispose();
        sdl.Press(GamepadButtons.A);
        router.Submit(Sample(CanonicalButtons.B));

        Assert.Empty(seen);
    }

    private static CanonicalControllerSample Sample(CanonicalButtons buttons) => new()
    {
        Sequence = 1,
        CycleGeneration = 1,
        Timestamp = DateTimeOffset.UnixEpoch,
        Buttons = buttons,
    };

    private sealed class FakeButtonSource : IUiButtonSource
    {
        public event Action<GamepadButtons>? ButtonPressed;

        internal void Press(GamepadButtons buttons) => ButtonPressed?.Invoke(buttons);
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        internal void Advance(TimeSpan delta) => _now += delta;
    }
}
