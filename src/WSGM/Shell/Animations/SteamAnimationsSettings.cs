namespace WSGM.Shell;

/// <summary>What the pages know beyond the choice and the lists.</summary>
/// <param name="ShuffleOnStart">Whether the boot movie is picked anew when WSGM starts.</param>
/// <param name="BootVolume">The boot movie's volume in percent of its file's.</param>
/// <param name="LibraryPath">Where the movies are kept.</param>
/// <param name="OverridesPath">Where the override is written, or null without Steam.</param>
/// <param name="RestartNeeded">Whether the override changed since Steam started, so a restart shows it.</param>
internal sealed record SteamAnimationsSettings(
    bool ShuffleOnStart,
    int BootVolume,
    string LibraryPath,
    string? OverridesPath,
    bool RestartNeeded);
