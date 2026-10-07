using System.Threading;
using WSGM.Device.Sdk.Input;

namespace WSGM.Input;

/// <summary>The managed controller as WSGM's own UI reads it: its latest buttons, polled like an SDL pad.</summary>
/// <remarks>
///     The plugin's sample path writes the newest physical buttons and <see cref="GamepadService" /> reads
///     them on its UI-thread tick, as HC reads the controller state on a timer. Edges, direction
///     auto-repeat and chords therefore behave exactly as they do for an SDL pad, and a control already
///     held when a surface opens produces no press. While the pad is active it is the only pad the UI
///     reads, because SDL sees the same hands through the virtual controller.
/// </remarks>
internal sealed class ManagedUiPad
{
    /// <summary>The canonical-to-UI button map, in canonical order.</summary>
    private static readonly (CanonicalButtons Canonical, GamepadButtons Ui)[] Map =
    [
        (CanonicalButtons.DPadUp, GamepadButtons.DPadUp),
        (CanonicalButtons.DPadDown, GamepadButtons.DPadDown),
        (CanonicalButtons.DPadLeft, GamepadButtons.DPadLeft),
        (CanonicalButtons.DPadRight, GamepadButtons.DPadRight),
        (CanonicalButtons.Menu, GamepadButtons.Start),
        (CanonicalButtons.View, GamepadButtons.Back),
        (CanonicalButtons.LeftStick, GamepadButtons.LeftThumb),
        (CanonicalButtons.RightStick, GamepadButtons.RightThumb),
        (CanonicalButtons.LeftShoulder, GamepadButtons.LeftShoulder),
        (CanonicalButtons.RightShoulder, GamepadButtons.RightShoulder),
        (CanonicalButtons.A, GamepadButtons.A),
        (CanonicalButtons.B, GamepadButtons.B),
        (CanonicalButtons.X, GamepadButtons.X),
        (CanonicalButtons.Y, GamepadButtons.Y),
        (CanonicalButtons.RearPaddle1, GamepadButtons.L4),
        (CanonicalButtons.RearPaddle2, GamepadButtons.R4),
        (CanonicalButtons.RearPaddle3, GamepadButtons.L5),
        (CanonicalButtons.RearPaddle4, GamepadButtons.R5),
        (CanonicalButtons.Guide, GamepadButtons.Steam),
        (CanonicalButtons.QuickAccess, GamepadButtons.QuickAccess),
        (CanonicalButtons.LeftPadClick, GamepadButtons.LeftPadPress),
        (CanonicalButtons.RightPadClick, GamepadButtons.RightPadPress)
    ];

    private volatile bool _active;
    private uint _buttons;

    /// <summary>Whether controller management is active, so this pad replaces SDL for the UI.</summary>
    internal bool IsActive => _active;

    /// <summary>The newest buttons, in the UI vocabulary.</summary>
    internal GamepadButtons Buttons => (GamepadButtons)Volatile.Read(ref _buttons);

    /// <summary>Records one physical sample. Allocation-free; called for every sample.</summary>
    /// <param name="sample">Validated physical sample from any producer thread; only its latest UI button state is retained.</param>
    internal void Publish(CanonicalControllerSample sample)
    {
        Volatile.Write(ref _buttons, (uint)Translate(sample));
    }

    /// <summary>Makes the pad the UI's source, or hands the UI back to SDL with nothing held.</summary>
    /// <param name="active">False clears held buttons before handing UI polling back to SDL; true enables this source.</param>
    internal void SetActive(bool active)
    {
        if (!active)
        {
            Volatile.Write(ref _buttons, 0);
        }

        _active = active;
    }

    /// <summary>Translates one canonical sample into the UI button vocabulary.</summary>
    /// <param name="sample">Canonical buttons, left stick and triggers.</param>
    /// <returns>UI button mask including synthesized stick directions and trigger presses.</returns>
    internal static GamepadButtons Translate(CanonicalControllerSample sample)
    {
        GamepadButtons held = 0;
        foreach (var (canonical, ui) in Map)
        {
            if ((sample.Buttons & canonical) != 0)
            {
                held |= ui;
            }
        }

        held |= UiPadAxes.Stick(sample.LeftStickX, sample.LeftStickY);
        held |= UiPadAxes.Triggers(sample.LeftTrigger, sample.RightTrigger);
        return held;
    }
}
