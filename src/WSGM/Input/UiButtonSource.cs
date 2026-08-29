using System;
using WSGM.Device.Sdk.Input;

namespace WSGM.Input;

/// <summary>
/// Where WSGM's own navigation gets its button presses from.
/// </summary>
/// <remarks>
/// One event, because that is the entire coupling every navigation surface has had to
/// <see cref="GamepadService"/>. Making it an interface is what lets the managed canonical stream
/// stand in for SDL without any surface knowing which one it is talking to.
/// </remarks>
public interface IUiButtonSource
{
    /// <summary>Raised on the press edge of each button, on the UI thread.</summary>
    event Action<GamepadButtons>? ButtonPressed;
}

/// <summary>
/// Turns the plugin's canonical samples into the press edges WSGM's navigation consumes.
/// </summary>
/// <remarks>
/// Edges rather than state, because that is what navigation acts on and it is where the two sources
/// have to agree: SDL reports a press once, and a canonical stream reports a held button on every
/// sample. Deriving edges here rather than in each surface is what keeps them interchangeable.
/// <para>
/// This is the source that makes the rear paddles, the Quick Access button and the trackpad clicks
/// reachable by WSGM's own UI at all — SDL cannot see them on a handheld, which is the reason the
/// managed source exists.
/// </para>
/// </remarks>
public sealed class CanonicalButtonSource : IUiButtonSource
{
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

        // Rear paddle numbering follows the UI vocabulary's own layout names: 1 and 2 are the upper
        // pair (L4/R4), 3 and 4 the lower pair (L5/R5) a Steam Deck has and the Claw does not.
        (CanonicalButtons.RearPaddle1, GamepadButtons.L4),
        (CanonicalButtons.RearPaddle2, GamepadButtons.R4),
        (CanonicalButtons.RearPaddle3, GamepadButtons.L5),
        (CanonicalButtons.RearPaddle4, GamepadButtons.R5),
        (CanonicalButtons.Guide, GamepadButtons.Steam),
        (CanonicalButtons.QuickAccess, GamepadButtons.QuickAccess),
        (CanonicalButtons.LeftPadClick, GamepadButtons.LeftPadPress),
        (CanonicalButtons.RightPadClick, GamepadButtons.RightPadPress),
    ];

    /// <summary>How far a trigger travels before it counts as a press.</summary>
    /// <remarks>
    /// The same threshold the SDL path synthesizes its trigger buttons at, so a trigger does not
    /// become easier or harder to activate depending on which source is current.
    /// </remarks>
    private const float TriggerThreshold = 0.5f;

    private GamepadButtons _previous;

    /// <inheritdoc/>
    public event Action<GamepadButtons>? ButtonPressed;

    /// <summary>The buttons currently held, in the UI vocabulary.</summary>
    /// <remarks>
    /// Read by the router to carry held controls across a source switch, which is the one thing that
    /// cannot be reconstructed from edges alone.
    /// </remarks>
    public GamepadButtons Held => _previous;

    /// <summary>Feeds one canonical sample and raises the edges it produced.</summary>
    /// <param name="sample">The sample the plugin published.</param>
    public void Submit(CanonicalControllerSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        GamepadButtons current = Translate(sample);
        GamepadButtons pressed = current & ~_previous;
        _previous = current;
        if (pressed != 0)
        {
            ButtonPressed?.Invoke(pressed);
        }
    }

    /// <summary>Forgets what is held, so the next sample produces edges for everything on it.</summary>
    /// <remarks>
    /// Used when this source stops being current. Without it, a button held at the moment the source
    /// was dropped stays recorded as held, and the press the user makes after coming back produces
    /// no edge at all.
    /// </remarks>
    public void Reset() => _previous = 0;

    /// <summary>Translates one sample into the UI button vocabulary.</summary>
    /// <param name="sample">The sample to translate.</param>
    /// <returns>The buttons held, in the UI vocabulary.</returns>
    public static GamepadButtons Translate(CanonicalControllerSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        GamepadButtons held = 0;
        foreach ((CanonicalButtons canonical, GamepadButtons ui) in Map)
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
