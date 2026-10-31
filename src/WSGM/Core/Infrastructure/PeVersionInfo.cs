using System;

namespace WSGM.Core;

/// <summary>The version resource fields WSGM compares when identifying a native file.</summary>
/// <param name="OriginalFilename">The file name the module was built as, independent of its current name.</param>
/// <param name="FileVersion">The numeric file version string.</param>
/// <param name="ProductVersion">The product version string, which may carry a channel suffix.</param>
/// <param name="FileDescription">The descriptive name.</param>
/// <param name="ProductName">The product name.</param>
/// <param name="InternalName">The internal module name.</param>
/// <param name="CompanyName">The publisher.</param>
internal sealed record PeVersionInfo(
    string OriginalFilename,
    string FileVersion,
    string ProductVersion,
    string FileDescription,
    string ProductName,
    string InternalName,
    string CompanyName)
{
    /// <summary>Whether any identity field contains a token, ignoring case.</summary>
    /// <param name="token">The lowercase token to look for.</param>
    /// <returns>True when one of the seven fields contains it.</returns>
    internal bool Mentions(string token)
    {
        return string.Join('\n', OriginalFilename, FileDescription, ProductName, InternalName, CompanyName,
                FileVersion, ProductVersion)
            .Contains(token, StringComparison.OrdinalIgnoreCase);
    }
}
