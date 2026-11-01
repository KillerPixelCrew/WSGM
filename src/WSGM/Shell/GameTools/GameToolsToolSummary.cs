namespace WSGM.Shell;

/// <summary>One installed tool of a game in the Game Tools list.</summary>
/// <param name="ToolId">The tool id.</param>
/// <param name="ToolName">The product name.</param>
/// <param name="Version">The installed release's version, such as <c>0.9.4</c>.</param>
/// <param name="VersionLabel">The version as the user reads it, with the build date for a dated build.</param>
/// <param name="Channel">The channel the installation follows.</param>
/// <param name="ChannelLabel">The channel's name, such as <c>Stable</c> or <c>Nightly</c>.</param>
/// <param name="DefaultChannel">Whether that is the tool's default channel, which needs no channel tag.</param>
/// <param name="ProxyName">The file name the game loads the tool through.</param>
/// <param name="Status">
///     <c>upToDate</c>; <c>updateAvailable</c> only when the channel's latest release is already known from the
///     cached lookup; <c>modified</c> when owned files are missing or changed (needs repair); <c>interrupted</c>
///     when a change was interrupted or could not be rolled back; <c>folderUnavailable</c> when the folder the
///     tool was installed into is not there.
/// </param>
/// <param name="StatusLabel">The status chip's text, such as "Up to date" or "Update to 0.9.5".</param>
/// <param name="Detail">The row's line, such as "OptiScaler 0.9.4 as dxgi.dll".</param>
/// <param name="LatestVersion">The channel's latest version when an update is available, else null.</param>
/// <param name="ChangedFiles">How many owned files are missing or changed.</param>
internal sealed record GameToolsToolSummary(
    string ToolId,
    string ToolName,
    string Version,
    string VersionLabel,
    string Channel,
    string ChannelLabel,
    bool DefaultChannel,
    string ProxyName,
    string Status,
    string StatusLabel,
    string Detail,
    string? LatestVersion,
    int ChangedFiles);
