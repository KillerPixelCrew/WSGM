using WSGM.Shell;
using static WSGM.Tests.Builders.ControllerSamples;
using CanonicalButtons = LibHandheld.Contracts.CanonicalButtons;

namespace WSGM.Tests.Shell;

public sealed class ControllerWakeGateTests
{
    private readonly List<CanonicalButtons> _withheld = [];
    private bool _accepted = true;
    private int _nudges;

    private ControllerWakeGate Gate()
    {
        return new ControllerWakeGate(_withheld.Add, () =>
        {
            _nudges++;
            return _accepted;
        });
    }

    [Fact]
    public void APressWhileTheDisplayIsLitReachesTheGame()
    {
        var gate = Gate();

        gate.Observe(Sample(CanonicalButtons.A));

        Assert.Empty(_withheld);
        Assert.Equal(0, _nudges);
    }

    [Fact]
    public void TheWakingPressIsWithheldAndWakesTheDisplayOnce()
    {
        var gate = Gate();
        gate.NoteDisplay(true);

        gate.Observe(Sample(CanonicalButtons.A));
        gate.Observe(Sample(CanonicalButtons.A));

        Assert.Equal([CanonicalButtons.A], _withheld);
        Assert.Equal(1, _nudges);
    }

    [Fact]
    public void AMissedDisplayOnNoLongerWithholdsLaterPresses()
    {
        var gate = Gate();
        gate.NoteDisplay(true);
        gate.Observe(Sample(CanonicalButtons.A));
        gate.Observe(Sample(CanonicalButtons.None));

        // Windows never reports the display on. Steam's Quick Access sliders need A to start adjusting.
        gate.Observe(Sample(CanonicalButtons.A));
        gate.Observe(Sample(CanonicalButtons.None));
        gate.Observe(Sample(CanonicalButtons.B));

        Assert.Equal([CanonicalButtons.A], _withheld);
        Assert.Equal(1, _nudges);
    }

    [Fact]
    public void ARefusedWakeLeavesTheDisplayDarkForTheNextPress()
    {
        var gate = Gate();
        gate.NoteDisplay(true);
        _accepted = false;
        gate.Observe(Sample(CanonicalButtons.X));
        gate.Observe(Sample(CanonicalButtons.None));

        _accepted = true;
        gate.Observe(Sample(CanonicalButtons.Y));
        gate.Observe(Sample(CanonicalButtons.None));
        gate.Observe(Sample(CanonicalButtons.Y));

        Assert.Equal([CanonicalButtons.X, CanonicalButtons.Y], _withheld);
        Assert.Equal(2, _nudges);
    }

    [Fact]
    public void ANewDarkDisplayArmsTheNextPressAgain()
    {
        var gate = Gate();
        gate.NoteDisplay(true);
        gate.Observe(Sample(CanonicalButtons.A));
        gate.Observe(Sample(CanonicalButtons.None));

        gate.NoteDisplay(true);
        gate.Observe(Sample(CanonicalButtons.B));

        Assert.Equal([CanonicalButtons.A, CanonicalButtons.B], _withheld);
        Assert.Equal(2, _nudges);
    }

    [Fact]
    public void ADisplayOnFromAnotherWakeLetsTheNextPressThrough()
    {
        var gate = Gate();
        gate.NoteDisplay(true);
        gate.NoteDisplay(false);

        gate.Observe(Sample(CanonicalButtons.A));

        Assert.Empty(_withheld);
        Assert.Equal(0, _nudges);
    }
}
