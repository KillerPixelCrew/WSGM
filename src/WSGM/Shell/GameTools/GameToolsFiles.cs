using System.Collections.Generic;

namespace WSGM.Shell;

/// <summary>What WSGM owns in the game folder.</summary>
/// <param name="TargetDirectory">The folder the tool is or would be installed into, relative to the install folder.</param>
/// <param name="Owned">Files WSGM created.</param>
/// <param name="Replaced">Game files WSGM backed up and replaced.</param>
/// <param name="Missing">Owned files that are gone.</param>
/// <param name="Modified">Owned files changed since installation.</param>
internal sealed record GameToolsFiles(
    string TargetDirectory,
    int Owned,
    int Replaced,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> Modified);
