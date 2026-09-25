namespace WSGM.DeviceLab.Wizard;

/// <summary>What a controller answer means.</summary>
internal enum LabRumblePadAnswer
{
    /// <summary>No answer yet.</summary>
    None,

    /// <summary>A was pressed and released: felt it.</summary>
    Felt,

    /// <summary>B was pressed and released: did not feel it.</summary>
    NotFelt
}

/// <summary>
///     Lets the tester answer "Felt it" with A and "Didn't feel it" with B on the one connected XInput
///     controller, so they need not let go of the device.
/// </summary>
/// <remarks>
///     A button counts on its release, and only after A and B were both seen up since the question
///     appeared, so a press held from before never answers. With no controller or more than one, the
///     buttons are ignored, because it would be unclear whose press it is. Pressing A and B together
///     answers nothing.
/// </remarks>
internal sealed class LabRumblePad
{
    /// <summary>XInput's A button bit.</summary>
    public const ushort ButtonA = 0x1000;

    /// <summary>XInput's B button bit.</summary>
    public const ushort ButtonB = 0x2000;

    private bool _armed;
    private ushort _held;

    /// <summary>Feeds one poll of every XInput slot.</summary>
    /// <param name="connected">How many slots have a controller.</param>
    /// <param name="buttons">The buttons of the connected controller, when exactly one is.</param>
    /// <returns>The answer completed by this poll, or <see cref="LabRumblePadAnswer.None" />.</returns>
    public LabRumblePadAnswer Feed(int connected, ushort buttons)
    {
        if (connected != 1)
        {
            _armed = false;
            _held = 0;
            return LabRumblePadAnswer.None;
        }

        var pressed = (ushort)(buttons & (ButtonA | ButtonB));
        if (!_armed)
        {
            _armed = pressed == 0;
            return LabRumblePadAnswer.None;
        }

        if (pressed != 0)
        {
            _held |= pressed;
            return LabRumblePadAnswer.None;
        }

        var released = _held;
        _held = 0;
        return released switch
        {
            ButtonA => LabRumblePadAnswer.Felt,
            ButtonB => LabRumblePadAnswer.NotFelt,
            _ => LabRumblePadAnswer.None
        };
    }

    /// <summary>Whether exactly one XInput controller is connected, so A and B can answer.</summary>
    public static bool Available()
    {
        return Read().Connected == 1;
    }

    /// <summary>Polls every XInput slot once and feeds the result.</summary>
    /// <returns>The answer completed by this poll, or <see cref="LabRumblePadAnswer.None" />.</returns>
    public LabRumblePadAnswer Poll()
    {
        var (connected, buttons) = Read();
        return Feed(connected, buttons);
    }

    private static (int Connected, ushort Buttons) Read()
    {
        var connected = 0;
        ushort buttons = 0;
        for (uint slot = 0; slot < 4; slot++)
        {
            if (LabRumbleNative.XInputButtons(slot, out var state))
            {
                connected++;
                buttons = state;
            }
        }

        return (connected, buttons);
    }
}
