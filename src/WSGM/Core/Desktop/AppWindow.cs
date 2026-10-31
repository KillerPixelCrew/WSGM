namespace WSGM.Core;

/// <summary>A visible, switchable top-level window discovered during enumeration.</summary>
/// <param name="Hwnd">The native window handle.</param>
/// <param name="Title">The title presented in the switcher.</param>
/// <param name="ProcessId">The identifier of the owning process.</param>
public sealed record AppWindow(nint Hwnd, string Title, uint ProcessId)
{
    /// <summary>Gets whether the window was minimized at enumeration time.</summary>
    public bool IsMinimized { get; init; }
}
