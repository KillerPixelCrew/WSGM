namespace WSGM.Core;

/// <summary>Artwork provider credentials and the Steam browser's tab layout.</summary>
/// <remarks>
///     Persisted as <see cref="AppConfig.Artwork" />. The Screenscraper account is optional and only
///     raises that provider's own daily quota; WSGM ships the developer pair the API requires (see
///     <c>Core\Artwork\ScreenscraperCredentials.cs</c>). SteamGridDB has no bundled key, so an empty
///     one is reported as "not configured" rather than searched and refused.
/// </remarks>
public sealed class ArtworkConfig
{
    /// <summary>The user's SteamGridDB bearer key. Empty means the provider is not configured.</summary>
    public string SteamGridDbApiKey { get; set; } = "";

    /// <summary>Whether Screenscraper.fr is searched alongside SteamGridDB.</summary>
    public bool ScreenscraperEnabled { get; set; } = true;

    /// <summary>Optional Screenscraper account name, which raises that account's daily quota.</summary>
    public string ScreenscraperUser { get; set; } = "";

    /// <summary>The Screenscraper account's password.</summary>
    public string ScreenscraperUserPassword { get; set; } = "";

    /// <summary>Which tab the artwork browser opens on.</summary>
    public string DefaultTab { get; set; } = DefaultTabOrder.Split(',')[0];

    /// <summary>The tabs in display order, comma separated.</summary>
    public string TabOrder { get; set; } = DefaultTabOrder;

    /// <summary>Whether the Capsule tab is offered.</summary>
    public bool ShowGrid { get; set; } = true;

    /// <summary>Whether the Wide Capsule tab is offered.</summary>
    public bool ShowWide { get; set; } = true;

    /// <summary>Whether the Hero tab is offered.</summary>
    public bool ShowHero { get; set; } = true;

    /// <summary>Whether the Logo tab is offered.</summary>
    public bool ShowLogo { get; set; } = true;

    /// <summary>Whether the Icon tab is offered.</summary>
    public bool ShowIcon { get; set; } = true;

    /// <summary>Whether the Manage tab is offered.</summary>
    public bool ShowManage { get; set; } = true;

    /// <summary>Every tab id, in the order a configuration that names none falls back to.</summary>
    public const string DefaultTabOrder = "grid,wide,hero,logo,icon,manage";
}
