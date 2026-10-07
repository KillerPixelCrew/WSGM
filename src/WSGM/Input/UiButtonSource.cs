using System;

namespace WSGM.Input;

/// <summary>
///     Where WSGM's own navigation gets its button presses from.
/// </summary>
/// <remarks>
///     The source owns sampling and repetition. Subscribers consume semantic presses without acquiring
///     native input, polling a second controller source or manufacturing another repeat timer.
/// </remarks>
internal interface IUiButtonSource
{
    /// <summary>UI-thread button edges, with direction repeats already applied by the source.</summary>
    event Action<GamepadButtons>? ButtonPressed;
}
