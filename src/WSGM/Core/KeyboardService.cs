using System;

namespace WSGM.Core;

/// <summary>Bridge for requesting the shared on-screen keyboard window. In game mode
/// the slim quick-access sidebar has no room for a keyboard, so text entry pops a
/// separate keyboard window beside it (see <c>Overlay\KeyboardWindow</c>), which the
/// <c>OverlayController</c> owns and coordinates for gamepad focus. Any sidebar surface
/// that needs typing calls <see cref="Request"/> instead of embedding a keyboard.
/// When no handler is registered (e.g. Settings, which has real keyboard focus), the
/// request is a no-op and callers should fall back to a plain TextBox.</summary>
public static class KeyboardService
{
    /// <summary>The registered opener: (prompt, initial text, onAccept) → whether it was
    /// handled. Set by the overlay controller.</summary>
    public static Func<string, string, int, Action<string>, bool>? Handler { get; set; }

    /// <summary>Requests the keyboard window for a single field. Returns whether a
    /// handler took it; a false return means no keyboard window is available.</summary>
    /// <param name="prompt">The label shown above the field.</param>
    /// <param name="initial">The starting text.</param>
    /// <param name="onAccept">Invoked with the final text when the user accepts.</param>
    /// <param name="maxLength">Maximum accepted character count.</param>
    public static bool Request(string prompt, string initial, int maxLength, Action<string> onAccept)
        => Handler is not null && Handler(prompt, initial ?? "", maxLength, onAccept);
}
