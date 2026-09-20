namespace WSGM.Plugin.Artwork;

/// <summary>The artwork plugin's provider configuration.</summary>
public sealed record ArtworkConfiguration(
    string SteamGridDbApiKey,
    bool ScreenscraperEnabled,
    string ScreenscraperUser,
    string ScreenscraperUserPassword,
    string DefaultTab = "grid",
    string TabOrder = "grid,wide,hero,logo,icon,manage",
    bool ShowGrid = true,
    bool ShowWide = true,
    bool ShowHero = true,
    bool ShowLogo = true,
    bool ShowIcon = true,
    bool ShowManage = true)
{
    /// <summary>The provider defaults used before host configuration is restored.</summary>
    public static ArtworkConfiguration Default { get; } = new(
        "", true, "", "");
}
