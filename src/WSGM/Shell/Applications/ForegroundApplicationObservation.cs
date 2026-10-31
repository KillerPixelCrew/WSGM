namespace WSGM.Shell;

/// <summary>
///     The application the user currently has in front of them, independent of Steam.
/// </summary>
/// <param name="ExecutableName">
///     File name of the foreground process with its extension, or <see langword="null" /> when nothing
///     usable is in front.
/// </param>
/// <param name="ExecutablePath">
///     Full image path of the same process, when it could be read. The projection needs it to prove a
///     candidate actually runs from a Steam title's install folder before pairing the two.
/// </param>
/// <param name="ProcessId">
///     The same process's identifier, or zero when it could not be read. It is what the RTSS rendering
///     proof matches on: comparing identifiers is exact, where comparing an image path against whatever
///     RTSS recorded in its own table is a guess about that table's format.
/// </param>
/// <remarks>
///     This is the second identity source, and it exists so per-application policy works outside a
///     Steam game: on the desktop, for a title launched from another launcher, or for anything the user
///     picks a profile for from the overlay. It never competes with Steam — see
///     <see cref="RunningApplicationTargetProjection" /> for the precedence rule.
/// </remarks>
internal sealed record ForegroundApplicationObservation(
    string? ExecutableName,
    string? ExecutablePath = null,
    uint ProcessId = 0)
{
    /// <summary>Nothing usable in the foreground.</summary>
    internal static ForegroundApplicationObservation None { get; } = new((string?)null);
}
