namespace WSGM.Core;

/// <summary>
///     What one game's launch configuration looked like before WSGM changed it.
///     Restoring the launch action writes these values back.
/// </summary>
/// <remarks>
///     Load-bearing for non-Steam shortcuts: configuring one overwrites its Target with
///     the wrapper path, so without this snapshot the real program's path would survive
///     only inside the arguments string WSGM itself generated. Steam apps only need
///     <see cref="OriginalLaunchOptions" />, which is usually empty.
/// </remarks>
public sealed class LaunchWrapperConfig
{
    /// <summary>The configured app id (a shortcut's generated id, or a Steam app id).</summary>
    public long AppId { get; set; }

    /// <summary>Whether this entry is a non-Steam shortcut rather than a Steam title.</summary>
    public bool IsShortcut { get; set; }

    /// <summary>Which wrapper behaviours were applied.</summary>
    public LaunchWrapperMode Mode { get; set; }

    /// <summary>Whether WSGM applied its launch wrapper or a Steam-native custom action.</summary>
    public LaunchConfigurationKind Kind { get; set; }

    /// <summary>The executable or script selected for a Steam-native custom action.</summary>
    public string CustomActionPath { get; set; } = "";

    /// <summary>User-supplied native arguments appended to the custom action.</summary>
    public string CustomArguments { get; set; } = "";

    /// <summary>The shortcut's Target before WSGM replaced it (shortcuts only).</summary>
    public string OriginalTarget { get; set; } = "";

    /// <summary>The launch options / launch arguments before WSGM replaced them.</summary>
    public string OriginalLaunchOptions { get; set; } = "";

    /// <summary>
    ///     The shortcut's start directory at configuration time, recorded for
    ///     diagnostics; WSGM deliberately never changes it.
    /// </summary>
    public string OriginalStartDir { get; set; } = "";

    /// <summary>
    ///     Display name at configuration time, so the overlay can name an entry
    ///     whose game Steam can no longer resolve.
    /// </summary>
    public string Name { get; set; } = "";
}
