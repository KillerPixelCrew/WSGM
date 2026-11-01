using System;
using WSGM.Core;
using CanonicalButtons = LibHandheld.Contracts.CanonicalButtons;
using CanonicalControllerSample = LibHandheld.Contracts.CanonicalControllerSample;

namespace WSGM.Shell;

/// <summary>Turns a face-button press on the managed controller into a display wake while the display is dark.</summary>
/// <remarks>
///     The waking press is withheld from the game until released, so waking Big Picture does not also
///     activate whatever has focus. The display counts as lit again as soon as Windows accepts the wake
///     input, so at most that one press is withheld: a display-on notification that never arrives, as
///     can happen around standby, cannot keep later presses from the game.
/// </remarks>
internal sealed class ControllerWakeGate
{
    private const CanonicalButtons FaceButtons =
        CanonicalButtons.A | CanonicalButtons.B | CanonicalButtons.X | CanonicalButtons.Y;

    private readonly Func<bool> _nudge;
    private readonly Action<CanonicalButtons> _withhold;
    private volatile bool _dark;

    /// <summary>Only touched on the controller's single sample drain.</summary>
    private CanonicalButtons _lastFace;

    /// <param name="withhold">Keeps controls from the game until each is released.</param>
    /// <param name="nudge">Injects the wake input and reports whether Windows accepted it.</param>
    internal ControllerWakeGate(Action<CanonicalButtons> withhold, Func<bool> nudge)
    {
        _withhold = withhold ?? throw new ArgumentNullException(nameof(withhold));
        _nudge = nudge ?? throw new ArgumentNullException(nameof(nudge));
    }

    /// <summary>Records this session's display state.</summary>
    /// <param name="off">Whether the display is off.</param>
    internal void NoteDisplay(bool off)
    {
        _dark = off;
    }

    /// <summary>Wakes a dark display on a new face-button press, withholding that press from the game.</summary>
    /// <param name="sample">A physical sample, observed before it is routed.</param>
    internal void Observe(CanonicalControllerSample sample)
    {
        var face = sample.Buttons & FaceButtons;
        var pressed = face & ~_lastFace;
        _lastFace = face;
        if (pressed is CanonicalButtons.None || !_dark)
        {
            return;
        }

        // Raised before the routing decision, so this very sample is already withheld.
        _withhold(face);
        if (_nudge())
        {
            _dark = false;
            Log.Change("display.controller-wake", "Display off: a controller face button woke the display.");
        }
        else
        {
            Log.Change("display.controller-wake",
                "Display off: a controller face button was pressed, but Windows refused the wake input.");
        }
    }
}
