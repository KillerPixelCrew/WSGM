using System;
using System.Threading.Tasks;
using WindowsDeviceControl;
using WSGM.Interop;

namespace WSGM.Overlay;

/// <summary>The display-mode operations supplied by the composition, also used by pinned controls.</summary>
/// <param name="Read">Reads modes for the sheet's current display source.</param>
/// <param name="Apply">Applies a committed choice to its observed display.</param>
/// <param name="Changes">
///     The session's display-change window, whose notification makes the selectors read again; null
///     without one, when they read only as they attach.
/// </param>
internal sealed record DisplayModeAccess(
    Func<string?, Task<DisplayModeSnapshot?>> Read,
    Func<DisplayModeSnapshot, DisplayMode, Task<DisplayModeResult>> Apply,
    DisplayChangeWindow? Changes = null)
{
    internal static DisplayModeAccess Unavailable { get; } = new(
        _ => Task.FromResult<DisplayModeSnapshot?>(null),
        (_, _) => Task.FromResult(new DisplayModeResult(DisplayModeOutcome.Refused, 0, false, false)));
}
