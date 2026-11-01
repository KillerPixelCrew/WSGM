using System.Collections.Generic;
using WSGM.Interop;

namespace WSGM.Shell;

/// <summary>The Windows calls a hotkey send makes, so tests can stand in for the desktop and the keyboard.</summary>
internal interface IHotkeyDesktop
{
    /// <summary>The foreground window.</summary>
    /// <returns>Its handle, or 0.</returns>
    nint Foreground();

    /// <summary>Describes a window that may receive a hotkey.</summary>
    /// <param name="window">A window handle.</param>
    /// <returns>The target, or null for WSGM, Steam, launchers, the shell and system surfaces.</returns>
    HotkeyTarget? Describe(nint window);

    /// <summary>Asks Windows to bring a window to the front.</summary>
    /// <param name="window">The window.</param>
    void BringToFront(nint window);

    /// <summary>Presses a chord in the foreground window.</summary>
    /// <param name="keys">The keys in press order.</param>
    /// <param name="pressDelayMs">How long the keys stay down.</param>
    /// <returns>How many key events Windows accepted.</returns>
    KeyboardInput.SendResult Send(IReadOnlyList<uint> keys, int pressDelayMs);
}
