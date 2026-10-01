using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using WSGM.Core;

namespace WSGM.Settings;

public sealed partial class SettingsViewModel
{
    /// <summary>What each artwork tab id is called, in the canonical order.</summary>
    private static readonly Dictionary<string, string> ArtworkTabTitles = new(StringComparer.Ordinal)
    {
        ["grid"] = "Capsule",
        ["wide"] = "Wide capsule",
        ["hero"] = "Hero",
        ["logo"] = "Logo",
        ["icon"] = "Icon",
        ["manage"] = "Manage"
    };

    /// <summary>Gets the command that moves an artwork tab earlier in the strip.</summary>
    public RelayCommand<ArtworkTabRow> MoveArtworkTabUpCommand { get; }

    /// <summary>Gets the command that moves an artwork tab later in the strip.</summary>
    public RelayCommand<ArtworkTabRow> MoveArtworkTabDownCommand { get; }

    /// <summary>
    ///     Gets or sets whether WSGM deploys its Steam Input shim into Steam's
    ///     own install directory, so Steam loads it and WSGM never injects.
    /// </summary>
    public bool SteamInputManagementEnabled
    {
        get;
        set => SetField(ref field, value, nameof(SteamInputManagementEnabled));
    }

    /// <summary>Gets or sets the user's own SteamGridDB key. Empty leaves that source unsearched.</summary>
    public string ArtworkSteamGridDbApiKey
    {
        get;
        set => SetField(ref field, value, nameof(ArtworkSteamGridDbApiKey));
    } = "";

    /// <summary>Gets or sets whether Screenscraper.fr is searched alongside SteamGridDB.</summary>
    public bool ArtworkScreenscraperEnabled
    {
        get;
        set => SetField(ref field, value, nameof(ArtworkScreenscraperEnabled));
    }

    /// <summary>Gets or sets the optional Screenscraper account, which raises its own daily quota.</summary>
    public string ArtworkScreenscraperUser
    {
        get;
        set => SetField(ref field, value, nameof(ArtworkScreenscraperUser));
    } = "";

    /// <summary>Gets or sets the Screenscraper account's password.</summary>
    public string ArtworkScreenscraperPassword
    {
        get;
        set => SetField(ref field, value, nameof(ArtworkScreenscraperPassword));
    } = "";

    /// <summary>Gets the artwork page's tabs, in the order they are offered.</summary>
    /// <remarks>
    ///     The collection's own order is the stored tab order, so the move commands are the whole
    ///     reordering edit and nothing else has to be kept in step.
    /// </remarks>
    public ObservableCollection<ArtworkTabRow> ArtworkTabs { get; } = [];

    /// <summary>Gets the launch modes a new Game Library title can start on, in index order.</summary>
    public IReadOnlyList<string> GameLibraryModes { get; } = ["Steam overlay", "Controller only"];

    /// <summary>Gets or sets which mode a newly found single-player title starts on.</summary>
    public int GameLibraryDefaultModeIndex
    {
        get;
        set => SetField(ref field, value, nameof(GameLibraryDefaultModeIndex));
    }

    /// <summary>Gets or sets whether titles with no validated launch route are offered.</summary>
    public bool GameLibraryImportUnroutable
    {
        get;
        set => SetField(ref field, value, nameof(GameLibraryImportUnroutable));
    }

    /// <summary>Gets where a new Game Library title's artwork starts from, in index order.</summary>
    public IReadOnlyList<string> GameLibraryArtworkPreferences { get; } =
        ["The launcher's own images", "SteamGridDB"];

    /// <summary>Gets or sets which artwork a new title starts on, as an index into the list.</summary>
    public int GameLibraryArtworkPreferenceIndex
    {
        get;
        set => SetField(ref field, value, nameof(GameLibraryArtworkPreferenceIndex));
    }

    /// <summary>Gets or sets which tab the artwork page opens on, as an index into the strip.</summary>
    public int ArtworkDefaultTabIndex
    {
        get;
        set => SetField(ref field, value, nameof(ArtworkDefaultTabIndex));
    }

#pragma warning disable CA1822
    /// <summary>
    ///     Gets a plain-language description of the shim deployment, naming the
    ///     file so a pasted screenshot is diagnostic on its own.
    /// </summary>
    public string SteamInputShimStatusText => SteamInputManagement.Describe(SteamInputShim.LastStatus);
#pragma warning restore CA1822

    // --- Steam (the only launcher; located via registry, nothing to configure) ---
#pragma warning disable CA1822
    /// <summary>Gets Steam discovery status because game mode requires Steam.</summary>
    // ReSharper disable once MemberCanBeMadeStatic.Global
    public string SteamStatusText => Steam.ExePath is { } exe
        ? $"Detected: {exe}"
        : "Steam was not found on this PC. Install Steam first — WSGM is Steam-exclusive.";
#pragma warning restore CA1822

    /// <summary>Gets or sets whether the Steam monitor restarts Steam after an unexpected exit.</summary>
    public bool SteamAutoRelaunch
    {
        get;
        set => SetField(ref field, value, nameof(SteamAutoRelaunch));
    }

    /// <summary>Whether the complete Steam client starts at medium integrity.</summary>
    public bool SteamLaunchUnelevated
    {
        get;
        set => SetField(ref field, value, nameof(SteamLaunchUnelevated));
    }

    /// <summary>Builds the tab rows from the stored order and visibility.</summary>
    private void LoadArtworkTabs()
    {
        var titles = ArtworkTabTitles;
        var stored = _config.Artwork.TabOrder
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(titles.ContainsKey)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // ConfigStore repairs a stored order that is not a permutation, but Settings can be opened
        // against anything on disk, so the canonical list still fills in whatever is missing.
        stored.AddRange(ArtworkConfig.DefaultTabOrder.Split(',').Where(id => !stored.Contains(id)));

        ArtworkTabs.Clear();
        foreach (var id in stored)
        {
            ArtworkTabs.Add(new ArtworkTabRow(id, titles[id], IsArtworkTabVisible(id)));
        }

        var index = ArtworkTabs.ToList()
            .FindIndex(row => string.Equals(row.Id, _config.Artwork.DefaultTab, StringComparison.Ordinal));
        ArtworkDefaultTabIndex = index < 0 ? 0 : index;
    }

    /// <summary>Whether the stored configuration offers one tab.</summary>
    /// <param name="id">The tab id.</param>
    private bool IsArtworkTabVisible(string id)
    {
        return id switch
        {
            "grid" => _config.Artwork.ShowGrid,
            "wide" => _config.Artwork.ShowWide,
            "hero" => _config.Artwork.ShowHero,
            "logo" => _config.Artwork.ShowLogo,
            "icon" => _config.Artwork.ShowIcon,
            "manage" => _config.Artwork.ShowManage,
            _ => true
        };
    }

    /// <summary>Whether the edited rows offer one tab.</summary>
    /// <param name="id">The tab id.</param>
    private bool IsArtworkTabChecked(string id)
    {
        return ArtworkTabs.FirstOrDefault(row => string.Equals(row.Id, id, StringComparison.Ordinal))
            ?.Visible ?? true;
    }

    /// <summary>Moves an artwork tab by one position when the target remains in range.</summary>
    /// <param name="row">The row to move, or null (a no-op).</param>
    /// <param name="delta">The signed number of positions to move the row.</param>
    private void MoveArtworkTab(ArtworkTabRow? row, int delta)
    {
        if (row is null)
        {
            return;
        }

        var index = ArtworkTabs.IndexOf(row);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= ArtworkTabs.Count)
        {
            return;
        }

        // The default follows the tab it names rather than the position, which is what a user
        // reordering the strip means by it.
        var wanted = ArtworkDefaultTabIndex >= 0 && ArtworkDefaultTabIndex < ArtworkTabs.Count
            ? ArtworkTabs[ArtworkDefaultTabIndex]
            : null;
        ArtworkTabs.Move(index, target);
        if (wanted is not null)
        {
            ArtworkDefaultTabIndex = ArtworkTabs.IndexOf(wanted);
        }
    }
}
