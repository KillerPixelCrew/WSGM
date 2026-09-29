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
    /// <summary>How far a trigger travels before it counts as a press.</summary>
    /// <remarks>
    ///     Not the SDL path's threshold: SdlGamepads synthesizes its trigger buttons at 8000/32767
    ///     (about 0.24). The difference is long-shipped behavior; align only with device
    ///     re-verification.
    /// </remarks>
    private const float TriggerThreshold = 0.5f;

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
    internal void Publish(CanonicalControllerSample sample)
    {
        Volatile.Write(ref _buttons, (uint)Translate(sample));
    }

    /// <summary>Makes the pad the UI's source, or hands the UI back to SDL with nothing held.</summary>
    internal void SetActive(bool active)
    {
        if (!active)
        {
            Volatile.Write(ref _buttons, 0);
        }

        _active = active;
    }

    /// <summary>Translates one canonical sample into the UI button vocabulary.</summary>
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

        if (sample.LeftTrigger >= TriggerThreshold)
        {
            held |= GamepadButtons.LeftTrigger;
        }

        if (sample.RightTrigger >= TriggerThreshold)
        {
            held |= GamepadButtons.RightTrigger;
        }

        return held;
    }
}
