namespace WSGM.Input;

/// <summary>
///     How both UI pad sources turn analog axes into buttons, so the managed pad and an SDL pad
///     navigate the same way.
/// </summary>
internal static class UiPadAxes
{
    /// <summary>How far the stick travels, as a fraction of full deflection, before it counts as a direction.</summary>
    /// <remarks>16000 of SDL's 32767, the deadzone the SDL path has always folded at.</remarks>
    private const float Deadzone = 16000f / 32767f;

    /// <summary>How far a trigger travels, as a fraction of full pull, before it counts as a press.</summary>
    /// <remarks>
    ///     Half travel, the managed pad's shipped value. The SDL path used to press at 8000/32767; a
    ///     full pull passes either, so one value serves both sources.
    /// </remarks>
    private const float TriggerThreshold = 0.5f;

    /// <summary>Folds a stick position into D-pad directions.</summary>
    /// <param name="x">Horizontal position, from -1 to 1, positive right.</param>
    /// <param name="y">Vertical position, from -1 to 1, positive up.</param>
    /// <returns>The directions past the deadzone; none while the stick rests.</returns>
    internal static GamepadButtons Stick(float x, float y)
    {
        GamepadButtons held = 0;
        if (y > Deadzone)
        {
            held |= GamepadButtons.DPadUp;
        }
        else if (y < -Deadzone)
        {
            held |= GamepadButtons.DPadDown;
        }

        if (x < -Deadzone)
        {
            held |= GamepadButtons.DPadLeft;
        }
        else if (x > Deadzone)
        {
            held |= GamepadButtons.DPadRight;
        }

        return held;
    }

    /// <summary>Turns trigger positions into trigger buttons.</summary>
    /// <param name="left">Left trigger pull, from 0 to 1.</param>
    /// <param name="right">Right trigger pull, from 0 to 1.</param>
    /// <returns>The triggers pulled past the threshold.</returns>
    internal static GamepadButtons Triggers(float left, float right)
    {
        GamepadButtons held = 0;
        if (left >= TriggerThreshold)
        {
            held |= GamepadButtons.LeftTrigger;
        }

        if (right >= TriggerThreshold)
        {
            held |= GamepadButtons.RightTrigger;
        }

        return held;
    }
}
