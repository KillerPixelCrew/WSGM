using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>The gamepad-driven SteamGridDB artwork changer, hosted as a Tools sub-view
/// of the overlay (like <see cref="LibraryTabsView"/>). Flow: target the game the user
/// is viewing (<see cref="SteamPageBridge.GetCurrentAppIdAsync"/>) or pick one from the
/// library → choose an artwork slot → browse SteamGridDB thumbnails → apply. Applying
/// grid/hero/logo/wide is a robust Steam API call (<see cref="SteamArtwork"/>); the
/// image bytes are fetched and base64-encoded in C#. Self-drawing (no XAML), every
/// interactive element a <see cref="Button"/> so D-pad/A/B work with no extra
/// plumbing.</summary>
public sealed class ArtworkView : UserControl
{
    private static readonly System.Threading.SemaphoreSlim ThumbnailGate = new(4, 4);
    private readonly Stack<Action> _stack = new();
    private Action? _current;

    private long _appId;
    private string _appName = "";
    private string _apiKey = "";
    private IReadOnlyList<SteamCollections.AppInfo>? _games;

    // When > 0, artwork is sourced from this SteamGridDB game id (a manual name search)
    // instead of the target's Steam app id — needed for non-Steam shortcuts / ROMs and
    // when the auto-detected game is wrong. The art still APPLIES to _appId.
    private int _sgdbGameId;
    private int _generation;

    // One-shot message shown at the top of the next overview render (apply outcome).
    private string? _notice;

    // Remembered shortcut → SGDB game associations, snapshotted from config on open
    // and updated on every match pick, so a shortcut is clarified once, not per visit.
    private readonly Dictionary<long, (int Id, string Name)> _sgdbLinks = new();

    /// <summary>Raised when the user backs out of the top level.</summary>
    public event Action? CloseRequested;

    /// <summary>Loads config, detects the current game, and opens the picker.</summary>
    public void Open() => _ = RunSafelyAsync(OpenAsync(), "open");

    private async Task OpenAsync()
    {
        var generation = ++_generation;
        _stack.Clear();
        _current = null;
        _sgdbGameId = 0;
        var config = await Task.Run(LibraryTabManager.LoadConfig);
        if (generation != _generation) { return; }
        _apiKey = SteamGridDb.ResolveKey(config);
        _sgdbLinks.Clear();
        foreach (var link in config.SgdbLinks.Where(l => l.SgdbGameId > 0))
        {
            _sgdbLinks[link.AppId] = (link.SgdbGameId, link.Name);
        }
        if (string.IsNullOrEmpty(_apiKey))
        {
            Navigate(RenderNoKey);
            return;
        }

        Navigate(() => RenderMessage("Change Artwork", "Detecting the game you're viewing…"));
        try
        {
            _appId = await SteamPageBridge.GetCurrentAppIdAsync();
            if (generation != _generation)
            {
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Artwork: current-app detect failed: {ex.Message}");
            _appId = 0;
        }

        if (_appId > 0)
        {
            _games ??= await SafeGamesAsync();
            if (generation != _generation)
            {
                return;
            }
            _appName = NameFor(_appId);
            if (IsShortcutApp(_appId))
            {
                // Clarify the SteamGridDB source game UP FRONT: a shortcut's id has
                // no Steam page, and springing a text box on the user after they
                // pick an art type reads as a broken flow. A remembered match skips
                // the question entirely; otherwise auto-search by the shortcut's
                // name and let them pick — typing only on explicit request.
                if (_sgdbLinks.TryGetValue(_appId, out var link))
                {
                    _sgdbGameId = link.Id;
                    _appName = link.Name;
                    Replace(RenderAssetTypes);
                    return;
                }
                DoSgdbSearch(_appName);
                return;
            }
            Replace(RenderAssetTypes);
        }
        else
        {
            Replace(RenderGameList);
        }
    }

    /// <summary>Invalidates outstanding work when the host hides this view.</summary>
    public void Close() => _generation++;

    /// <summary>Handles Back/B: pops one level or requests close at the top.</summary>
    public bool Back()
    {
        if (_stack.Count == 0)
        {
            CloseRequested?.Invoke();
            return true;
        }
        _current = _stack.Pop();
        _current();
        return true;
    }

    private void Navigate(Action render)
    {
        if (_current is not null)
        {
            _stack.Push(_current);
        }
        _current = render;
        render();
    }

    private void Replace(Action render)
    {
        _current = render;
        render();
    }

    private void RenderNoKey()
    {
        var stack = NewStack("Change Artwork");
        stack.Children.Add(Caption("This needs a free SteamGridDB API key. Add yours in "
            + "Settings → Steam, then reopen this."));
        stack.Children.Add(Caption($"Get one at {SteamGridDb.KeyPageUrl}"));
        stack.Children.Add(SectionLabel(""));
        stack.Children.Add(Row("Close", "Back to Tools", Icons.ExitFullscreen,
            () => CloseRequested?.Invoke()));
        SetContent(stack);
    }

    // ---- Level: pick a game ----

    private void RenderGameList() => _ = RunSafelyAsync(RenderGameListAsync(), "game list");

    private async Task RenderGameListAsync()
    {
        var generation = _generation;
        RenderMessage("Change Artwork", "Loading your games…");
        var games = await SafeGamesAsync();
        if (generation != _generation) { return; }
        _games = games;
        var stack = NewStack("Change Artwork");
        stack.Children.Add(Caption("Choose a game (or open one in Steam and reopen this)."));
        foreach (var game in _games)
        {
            var g = game;
            stack.Children.Add(Row(g.Name, g.Shortcut ? "Non-Steam shortcut" : "", Icons.SteamLike, () =>
            {
                _appId = g.AppId;
                _appName = g.Name;
                _sgdbGameId = 0;
                if (g.Shortcut)
                {
                    // Same up-front clarification as the auto-detected case, with the
                    // same remembered-match short-circuit.
                    if (_sgdbLinks.TryGetValue(g.AppId, out var link))
                    {
                        _sgdbGameId = link.Id;
                        _appName = link.Name;
                        Navigate(RenderAssetTypes);
                        return;
                    }
                    DoSgdbSearch(g.Name);
                }
                else
                {
                    Navigate(RenderAssetTypes);
                }
            }));
        }
        stack.Children.Add(SectionLabel(""));
        stack.Children.Add(Row("Back", "Close", Icons.ExitFullscreen, () => Back()));
        SetContent(stack);
    }

    // ---- Level: pick an artwork slot ----

    private static readonly (ArtworkAsset Asset, string Label, string Desc)[] Assets =
    [
        (ArtworkAsset.Grid, "Capsule (portrait)", "The vertical library cover (600×900)"),
        (ArtworkAsset.Hero, "Hero banner", "The wide banner on the game page"),
        (ArtworkAsset.Logo, "Logo", "The transparent title logo"),
        (ArtworkAsset.Wide, "Wide capsule", "The horizontal cover (460×215)"),
        (ArtworkAsset.Icon, "Icon", "Small icon (Steam games only)"),
    ];

    private void RenderAssetTypes()
    {
        var stack = NewStack("Change Artwork");
        if (_notice is not null)
        {
            stack.Children.Add(Caption(_notice));
            _notice = null;
        }
        stack.Children.Add(Caption(_sgdbGameId > 0
            ? $"Applying to: {_appName}  ·  art from your search"
            : IsShortcutApp(_appId)
                ? $"Shortcut: {_appName} — picking any art type first searches "
                    + "SteamGridDB by name (a shortcut's id has no Steam page)."
                : $"Game: {_appName}"));
        foreach (var (asset, label, desc) in Assets)
        {
            var a = asset;
            stack.Children.Add(Row(label, desc, Icons.Palette, () => OpenArtGrid(a)));
            // Current CUSTOM art for the slot, shown under its row (nothing when the
            // slot uses Steam's official art). Probed and decoded off-thread.
            var preview = new Image
            {
                Stretch = Stretch.Uniform,
                MaxHeight = 64,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Avalonia.Thickness(10, 0, 0, 4),
                IsVisible = false,
            };
            stack.Children.Add(preview);
            _ = LoadCurrentArtAsync(preview, _appId, a, _generation);
        }
        stack.Children.Add(SectionLabel(""));
        stack.Children.Add(Row("Wrong game? Search by name", "For ROMs, shortcuts, or misdetections",
            Icons.CopyDoc, RenderNameSearch));
        stack.Children.Add(Row("Change game", "Target a different installed game", Icons.SteamLike,
            () => Navigate(RenderGameList)));
        stack.Children.Add(Row("Back", "Close", Icons.ExitFullscreen, () => Back()));
        SetContent(stack);
    }

    // ---- Level: search SteamGridDB by name (non-Steam / misdetected games) ----

    private void RenderNameSearch()
    {
        // Prefer the separate keyboard window; fall back to an inline keyboard screen.
        if (KeyboardService.Request("Search SteamGridDB by name", _appName, 100,
                term => DoSgdbSearch(term)))
        {
            return;
        }
        Navigate(() =>
        {
            var stack = NewStack("Search SteamGridDB");
            stack.Children.Add(Caption("Type the game's name — used to find art (applies to "
                + $"{_appName})."));
            var box = new TextBox { Text = _appName, Margin = new Avalonia.Thickness(0, 0, 0, 4) };
            stack.Children.Add(box);
            var keyboard = new OnScreenKeyboard { Target = box };
            keyboard.Accepted += (_, _) => DoSgdbSearch(box.Text ?? "");
            stack.Children.Add(keyboard);
            stack.Children.Add(PrimaryRow("Search", "Find matching games", Icons.Play,
                () => DoSgdbSearch(box.Text ?? "")));
            stack.Children.Add(Row("Cancel", "Back", Icons.ExitFullscreen, () => Back()));
            SetContent(stack);
        });
    }

    private void DoSgdbSearch(string term) => _ = RunSafelyAsync(DoSgdbSearchAsync(term), "search");

    private async Task DoSgdbSearchAsync(string term)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return;
        }
        var generation = _generation;
        Navigate(() => RenderMessage("Search SteamGridDB", $"Searching for \"{term}\"…"));
        IReadOnlyList<SgdbGame> matches;
        string? failure = null;
        try
        {
            matches = await SteamGridDb.SearchGamesAsync(term, _apiKey);
        }
        catch (Exception ex)
        {
            Log.Warn($"Artwork: SGDB search failed: {ex.Message}");
            matches = Array.Empty<SgdbGame>();
            failure = ex.Message;
        }
        if (generation != _generation) { return; }
        Replace(() =>
        {
            var stack = NewStack("Pick a Match");
            if (failure is not null)
            {
                stack.Children.Add(Caption(failure));
            }
            else if (matches.Count == 0)
            {
                stack.Children.Add(Caption("No matches on SteamGridDB. Try a different name."));
            }
            foreach (var game in matches.Take(30))
            {
                var g = game;
                stack.Children.Add(Row(g.Name, "", Icons.Palette, () =>
                {
                    _sgdbGameId = g.Id;
                    _appName = g.Name;
                    RememberSgdbLink(g.Id, g.Name);
                    // Drop the search + pick levels; land back on the asset types.
                    PopIfAny();
                    PopIfAny();
                    Replace(RenderAssetTypes);
                }));
            }
            stack.Children.Add(SectionLabel(""));
            // Typing is an explicit choice, never a surprise: the text entry only
            // opens from this row (or when nothing matched and the user wants it).
            stack.Children.Add(Row("Type a different name", "Search SteamGridDB manually",
                Icons.CopyDoc, RenderNameSearch));
            stack.Children.Add(Row("Back", "Return", Icons.ExitFullscreen, () => Back()));
            SetContent(stack);
        });
    }

    // ---- Level: browse SteamGridDB art ----

    // Safety net (clarification normally happens on entry): a shortcut's generated
    // app id means nothing to SteamGridDB, so a lookup by it can only 404 — run the
    // auto-search instead; picking a match sets _sgdbGameId and lands back on the
    // asset types, after which grids load normally. Never a surprise text box.
    private void OpenArtGrid(ArtworkAsset asset)
    {
        if (_sgdbGameId == 0 && IsShortcutApp(_appId))
        {
            DoSgdbSearch(_appName);
            return;
        }
        _ = RunSafelyAsync(OpenArtGridAsync(asset), "art list");
    }

    private async Task OpenArtGridAsync(ArtworkAsset asset)
    {
        var generation = _generation;
        var sourceGameId = _sgdbGameId;
        var targetAppId = _appId;
        Navigate(() => RenderMessage(AssetLabel(asset), "Loading artwork from SteamGridDB…"));
        IReadOnlyList<SgdbAsset> assets;
        string? failure = null;
        try
        {
            assets = sourceGameId > 0
                ? await SteamGridDb.GetAssetsForGameAsync(asset, sourceGameId, _apiKey)
                : await SteamGridDb.GetAssetsForSteamAppAsync(asset, targetAppId, _apiKey);
        }
        catch (Exception ex)
        {
            Log.Warn($"Artwork: SGDB fetch failed: {ex.Message}");
            assets = Array.Empty<SgdbAsset>();
            failure = ex.Message;
        }
        if (generation != _generation || targetAppId != _appId || sourceGameId != _sgdbGameId)
        {
            return;
        }
        Replace(() => RenderArtGrid(asset, assets, failure));
    }

    private void RenderArtGrid(ArtworkAsset asset, IReadOnlyList<SgdbAsset> assets, string? failure)
    {
        var stack = NewStack(AssetLabel(asset));
        stack.Children.Add(Caption($"{_appName} — pick one to apply, or reset."));
        stack.Children.Add(PrimaryRow("Reset to official", "Remove the custom art", Icons.Restart,
            () => Apply(asset, null)));

        if (failure is not null)
        {
            stack.Children.Add(Caption(failure));
        }
        else if (assets.Count == 0)
        {
            stack.Children.Add(Caption("No artwork found for this game/slot on SteamGridDB."));
        }
        else
        {
            var (w, h) = ThumbSize(asset);
            var grid = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var art in assets.Where(a => ImageHeader.IsWithinLimits(a.Width, a.Height)).Take(30))
            {
                grid.Children.Add(ThumbButton(art, w, h, () => Apply(asset, art)));
            }
            stack.Children.Add(grid);
        }

        stack.Children.Add(SectionLabel(""));
        stack.Children.Add(Row("Back", "Choose another slot", Icons.ExitFullscreen, () => Back()));
        SetContent(stack);
    }

    private Button ThumbButton(SgdbAsset art, double w, double h, Action onClick)
    {
        var image = new Image { Stretch = Stretch.UniformToFill };
        var button = new Button
        {
            Content = image,
            Width = w,
            Height = h,
            Padding = new Avalonia.Thickness(2),
            Margin = new Avalonia.Thickness(3),
        };
        button.Click += (_, _) => onClick();
        _ = LoadThumbAsync(image, string.IsNullOrEmpty(art.Thumb) ? art.Url : art.Thumb, _generation);
        return button;
    }

    // Mirrors SteamGridDb.DownloadImageAsync's 16 MB safety limit for formats whose
    // headers ImageHeader cannot read (webp previews must keep working).
    private const long CurrentArtMaxBytes = 16 * 1024 * 1024;

    // Shows the slot's current custom-art file (if any) in the given placeholder.
    // Disk-only; failures just leave the preview hidden.
    private async Task LoadCurrentArtAsync(Image image, long appId, ArtworkAsset asset, int generation)
    {
        try
        {
            var bitmap = await Task.Run(() =>
            {
                var path = SteamArtwork.FindCustomArtFile(appId, asset);
                if (path is null)
                {
                    return null;
                }
                // Grid files are written by Steam and third-party art tools, so they are
                // untrusted: refuse hostile declared dimensions for the formats ImageHeader
                // parses (PNG/JPEG/BMP), and byte-cap the ones it cannot (webp) so a tiny
                // file cannot commit an unbounded decode allocation.
                if (ImageHeader.TryReadSize(path, out var artWidth, out var artHeight))
                {
                    if (!ImageHeader.IsWithinLimits(artWidth, artHeight))
                    {
                        Log.Warn($"Artwork: current-art preview skipped, image declares "
                            + $"{artWidth}x{artHeight} px: {path}");
                        return null;
                    }
                }
                else if (new FileInfo(path).Length > CurrentArtMaxBytes)
                {
                    Log.Warn($"Artwork: current-art preview skipped, file exceeds "
                        + $"{CurrentArtMaxBytes / (1024 * 1024)} MB cap: {path}");
                    return null;
                }
                using var stream = File.OpenRead(path);
                return Bitmap.DecodeToWidth(stream, 200);
            });
            if (bitmap is null)
            {
                return;
            }
            if (generation != _generation)
            {
                bitmap.Dispose();
                return;
            }
            (image.Source as IDisposable)?.Dispose();
            image.Source = bitmap;
            image.IsVisible = true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Artwork: current-art preview failed: {ex.Message}");
        }
    }

    private async Task LoadThumbAsync(Image image, string url, int generation)
    {
        await ThumbnailGate.WaitAsync();
        try
        {
            var bytes = await SteamGridDb.DownloadImageAsync(url);
            if (generation != _generation || bytes is null || bytes.Length == 0)
            {
                return;
            }
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                try
                {
                    using var stream = new MemoryStream(bytes);
                    var bitmap = Bitmap.DecodeToWidth(stream, 300);
                    if (generation == _generation)
                    {
                        (image.Source as IDisposable)?.Dispose();
                        image.Source = bitmap;
                    }
                    else
                    {
                        bitmap.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn($"Artwork: thumb decode failed: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"Artwork: thumbnail load failed: {ex.Message}");
        }
        finally
        {
            ThumbnailGate.Release();
        }
    }

    // ---- Apply / reset ----

    private void Apply(ArtworkAsset asset, SgdbAsset? art) => _ = RunSafelyAsync(ApplyAsync(asset, art), "apply");

    private async Task ApplyAsync(ArtworkAsset asset, SgdbAsset? art)
    {
        var generation = _generation;
        var targetAppId = _appId;
        Navigate(() => RenderMessage(AssetLabel(asset),
            art is null ? "Resetting to official art…" : "Applying artwork…"));

        ArtworkResult result;
        try
        {
            if (art is null)
            {
                result = await SteamArtwork.ClearAsync(targetAppId, asset);
            }
            else
            {
                var bytes = await SteamGridDb.DownloadImageAsync(art.Url);
                if (bytes is null || bytes.Length == 0)
                {
                    result = new ArtworkResult(false, "Could not download the image.");
                }
                else
                {
                    result = await SteamArtwork.ApplyAsync(targetAppId, asset, bytes, art.Extension);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Artwork apply failed.", ex);
            result = new ArtworkResult(false, "Something went wrong — see the log.");
        }

        if (generation != _generation || targetAppId != _appId)
        {
            return;
        }
        // No continue screen: land straight back on the changer's overview with the
        // outcome as a one-line notice, ready for the next change or Back to leave.
        _notice = result.Detail;
        _stack.Clear();
        Replace(RenderAssetTypes);
    }

    // ---- Shared builders (mirrors LibraryTabsView) ----

    private void PopIfAny()
    {
        if (_stack.Count > 0)
        {
            _stack.Pop();
        }
    }

    private async Task<IReadOnlyList<SteamCollections.AppInfo>> SafeGamesAsync()
    {
        try
        {
            return await SteamCollections.GetGamesAsync();
        }
        catch (Exception ex)
        {
            Log.Warn($"Artwork: games list failed: {ex.Message}");
            return Array.Empty<SteamCollections.AppInfo>();
        }
    }

    private static async Task RunSafelyAsync(Task task, string operation)
    {
        try { await task; }
        catch (Exception ex) { Log.Error($"Artwork {operation} failed.", ex); }
    }

    private string NameFor(long appId)
        => _games?.FirstOrDefault(g => g.AppId == appId)?.Name ?? $"App {appId}";

    // Prefer Steam's own flag (live-verified BIsShortcut in the games list); the
    // numeric check covers an id missing from the list — shortcut ids carry the
    // high bit (>= 2^31), real store appids never do.
    private bool IsShortcutApp(long appId)
        => _games?.FirstOrDefault(g => g.AppId == appId)?.Shortcut ?? appId >= 0x80000000L;

    // Persist the association only for shortcuts: a normal game's id already IS its
    // SGDB lookup key, and pinning a manual-search override for it could silently
    // outlive a one-off misdetection workaround.
    private void RememberSgdbLink(int sgdbGameId, string name)
    {
        if (!IsShortcutApp(_appId))
        {
            return;
        }
        _sgdbLinks[_appId] = (sgdbGameId, name);
        var appId = _appId;
        _ = LibraryTabManager.MutateConfigAsync<object?>(config =>
        {
            config.SgdbLinks.RemoveAll(l => l.AppId == appId);
            config.SgdbLinks.Add(new SgdbLinkConfig
            {
                AppId = appId,
                SgdbGameId = sgdbGameId,
                Name = name,
            });
            return null;
        });
    }

    private static string AssetLabel(ArtworkAsset asset)
        => Assets.FirstOrDefault(a => a.Asset == asset).Label ?? asset.ToString();

    private static (double W, double H) ThumbSize(ArtworkAsset asset) => asset switch
    {
        ArtworkAsset.Grid => (120, 180),
        ArtworkAsset.Hero => (260, 96),
        ArtworkAsset.Logo => (160, 96),
        ArtworkAsset.Wide => (200, 94),
        ArtworkAsset.Icon => (80, 80),
        _ => (120, 180),
    };

    private StackPanel NewStack(string heading)
    {
        var stack = new StackPanel { Spacing = 4 };
        if (!string.IsNullOrEmpty(heading))
        {
            stack.Children.Add(new TextBlock
            {
                Text = heading,
                FontSize = 15,
                FontWeight = FontWeight.SemiBold,
                Margin = new Avalonia.Thickness(0, 0, 0, 4),
            });
        }
        return stack;
    }

    private void RenderMessage(string heading, string message)
    {
        var stack = NewStack(heading);
        stack.Children.Add(Caption(message));
        SetContent(stack);
    }

    private CardButton Row(string title, string desc, Geometry? icon, Action onClick)
    {
        var button = new CardButton { Title = title, Description = desc, IconGeometry = icon };
        button.Click += (_, _) => onClick();
        return button;
    }

    private CardButton PrimaryRow(string title, string desc, Geometry? icon, Action onClick)
    {
        var button = Row(title, desc, icon, onClick);
        button.Classes.Add("primary");
        return button;
    }

    private TextBlock Caption(string text) => new()
    {
        Text = text,
        Classes = { "caption" },
        TextWrapping = TextWrapping.Wrap,
        Margin = new Avalonia.Thickness(2, 0, 2, 4),
    };

    private TextBlock SectionLabel(string text) => new()
    {
        Text = text,
        Classes = { "eyebrow" },
        Margin = new Avalonia.Thickness(2, 6, 2, 2),
    };

    // Content goes straight into the host (no inner ScrollViewer): the overlay's
    // ContentScroller owns scrolling and its GotFocus→BringIntoView keeps the focused
    // control — including on-screen-keyboard keys — on screen (Codex's fix). A nested
    // scroller would swallow that scroll-into-view.
    private void SetContent(StackPanel stack)
    {
        if (Content is Control previous)
        {
            DisposeImages(previous);
        }
        Content = stack;
        FocusFirst(stack);
    }

    private static void DisposeImages(Control root)
    {
        if (root is Image image)
        {
            (image.Source as IDisposable)?.Dispose();
            image.Source = null;
        }
        if (root is Panel panel)
        {
            foreach (var child in panel.Children.OfType<Control>())
            {
                DisposeImages(child);
            }
        }
        else if (root is ContentControl { Content: Control child })
        {
            DisposeImages(child);
        }
    }

    private void FocusFirst(StackPanel stack) => Dispatcher.UIThread.Post(() =>
    {
        foreach (var child in stack.Children)
        {
            if (child is Button { IsEffectivelyEnabled: true } b)
            {
                b.Focus(NavigationMethod.Directional);
                return;
            }
            if (child is WrapPanel wrap)
            {
                foreach (var wc in wrap.Children)
                {
                    if (wc is Button wb)
                    {
                        wb.Focus(NavigationMethod.Directional);
                        return;
                    }
                }
            }
        }
    });
}
