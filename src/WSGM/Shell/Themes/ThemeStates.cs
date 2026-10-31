namespace WSGM.Shell;

/// <summary>
///     The <see cref="SteamThemesInstalled.Status" /> and <see cref="SteamThemesStoreItem.LocalStatus" />
///     values, shared by the service and the overlay.
/// </summary>
internal static class ThemeStates
{
    /// <summary>Installed at the store's version.</summary>
    public const string Installed = "installed";

    /// <summary>Installed at an older version than the store's.</summary>
    public const string Outdated = "outdated";

    /// <summary>Installed, and the store does not list it.</summary>
    public const string Local = "local";

    /// <summary>Not checked against the store yet.</summary>
    public const string Unknown = "unknown";

    /// <summary>A store listing that is not installed.</summary>
    public const string None = "none";
}
