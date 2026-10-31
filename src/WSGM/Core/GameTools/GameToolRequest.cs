using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>What an install, update or repair should do.</summary>
/// <param name="Operation">
///     <c>install</c>, <c>replaceExisting</c> (back up and replace an unmanaged installation), <c>update</c> or
///     <c>repair</c>.
/// </param>
/// <param name="ProxyName">The name the primary file is installed under.</param>
/// <param name="Settings">Setting values by id; missing ids keep their current or default value.</param>
/// <param name="ReplaceConfig">Take the package's stock configuration file, then re-apply WSGM's settings.</param>
internal sealed record GameToolRequest(
    string Operation,
    string ProxyName,
    IReadOnlyDictionary<string, string> Settings,
    bool ReplaceConfig = false)
{
    /// <summary>Whether an unmanaged installation is backed up and replaced instead of refusing the install.</summary>
    internal bool ReplaceExisting => Operation == "replaceExisting";
}
