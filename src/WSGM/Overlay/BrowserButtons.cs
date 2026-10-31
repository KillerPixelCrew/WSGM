using System;
using WSGM.Input;

namespace WSGM.Overlay;

/// <summary>
///     The sheet's controller presses less the ones the browser page's native viewport consumes, without
///     another poller, input lease or mouse mapping.
/// </summary>
internal sealed class BrowserButtons : IUiButtonSource, IDisposable
{
    private readonly Func<GamepadButtons, bool> _consume;
    private readonly IUiButtonSource _source;

    /// <summary>Starts reading the source.</summary>
    /// <param name="source">The presses.</param>
    /// <param name="consume">Takes a press and answers whether it was consumed, which keeps it from the sheet.</param>
    internal BrowserButtons(IUiButtonSource source, Func<GamepadButtons, bool> consume)
    {
        _source = source;
        _consume = consume;
        source.ButtonPressed += OnButtons;
    }

    /// <inheritdoc />
    public event Action<GamepadButtons>? ButtonPressed;

    /// <summary>Stops reading the source.</summary>
    public void Dispose()
    {
        _source.ButtonPressed -= OnButtons;
    }

    private void OnButtons(GamepadButtons buttons)
    {
        if (!_consume(buttons))
        {
            ButtonPressed?.Invoke(buttons);
        }
    }
}
