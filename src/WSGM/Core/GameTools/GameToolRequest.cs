using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>What an install, update, channel switch or repair should do.</summary>
/// <param name="Operation">
///     <c>install</c>, <c>replaceExisting</c> (back up and replace an unmanaged installation), <c>update</c>,
///     <c>switchChannel</c> (install the latest release of another channel, older or newer) or <c>repair</c>.
/// </param>
/// <param name="ProxyName">The name the primary file is installed under.</param>
/// <param name="Settings">Setting values by id; missing ids keep their current or default value.</param>
/// <param name="ReplaceConfig">Take the package's stock configuration file, then re-apply WSGM's settings.</param>
/// <param name="Channel">The channel the package came from, recorded in the manifest; null keeps the current one.</param>
internal sealed record GameToolRequest(
    string Operation,
    string ProxyName,
    IReadOnlyDictionary<string, string> Settings,
    bool ReplaceConfig = false,
    string? Channel = null)
{
    /// <summary>Whether an unmanaged installation is backed up and replaced instead of refusing the install.</summary>
    internal bool ReplaceExisting => Operation is "replaceExisting" or "adopt";
}
