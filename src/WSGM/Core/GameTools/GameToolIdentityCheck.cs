using System;
using System.Linq;

namespace WSGM.Core;

/// <summary>Decides whether a binary is a tool's own build, regardless of its file name.</summary>
internal static class GameToolIdentityCheck
{
    /// <summary>Whether a version resource identifies the tool.</summary>
    /// <param name="tool">The tool.</param>
    /// <param name="info">The version resource, already trimmed.</param>
    /// <returns>
    ///     True when OriginalFilename matches ordinally ignoring case and no veto token (such as ReShade)
    ///     appears in any of the seven identity fields. The veto is checked first.
    /// </returns>
    internal static bool IsTool(GameToolDefinition tool, PeVersionInfo info)
    {
        if (tool.Identity.VetoTokens.Any(info.Mentions))
        {
            return false;
        }

        return info.OriginalFilename.Equals(tool.Identity.OriginalFilename, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Names another known mod a file belongs to.</summary>
    /// <param name="tool">The tool.</param>
    /// <param name="info">The version resource.</param>
    /// <returns>The other product's name, or null.</returns>
    internal static string? OtherTool(GameToolDefinition tool, PeVersionInfo info)
    {
        return tool.Identity.OtherTools.FirstOrDefault(other => info.Mentions(other.Token))?.Name;
    }
}
