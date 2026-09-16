using WSGM.Input;

namespace WSGM.Tests.Fakes;

/// <summary>A UI button source a test presses by hand.</summary>
internal sealed class FakeButtonSource : IUiButtonSource
{
    public event Action<GamepadButtons>? ButtonPressed;

    internal void Press(GamepadButtons buttons)
    {
        ButtonPressed?.Invoke(buttons);
    }
}
