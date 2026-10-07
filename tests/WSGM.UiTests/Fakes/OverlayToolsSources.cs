using System.Text.Json;
using SkiaSharp;
using SteamUiToolkit;
using WSGM.Core;
using WSGM.Shell;

namespace WSGM.UiTests.Fakes;

internal sealed class EmulatorOverlaySource : IEmulatorBackend
{
    internal EmulatorSnapshot State { get; set; } = new()
    {
        Definitions =
        [
            new EmulatorDefinition
            {
                Id = "retroarch", Name = "RetroArch", Channels = ["stable"],
                DataPolicy = new EmulatorDataPolicy { HasCores = true }
            }
        ]
    };

    internal List<string> Commands { get; } = [];
    public event Action? Changed;

    public EmulatorSnapshot ReadState()
    {
        return State;
    }

    public EmulatorProgressState ReadProgressState()
    {
        return new EmulatorProgressState(State.Busy, State.Status);
    }

    public EmulatorPageState ReadPageState()
    {
        var systems = RomProfiles.ForInstallations(State.Installations);
        return new EmulatorPageState(State with { Busy = false, Status = "" }, systems,
            RomEmulatorProjection.Create(State, systems).Choices, [], "x64");
    }

    public Task<SteamUiCommandResult> CancelAsync(CancellationToken cancellationToken)
    {
        return Command("CancelAsync", cancellationToken);
    }

    public Task<SteamUiCommandResult> RefreshEmulatorsAsync(CancellationToken cancellationToken)
    {
        return Command("RefreshEmulatorsAsync", cancellationToken);
    }

    public Task<SteamUiCommandResult> InstallEmulatorAsync(string definitionId, string channel,
        CancellationToken cancellationToken)
    {
        return Command("InstallEmulatorAsync", cancellationToken);
    }

    public Task<SteamUiCommandResult> UpdateEmulatorAsync(string installationId, CancellationToken cancellationToken)
    {
        return Command("UpdateEmulatorAsync", cancellationToken);
    }

    public Task<SteamUiCommandResult> RepairEmulatorAsync(string installationId, CancellationToken cancellationToken)
    {
        return Command("RepairEmulatorAsync", cancellationToken);
    }

    public Task<SteamUiCommandResult> UseExternalEmulatorAsync(string definitionId, string executable,
        CancellationToken cancellationToken)
    {
        return Command("UseExternalEmulatorAsync", cancellationToken);
    }

    public Task<SteamUiCommandResult> RemoveEmulatorAsync(string installationId, CancellationToken cancellationToken)
    {
        return Command("RemoveEmulatorAsync", cancellationToken);
    }

    public Task<SteamUiCommandResult> IgnoreEmulatorVersionAsync(string installationId, string releaseId,
        CancellationToken cancellationToken)
    {
        return Command("IgnoreEmulatorVersionAsync", cancellationToken);
    }

    public Task<SteamUiCommandResult> OpenEmulatorReleaseNotesAsync(string definitionId, string channel,
        string architecture,
        CancellationToken cancellationToken)
    {
        return Command("OpenEmulatorReleaseNotesAsync", cancellationToken);
    }

    public Task<SteamUiCommandResult> ConfigureEmulatorPrerequisiteAsync(string installationId, string path,
        string kind, CancellationToken cancellationToken)
    {
        return Command("ConfigureEmulatorPrerequisiteAsync", cancellationToken);
    }

    public Task<SteamUiCommandResult> SetPreferredEmulatorAsync(string systemId, string installationId, string coreId,
        CancellationToken cancellationToken)
    {
        return Command("SetPreferredEmulatorAsync", cancellationToken);
    }

    private Task<SteamUiCommandResult> Command(string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Commands.Add(name);
        Changed?.Invoke();
        return Task.FromResult(SteamUiCommandResult.Applied);
    }
}

internal static class OverlayToolFixtures
{
    internal static readonly string Image = MakeImage();

    internal static SteamThemesState Themes => new("installed",
        [
            new SteamThemesInstalled("dark", "Dark", "Midnight", "1.0", "Example author", true, false, "outdated",
                "1.1", [], [])
        ],
        [
            new SteamThemesInstalled("profile", "Night.profile", "Night", "1.0", "", false, false, "local", null, [],
                ["Dark"])
        ], "",
        new SteamThemesBrowse("All", "Last Updated", "", new Dictionary<string, int> { ["QuickAccess"] = 1 },
            ["Last Updated", "Most Downloaded"],
            [
                new SteamThemesStoreItem("dark", "Dark", "Midnight", "1.1", "QuickAccess", ["QuickAccess"],
                    "Example author", Image, 120, 12, "2026-10-01", "outdated")
            ], 1, 1, false, null),
        null, new SteamThemesSettings(true, "auto", false, 420, "2026-10-01", "Themes", "Available"), [], false, null,
        null, 1, 1);

    internal static SteamAnimationsState Movies => new("library", "movie-one", "Steam's own",
        [
            new SteamAnimationsItem("movie-one", "Aurora", "Example author", Image, null, "A boot movie.", 32, 1200,
                "2026-10-01", true, false)
        ],
        new SteamAnimationsBrowse("newest",
            [new SteamAnimationsSort("newest", "Newest"), new SteamAnimationsSort("name", "Name")], "",
            [
                new SteamAnimationsItem("movie-two", "Sunrise", "Example author", Image, null, "A repository movie.", 8,
                    400, "2026-10-01", false, false)
            ], 1, 2, false, null),
        null, new SteamAnimationsSettings(false, 60, "Animations", "uioverrides", true), false, null, null, 1);

    internal static SteamArtworkBrowserState Artwork => new(42, "Example game",
        [
            new SteamArtworkBrowserTab("grid", "Capsule"), new SteamArtworkBrowserTab("wide", "Wide Capsule"),
            new SteamArtworkBrowserTab("hero", "Hero"), new SteamArtworkBrowserTab("logo", "Logo"),
            new SteamArtworkBrowserTab("icon", "Icon"), new SteamArtworkBrowserTab("manage", "Manage", true)
        ], "grid",
        [new SteamArtworkBrowserAsset("art-one", Image, Image, 600, 900, "png", "SteamGridDB", "Example artist")],
        [new SteamArtworkOfficialAsset("official", "Steam capsule", Image, 600, 900, "png")],
        [
            new SteamArtworkManagedSlot("grid", "Capsule", true, Image),
            new SteamArtworkManagedSlot("logo", "Logo", false)
        ],
        new SteamArtworkBrowserFilter([], [], [], Adult: false),
        [new SteamArtworkBrowserGame("match", "Example game", "SteamGridDB")], Revision: 1);

    internal static GameLibraryState Library => new(
        [
            new GameLibrarySource("xbox", "Xbox", LibrarySourceKind.Launcher, true, true, "Installed", 2, true, true),
            new GameLibrarySource("epic", "Epic Games", LibrarySourceKind.Launcher, true, true, "Installed", 1, true,
                true)
        ],
        ["Xbox", "Epic Games"], "review",
        [
            Entry("new-one", "New game", "new", "Add", true, 0),
            Entry("imported-one", "Imported game", "imported", "Update", false, 42),
            Entry("removed-one", "Removed game", "attention", "Remove", false, 43)
        ], 1, 0, 3, true, ArtworkPreference: "Catalog", CreateCollections: true, Revision: 1);

    private static string MakeImage()
    {
        using var bitmap = new SKBitmap(180, 260);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(26, 37, 53));
        using var paint = new SKPaint { Color = new SKColor(50, 160, 220), IsAntialias = true };
        canvas.DrawRoundRect(new SKRect(22, 42, 158, 218), 16, 16, paint);
        paint.Color = new SKColor(255, 157, 61);
        canvas.DrawCircle(90, 110, 40, paint);
        paint.Color = new SKColor(233, 229, 223);
        canvas.DrawRect(new SKRect(48, 166, 132, 177), paint);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 90);
        return "data:image/png;base64," + Convert.ToBase64String(encoded.ToArray());
    }

    private static GameLibraryEntry Entry(string id, string name, string group, string action, bool selected,
        uint appId)
    {
        return new GameLibraryEntry(id, name, "Xbox", "xbox", action, action, group, "Found by source", selected, true,
            false, true,
            true, "ControllerOnly", true, true, false, "Controller only", false, [], "", appId, null,
            [
                new GameLibraryArtworkSlot("grid", "default", Image, "Catalog", 1, 2),
                new GameLibraryArtworkSlot("wide", "keep", Image, "Steam", 0, 0),
                new GameLibraryArtworkSlot("hero", "none", "", "", 0, 0),
                new GameLibraryArtworkSlot("logo", "pick", Image, "SteamGridDB", 1, 1),
                new GameLibraryArtworkSlot("icon", "default", Image, "Catalog", 1, 1)
            ], "ready", "", "Example game", false);
    }
}

internal sealed class FakeThemesSource : IThemeBrowseSession
{
    internal readonly List<string> Commands = [];
    internal SteamThemesState State = OverlayToolFixtures.Themes;
    public event Action? Changed;

    public SteamThemesState ReadState()
    {
        return State;
    }

    public string Tab
    {
        get => State.ActiveTab;
        set => State = State with { ActiveTab = value };
    }

    public void CancelQueries()
    {
    }

    public void Dispose()
    {
    }

    public Task<SteamUiCommandResult> SetTabAsync(string tab, CancellationToken cancellationToken)
    {
        return Command("SetTabAsync");
    }

    public Task<SteamUiCommandResult> BrowseAsync(string filter, string order, string search,
        CancellationToken cancellationToken)
    {
        return Command("BrowseAsync");
    }

    public Task<SteamUiCommandResult> LoadMoreAsync(CancellationToken cancellationToken)
    {
        return Command("LoadMoreAsync");
    }

    public Task<SteamUiCommandResult> OpenAsync(string id, CancellationToken cancellationToken)
    {
        var item = State.Browse.Items.First();
        State = State with
        {
            Detail = new SteamThemesDetail(item, "Theme details", [OverlayToolFixtures.Image], [], false, null)
        };
        return Command("OpenAsync");
    }

    public Task<SteamUiCommandResult> CloseDetailAsync(CancellationToken cancellationToken)
    {
        State = State with { Detail = null };
        return Command("CloseDetailAsync");
    }

    public Task<SteamUiCommandResult> InstallAsync(string id, CancellationToken cancellationToken)
    {
        return Command("InstallAsync");
    }

    public Task<SteamUiCommandResult> UpdateAsync(string name, CancellationToken cancellationToken)
    {
        return Command("UpdateAsync");
    }

    public Task<SteamUiCommandResult> UpdateAllAsync(CancellationToken cancellationToken)
    {
        return Command("UpdateAllAsync");
    }

    public Task<SteamUiCommandResult> DeleteAsync(string name, CancellationToken cancellationToken)
    {
        return Command("DeleteAsync");
    }

    public Task<SteamUiCommandResult> SetEnabledAsync(string name, bool enabled, CancellationToken cancellationToken)
    {
        return Command("SetEnabledAsync");
    }

    public Task<SteamUiCommandResult> SetPatchAsync(string theme, string patch, string value,
        CancellationToken cancellationToken)
    {
        Commands.Add("value:" + value);
        return Command("SetPatchAsync");
    }

    public Task<SteamUiCommandResult> SetComponentAsync(string theme, string patch, string component, string value,
        CancellationToken cancellationToken)
    {
        return Command("SetComponentAsync");
    }

    public Task<SteamUiCommandResult> SetProfileAsync(string name, CancellationToken cancellationToken)
    {
        return Command("SetProfileAsync");
    }

    public Task<SteamUiCommandResult> CreateProfileAsync(string name, CancellationToken cancellationToken)
    {
        return Command("CreateProfileAsync");
    }

    public Task<SteamUiCommandResult> RefreshAsync(CancellationToken cancellationToken)
    {
        return Command("RefreshAsync");
    }

    public Task<SteamUiCommandResult> SetHiddenAsync(string name, bool hidden, CancellationToken cancellationToken)
    {
        return Command("SetHiddenAsync");
    }

    public Task<SteamUiCommandResult> SetSettingAsync(string key, JsonElement value,
        CancellationToken cancellationToken)
    {
        return Command("SetSettingAsync");
    }

    public Task<SteamUiCommandResult> SetThemesEnabledAsync(bool enabled, CancellationToken cancellationToken)
    {
        return Command("SetThemesEnabledAsync");
    }

    public Task<SteamUiCommandResult> SetTranslationsBranchAsync(string branch, CancellationToken cancellationToken)
    {
        return Command("SetTranslationsBranchAsync");
    }

    public Task<SteamUiCommandResult> DismissAsync(CancellationToken cancellationToken)
    {
        return Command("DismissAsync");
    }

    internal void Publish()
    {
        Changed?.Invoke();
    }

    private Task<SteamUiCommandResult> Command(string name)
    {
        Commands.Add(name);
        Publish();
        return Task.FromResult(SteamUiCommandResult.Applied);
    }
}

internal sealed class FakeMoviesSource : IAnimationBrowseSession
{
    internal readonly List<string> Commands = [];
    internal SteamAnimationsState State = OverlayToolFixtures.Movies;
    public event Action? Changed;

    public SteamAnimationsState ReadState()
    {
        return State;
    }

    public string Tab
    {
        get => State.ActiveTab;
        set => State = State with { ActiveTab = value };
    }

    public string? PreviewPath(string id)
    {
        return null;
    }

    public void Dispose()
    {
    }

    public Task<SteamUiCommandResult> SetTabAsync(string tab, CancellationToken cancellationToken)
    {
        return Command("SetTabAsync");
    }

    public Task<SteamUiCommandResult> BrowseAsync(string sort, string search, CancellationToken cancellationToken)
    {
        return Command("BrowseAsync");
    }

    public Task<SteamUiCommandResult> BrowseMoreAsync(CancellationToken cancellationToken)
    {
        return Command("BrowseMoreAsync");
    }

    public Task<SteamUiCommandResult> RefreshAsync(CancellationToken cancellationToken)
    {
        return Command("RefreshAsync");
    }

    public Task<SteamUiCommandResult> OpenAsync(string id, CancellationToken cancellationToken)
    {
        return Command("OpenAsync");
    }

    public Task<SteamUiCommandResult> CloseDetailAsync(CancellationToken cancellationToken)
    {
        return Command("CloseDetailAsync");
    }

    public Task<SteamUiCommandResult> DownloadAsync(string id, CancellationToken cancellationToken)
    {
        return Command("DownloadAsync");
    }

    public Task<SteamUiCommandResult> DeleteAsync(string id, CancellationToken cancellationToken)
    {
        return Command("DeleteAsync");
    }

    public Task<SteamUiCommandResult> SelectAsync(string id, CancellationToken cancellationToken)
    {
        return Command("SelectAsync");
    }

    public Task<SteamUiCommandResult> ShuffleAsync(CancellationToken cancellationToken)
    {
        return Command("ShuffleAsync");
    }

    public Task<SteamUiCommandResult> SetShuffleOnStartAsync(bool shuffle, CancellationToken cancellationToken)
    {
        return Command("SetShuffleOnStartAsync");
    }

    public Task<SteamUiCommandResult> SetBootVolumeAsync(int volume, CancellationToken cancellationToken)
    {
        return Command("SetBootVolumeAsync");
    }

    public Task<SteamUiCommandResult> AddFileAsync(string path, CancellationToken cancellationToken)
    {
        return Command("AddFileAsync");
    }

    public Task<SteamUiCommandResult> DismissAsync(CancellationToken cancellationToken)
    {
        return Command("DismissAsync");
    }

    internal void Publish()
    {
        Changed?.Invoke();
    }

    private Task<SteamUiCommandResult> Command(string name)
    {
        Commands.Add(name);
        Publish();
        return Task.FromResult(SteamUiCommandResult.Applied);
    }
}

internal sealed class FakeArtworkSource : IArtworkBrowseSession
{
    internal readonly List<string> Commands = [];
    internal SteamArtworkBrowserState State = OverlayToolFixtures.Artwork;
    public event Action? Changed;

    public SteamArtworkBrowserState ReadState()
    {
        return State;
    }

    public void Dispose()
    {
    }

    public void CancelBrowsing()
    {
    }

    public Task<SteamUiCommandResult> OpenAsync(uint id, string? title, CancellationToken token)
    {
        State = State with { AppId = id, AppName = title ?? State.AppName };
        return Command("OpenAsync");
    }

    public Task<OverlayLibraryResult> ReadGamesAsync()
    {
        return Task.FromResult(new OverlayLibraryResult([new SteamLibraryApp(42, "Example game")], null));
    }

    public Task<SteamLogoPosition?> ReadLogoPositionAsync()
    {
        return Task.FromResult<SteamLogoPosition?>(new SteamLogoPosition("BottomLeft", 50, 50));
    }

    public Task<SteamUiCommandResult> SelectTabAsync(string tab, CancellationToken cancellationToken)
    {
        State = State with { ActiveTab = tab };
        return Command("SelectTabAsync");
    }

    public Task<SteamUiCommandResult> ApplyAsync(string id, CancellationToken cancellationToken)
    {
        return Command("ApplyAsync");
    }

    public Task<SteamUiCommandResult> ApplyOfficialAsync(string id, CancellationToken cancellationToken)
    {
        return Command("ApplyOfficialAsync");
    }

    public Task<SteamUiCommandResult> ClearAsync(string tab, CancellationToken cancellationToken)
    {
        return Command("ClearAsync");
    }

    public Task<SteamUiCommandResult> LoadMoreAsync(CancellationToken cancellationToken)
    {
        return Command("LoadMoreAsync");
    }

    public Task<SteamUiCommandResult> ApplyLocalAsync(string tab, string path, CancellationToken cancellationToken)
    {
        Commands.Add("slot:" + tab);
        return Command("ApplyLocalAsync");
    }

    public Task<SteamUiCommandResult> ApplyInvisibleAsync(string tab, CancellationToken cancellationToken)
    {
        return Command("ApplyInvisibleAsync");
    }

    public Task<SteamUiCommandResult> SetFilterAsync(SteamArtworkBrowserFilter filter,
        CancellationToken cancellationToken)
    {
        return Command("SetFilterAsync");
    }

    public Task<SteamUiCommandResult> SearchGamesAsync(string term, CancellationToken cancellationToken)
    {
        return Command("SearchGamesAsync");
    }

    public Task<SteamUiCommandResult> SelectGameAsync(string? id, CancellationToken cancellationToken)
    {
        return Command("SelectGameAsync");
    }

    public Task<SteamUiCommandResult> SaveLogoPositionAsync(string anchor, int width, int height,
        CancellationToken cancellationToken)
    {
        return Command("SaveLogoPositionAsync");
    }

    public Task<SteamUiCommandResult> ResetLogoPositionAsync(CancellationToken cancellationToken)
    {
        return Command("ResetLogoPositionAsync");
    }

    internal void Publish()
    {
        Changed?.Invoke();
    }

    private Task<SteamUiCommandResult> Command(string name)
    {
        Commands.Add(name);
        Publish();
        return Task.FromResult(SteamUiCommandResult.Applied);
    }
}

internal sealed class FakeLibrarySource : IGameLibraryOverlaySource
{
    internal readonly List<string> Commands = [];
    internal GameLibraryState State = OverlayToolFixtures.Library;
    public event Action? Changed;

    public RomEmulatorState ReadRomState()
    {
        return RomEmulatorState.Empty;
    }

    public IReadOnlyList<RomSystemProfile> ReadRomSystems()
    {
        return RomProfiles.All;
    }

    public Task<SteamUiCommandResult> AddRomSourceAsync(RomSourceConfig source, CancellationToken cancellationToken)
    {
        return Command("AddRomSourceAsync");
    }

    public Task<SteamUiCommandResult> RemoveRomSourceAsync(string id, CancellationToken cancellationToken)
    {
        return Command("RemoveRomSourceAsync");
    }

    public Task<SteamUiCommandResult> AddManualSourceAsync(ManualShortcutConfig source,
        CancellationToken cancellationToken)
    {
        return Command("AddManualSourceAsync");
    }

    public Task<SteamUiCommandResult> RemoveManualSourceAsync(string id, CancellationToken cancellationToken)
    {
        return Command("RemoveManualSourceAsync");
    }

    public Task<SteamUiCommandResult> SetRomEmulatorAsync(string id, string installationId, string coreId,
        CancellationToken cancellationToken)
    {
        State = State with
        {
            Entries = State.Entries.Select(entry => entry.Id == id
                ? entry with { EmulatorInstallationId = installationId, CoreId = coreId }
                : entry).ToArray()
        };
        return Command("SetRomEmulatorAsync");
    }

    public Task<SteamUiCommandResult> SetRomTitleAsync(string id, string name, CancellationToken cancellationToken)
    {
        State = State with
        {
            Entries = State.Entries.Select(entry => entry.Id == id ? entry with { Name = name } : entry).ToArray()
        };
        return Command("SetRomTitleAsync");
    }

    public Task<SteamUiCommandResult> SetRomArgumentsAsync(string id, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        State = State with
        {
            Entries = State.Entries.Select(entry => entry.Id == id
                ? entry with { Arguments = arguments.ToArray() }
                : entry).ToArray()
        };
        return Command("SetRomArgumentsAsync");
    }

    public Task<SteamUiCommandResult> SetCleanupAsync(string id, bool cleanup, CancellationToken cancellationToken)
    {
        return Command("SetCleanupAsync");
    }

    public Task<SteamUiCommandResult> RecheckAvailabilityAsync(string id, CancellationToken cancellationToken)
    {
        return Command("RecheckAvailabilityAsync");
    }

    public GameLibraryState ReadState()
    {
        return State;
    }

    public GameLibraryDetails? ReadDetails(string id)
    {
        return new GameLibraryDetails("Game folder", "source-identity", "Source-provided launch route", "Known",
            "Catalog", []);
    }

    public GameLibraryOptionsAnswer? ReadArtworkOptions(string id, string asset)
    {
        return new GameLibraryOptionsAnswer(asset, "ready", "", 1,
        [
            new GameLibraryArtworkOption(OverlayToolFixtures.Image, OverlayToolFixtures.Image, "Catalog", true, 600,
                900)
        ]);
    }

    public Task<SteamUiCommandResult> ScanAsync(CancellationToken cancellationToken)
    {
        return Command("ScanAsync");
    }

    public Task<SteamUiCommandResult> CancelAsync(CancellationToken cancellationToken)
    {
        return Command("CancelAsync");
    }

    public Task<SteamUiCommandResult> ToggleEntryAsync(string id, CancellationToken cancellationToken)
    {
        State = State with
        {
            Entries = State.Entries
                .Select(entry => entry.Id == id ? entry with { Selected = !entry.Selected } : entry).ToArray()
        };
        return Command("ToggleEntryAsync");
    }

    public Task<SteamUiCommandResult> SelectAsync(string group, string query, bool selected,
        CancellationToken cancellationToken)
    {
        return Command("SelectAsync");
    }

    public Task<SteamUiCommandResult> SetSelectedAsync(IReadOnlyList<string> ids, bool selected,
        CancellationToken cancellationToken)
    {
        State = State with
        {
            Entries = State.Entries
                .Select(entry => ids.Contains(entry.Id) ? entry with { Selected = selected } : entry).ToArray()
        };
        return Command("SetSelectedAsync");
    }

    public Task<SteamUiCommandResult> SetModeAsync(string id, string mode, bool acknowledged,
        CancellationToken cancellationToken)
    {
        State = State with
        {
            Entries = State.Entries.Select(entry => entry.Id == id ? entry with { Mode = mode } : entry).ToArray()
        };
        return Command("SetModeAsync");
    }

    public async Task<GameLibraryLaunchCycle> CycleLaunchAsync(string id, CancellationToken cancellationToken)
    {
        return new GameLibraryLaunchCycle(await Command("CycleLaunchAsync"));
    }

    public Task<SteamUiCommandResult> ExcludeAsync(string id, CancellationToken cancellationToken)
    {
        return Command("ExcludeAsync");
    }

    public Task<SteamUiCommandResult> IncludeAsync(string id, CancellationToken cancellationToken)
    {
        return Command("IncludeAsync");
    }

    public Task<SteamUiCommandResult> DetailsAsync(string id, CancellationToken cancellationToken)
    {
        return Command("DetailsAsync");
    }

    public Task<SteamUiCommandResult> OpenArtworkAsync(string id, CancellationToken cancellationToken)
    {
        return Command("OpenArtworkAsync");
    }

    public Task<SteamUiCommandResult> ApplyAsync(CancellationToken cancellationToken)
    {
        return Command("ApplyAsync");
    }

    public Task<SteamUiCommandResult> SetSourceEnabledAsync(string id, bool enabled,
        CancellationToken cancellationToken)
    {
        return Command("SetSourceEnabledAsync");
    }

    public Task<SteamUiCommandResult> SetCollectionsAsync(bool enabled, CancellationToken cancellationToken)
    {
        return Command("SetCollectionsAsync");
    }

    public Task<SteamUiCommandResult> AddFolderAsync(string path, bool includeSubfolders,
        IReadOnlyList<string> extensions, CancellationToken cancellationToken)
    {
        return Command("AddFolderAsync");
    }

    public Task<SteamUiCommandResult> RemoveFolderAsync(string id, CancellationToken cancellationToken)
    {
        return Command("RemoveFolderAsync");
    }

    public Task<SteamUiCommandResult> SetRouteAsync(string id, string route, CancellationToken cancellationToken)
    {
        return Command("SetRouteAsync");
    }

    public Task<SteamUiCommandResult> CycleArtworkAsync(string id, string asset, int delta,
        CancellationToken cancellationToken)
    {
        return Command("CycleArtworkAsync");
    }

    public Task<SteamUiCommandResult> PickArtworkAsync(string id, string asset, string url,
        CancellationToken cancellationToken)
    {
        return Command("PickArtworkAsync");
    }

    public Task<SteamUiCommandResult> ClearArtworkAsync(string id, string asset, CancellationToken cancellationToken)
    {
        return Command("ClearArtworkAsync");
    }

    public Task<SteamUiCommandResult> FillArtworkAsync(string preference, bool onlyEmpty, string asset,
        CancellationToken cancellationToken)
    {
        return Command("FillArtworkAsync");
    }

    public Task<SteamUiCommandResult> ResetArtworkAsync(CancellationToken cancellationToken)
    {
        return Command("ResetArtworkAsync");
    }

    public Task<SteamUiCommandResult> ArtworkOptionsAsync(string id, string asset, CancellationToken cancellationToken)
    {
        return Command("ArtworkOptionsAsync");
    }

    public async Task<GameLibraryMatchSearch> SearchMatchAsync(string id, string query,
        CancellationToken cancellationToken)
    {
        return new GameLibraryMatchSearch(await Command("SearchMatchAsync"), new GameLibraryMatchesAnswer([]));
    }

    public Task<SteamUiCommandResult> SetMatchAsync(string id, string provider, string gameId, string name,
        CancellationToken cancellationToken)
    {
        return Command("SetMatchAsync");
    }

    internal void Publish()
    {
        Changed?.Invoke();
    }

    private Task<SteamUiCommandResult> Command(string name)
    {
        Commands.Add(name);
        Publish();
        return Task.FromResult(SteamUiCommandResult.Applied);
    }
}
