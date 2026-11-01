using System;
using System.IO;

namespace WSGM.Core;

/// <summary>Canonical WSGM application identity plus optional Steam and RTSS enrichment.</summary>
/// <remarks>
///     <see cref="ApplicationId" /> is authoritative whenever this record exists. Steam can name a game
///     before Windows exposes its foreground executable, so <see cref="RtssProfileName" /> is optional:
///     policy remains per-application. The frame limit waits for enrichment; the overlay level
///     applies through the global RTSS profile meanwhile.
/// </remarks>
/// <param name="ApplicationId">Authoritative WSGM application identifier.</param>
/// <param name="SteamAppId">Steam identity, when known.</param>
/// <param name="RtssProfileName">Executable profile name, or null until executable identity is available.</param>
/// <param name="ProcessId">Known application process, or null.</param>
internal sealed record PerformanceApplicationTarget(
    string ApplicationId,
    uint? SteamAppId,
    string? RtssProfileName,
    int? ProcessId = null)
{
    /// <summary>The RTSS target for the running application, or null for the global profile.</summary>
    /// <param name="active">The running application.</param>
    /// <returns>The target, with an invalid executable dropped.</returns>
    internal static PerformanceApplicationTarget? For(ActiveProfile active)
    {
        if (!active.HasApplication)
        {
            return null;
        }

        var executable = active.Executable is { } name && ValidProfileName(name) ? name.Trim() : null;
        return new PerformanceApplicationTarget(active.ApplicationId!.Trim(), active.SteamAppId, executable);
    }

    private static bool ValidProfileName(string value)
    {
        return !string.IsNullOrWhiteSpace(value)
               && string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal)
               && value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
    }
}
