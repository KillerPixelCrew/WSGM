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
///     Supporting evidence only: it can supply the executable of the game Steam reports running, when
///     Steam does not expose one. It never names a game on its own, because a window in front while Steam
///     runs nothing is a desktop application. See <see cref="RunningApplicationTargetProjection" />.
/// </remarks>
internal sealed record ForegroundApplicationObservation(
    string? ExecutableName,
    string? ExecutablePath = null,
    uint ProcessId = 0)
{
    /// <summary>Nothing usable in the foreground.</summary>
    internal static ForegroundApplicationObservation None { get; } = new((string?)null);
}
