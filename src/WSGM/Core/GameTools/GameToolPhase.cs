namespace WSGM.Core;

/// <summary>
///     The stable ids of a Game Tools operation's phases. Install, update, repair and a channel switch pass
///     through <see cref="Download" />, <see cref="Verify" />, <see cref="Copy" /> and <see cref="Settings" />
///     in that order, the four steps both pages show.
/// </summary>
internal static class GameToolPhase
{
    /// <summary>Admitted and waiting for the previous work or the first answer.</summary>
    internal const string Starting = "starting";

    /// <summary>Downloading the package; still cancellable.</summary>
    internal const string Download = "download";

    /// <summary>Extracting and validating the package; still cancellable.</summary>
    internal const string Verify = "verify";

    /// <summary>Planning, backing up and copying into the game folder; it completes or rolls back.</summary>
    internal const string Copy = "copy";

    /// <summary>Writing settings into the configuration file.</summary>
    internal const string Settings = "settings";

    /// <summary>Removing the tool and restoring the game's files.</summary>
    internal const string Remove = "remove";

    /// <summary>Reading the game folder again after the change.</summary>
    internal const string Rescan = "rescan";
}
