using System;

namespace WSGM.Core;

/// <summary>
///     Requests the overlay's in-window keyboard for an internal text field. The overlay
///     controller owns its lifetime and returns accepted text to the invoking editor.
///     External application keyboard requests use the session's keyboard action instead.
/// </summary>
public static class KeyboardService
{
    /// <summary>
    ///     The registered opener: (prompt, initial text, maxLength, onAccept) →
    ///     whether it was handled. Set by the overlay controller.
    /// </summary>
    public static Func<string, string, int, Action<string>, bool>? Handler { get; set; }

    /// <summary>
    ///     Requests the keyboard surface for a single field. Returns whether a
    ///     handler took it; a false return means no keyboard surface is available.
    /// </summary>
    /// <param name="prompt">The label shown above the field.</param>
    /// <param name="initial">The starting text.</param>
    /// <param name="maxLength">Maximum accepted character count.</param>
    /// <param name="onAccept">Invoked with the final text when the user accepts.</param>
    public static bool Request(string prompt, string initial, int maxLength, Action<string> onAccept)
    {
        return Handler is not null && Handler(prompt, initial, maxLength, onAccept);
    }
}
