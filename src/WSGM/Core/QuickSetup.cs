namespace WSGM.Core;

/// <summary>Decides when the first-run Quick Setup panel is shown.</summary>
/// <remarks>
/// Keyed on a revision rather than a "seen it" flag so the panel can come back
/// exactly once when a later build adds a setting that needs an explicit decision -
/// the same way Steam Input Management needed one. Raising
/// <see cref="CurrentRevision"/> is the whole trigger; everything else follows from
/// the comparison, and a user who has already answered a revision is never asked
/// about it again.
/// </remarks>
public static class QuickSetup
{
    /// <summary>The revision this build asks about.</summary>
    /// <remarks>
    /// Revision 1 introduced Steam Input Management, which writes a file into
    /// Steam's own install directory, and the Steam CEF integration master switch.
    /// Raise this only when a NEW setting genuinely needs the user's decision -
    /// every raise interrupts every existing device once.
    /// </remarks>
    public const int CurrentRevision = 1;

    /// <summary>Whether the panel should be shown for the given configuration.</summary>
    /// <param name="config">The configuration to test.</param>
    /// <returns><see langword="true"/> when this device has not answered the current revision.</returns>
    public static bool ShouldShow(AppConfig config) =>
        config.QuickSetupRevision < CurrentRevision;

    /// <summary>Records that the current revision has been answered.</summary>
    /// <param name="config">The configuration to stamp.</param>
    public static void MarkCompleted(AppConfig config) =>
        config.QuickSetupRevision = CurrentRevision;
}
