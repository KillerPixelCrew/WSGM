using System.Collections.Generic;

namespace WSGM.Shell;

/// <summary>
///     Every game with Game Tools records and what is installed in it, for the game list on both surfaces. It is
///     read from the durable store and the cached release answers, never from a folder walk.
/// </summary>
/// <param name="Loading">Whether the first read is still running and <paramref name="Games" /> is empty for that reason.</param>
/// <param name="Error">Why the store could not be read, or null.</param>
/// <param name="Games">Games with at least one installed tool, by name.</param>
/// <param name="WithTools">How many games have a tool installed: the "With tools" count.</param>
/// <param name="Updates">How many games have a known update for one of their tools: the "Updates" count.</param>
internal sealed record GameToolsLibrary(
    bool Loading,
    string? Error,
    IReadOnlyList<GameToolsGameSummary> Games,
    int WithTools,
    int Updates)
{
    /// <summary>The state before the first read.</summary>
    internal static GameToolsLibrary Pending { get; } = new(true, null, [], 0, 0);
}
