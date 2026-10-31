namespace WSGM.Shell;

/// <summary>The application window or desktop display a screenshot is of.</summary>
/// <param name="ProcessId">The application process, or zero for a desktop display.</param>
/// <param name="Window">The game window seen when the screenshot was requested, or zero when unknown.</param>
/// <param name="SteamAppId">The Steam app id whose overlay hosts Notes, or zero for WSGM's overlay.</param>
/// <param name="Description">The application or desktop, for messages and the log.</param>
/// <param name="Monitor">The desktop display to capture, or zero for an application window.</param>
internal sealed record NotesCaptureTarget(uint ProcessId, nint Window, uint SteamAppId, string Description, nint Monitor = 0)
{
    /// <summary>Whether the target is a desktop display rather than an application window.</summary>
    internal bool IsDesktop => Monitor != 0;
}
