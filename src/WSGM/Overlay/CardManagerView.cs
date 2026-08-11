using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>The gamepad-driven SD-card library manager, hosted as a Tools sub-view of
/// the overlay (the <c>PanelFormat</c> idiom): rename, hide, enable, inspect, and
/// forget tracked card libraries. Split out of <see cref="LibraryTabsView"/> — cards
/// are their own thing, not a tab-builder level. All Steam contact goes through
/// <see cref="LibraryTabManager"/>; cards are keyed by content id, never by the
/// reader's (shared) drive letter.</summary>
public sealed class CardManagerView : OverlaySubView
{
    private LibraryTabManager _manager = new();

    // Lazily-loaded, cached Steam data for the game list.
    private IReadOnlyList<SteamCollections.AppInfo>? _games;

    /// <inheritdoc />
    protected override string LogScope => "Card manager";

    /// <summary>Resets navigation and renders the card list. Called by the overlay
    /// when the sub-view opens.</summary>
    /// <param name="manager">The shared library-tab manager.</param>
    public void Open(LibraryTabManager manager)
    {
        _manager = manager;
        _stack.Clear();
        _current = null;
        _games = null;
        Navigate(RenderCardList);
    }

    // ---- Level: card list ----

    private void RenderCardList() => _ = RunSafelyAsync(RenderCardListAsync(), "card list");

    private async Task RenderCardListAsync()
    {
        var generation = _navigationGeneration;
        SetContent(NewStack("Card Manager").Also(s => s.Children.Add(Caption("Scanning cards…"))));
        IReadOnlyList<LibraryTabManager.CardView> cards;
        try
        {
            cards = await _manager.ListCardsAsync();
            if (generation != _navigationGeneration)
            {
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Card list failed: {ex.Message}");
            cards = Array.Empty<LibraryTabManager.CardView>();
        }
        var stack = NewStack("Card Manager");
        if (cards.Count == 0)
        {
            stack.Children.Add(Caption("No SD-card libraries tracked yet. Format or add one first."));
        }
        foreach (var card in cards)
        {
            var c = card;
            var marker = c.Inserted ? "★ inserted" : "⦸ ejected";
            var state = $"{c.GameCount} games · {marker}" + (c.Enabled ? "" : " · tab off")
                + (c.Hidden ? " · hidden" : "");
            stack.Children.Add(Row(c.Name, state, Icons.SdCard, () => RenderCardEditor(c)));
        }
        stack.Children.Add(SectionLabel(""));
        stack.Children.Add(Row("Back", "Return to Tools", Icons.ExitFullscreen, () => Back()));
        SetContent(stack);
    }

    // ---- Level: card editor ----

    private void RenderCardEditor(LibraryTabManager.CardView card)
    {
        Navigate(() =>
        {
            var stack = NewStack(card.Name);
            stack.Children.Add(Caption(card.Inserted ? "Currently inserted." : "Not inserted (remembered)."));
            stack.Children.Add(Row("Rename", card.Name, Icons.CopyDoc, () =>
                EditText("Card name", card.Name, 40, v => _ = RunCardMutationAsync(
                    async () =>
                    {
                        // Renames the tab, the Steam library label, and the Windows
                        // volume; a partial failure comes back as a note to show.
                        var note = await _manager.RenameCardAsync(card.ContentId, v);
                        if (note is not null)
                        {
                            Toast(note);
                        }
                    }, () =>
                {
                    // Drop the text-entry level and the card editor, landing on a fresh
                    // card list.
                    PopIfAny();
                    Replace(RenderCardList);
                    _ = SyncQuietly();
                }))));
            stack.Children.Add(CycleRow("Steam tab", card.Enabled ? "On" : "Off", () =>
                _ = RunCardMutationAsync(
                    () => _manager.SetCardEnabledAsync(card.ContentId, !card.Enabled), () =>
            {
                PopIfAny();
                Replace(RenderCardList);
                _ = SyncQuietly();
            })));
            stack.Children.Add(CycleRow("Hidden", card.Hidden ? "Yes" : "No", () =>
                _ = RunCardMutationAsync(
                    () => _manager.SetCardHiddenAsync(card.ContentId, !card.Hidden), () =>
            {
                PopIfAny();
                Replace(RenderCardList);
                _ = SyncQuietly();
            })));
            stack.Children.Add(Row("View games", $"{card.GameCount} installed", Icons.Grid4,
                () => OpenGameList(card)));
            stack.Children.Add(SectionLabel(""));
            stack.Children.Add(DangerRow("Forget card", "Remove its tab and tracking", Icons.Close,
                () => _ = RunCardMutationAsync(
                    () => _manager.ForgetCardAsync(card.ContentId), () =>
                {
                    PopIfAny();
                    Replace(RenderCardList);
                    _ = SyncQuietly();
                })));
            stack.Children.Add(Row("Back", "Return to cards", Icons.ExitFullscreen, () => Back()));
            SetContent(stack);
        });
    }

    private async Task RunCardMutationAsync(Func<Task> mutation, Action completed)
    {
        try
        {
            await mutation();
            completed();
        }
        catch (Exception ex)
        {
            Log.Warn($"Card manager change failed: {ex.Message}");
            Toast("Could not save the card change. Try again.");
        }
    }

    // ---- Level: game list ----

    private void OpenGameList(LibraryTabManager.CardView card)
        => _ = RunSafelyAsync(OpenGameListAsync(card), "card games");

    private async Task OpenGameListAsync(LibraryTabManager.CardView card)
    {
        Navigate(() => RenderLoading(card.Name));
        var generation = _navigationGeneration;
        var loaded = await SteamCollections.GetGamesAsync();
        if (generation != _navigationGeneration)
        {
            return;
        }
        _games = loaded;
        var names = _games!.ToDictionary(g => g.AppId, g => g.Name);
        Replace(() =>
        {
            var stack = NewStack($"{card.Name} — Games");
            if (card.AppIds.Count == 0)
            {
                stack.Children.Add(Caption("No games recorded on this card."));
            }
            foreach (var id in card.AppIds)
            {
                stack.Children.Add(Row(names.TryGetValue(id, out var nm) ? nm
                    : id.ToString(CultureInfo.InvariantCulture), "", null, null));
            }
            stack.Children.Add(SectionLabel(""));
            stack.Children.Add(Row("Back", "Return", Icons.ExitFullscreen, () => Back()));
            SetContent(stack);
        });
    }

    // A card change (rename/enable/hide/forget) alters what Steam should show, so
    // re-materialize the tabs in the background; failures wait for the next sync.
    private async Task SyncQuietly()
    {
        try
        {
            var summary = await _manager.SyncAllAsync();
            Log.Info($"Card manager: {summary}");
        }
        catch (Exception ex)
        {
            Log.Warn($"Card manager sync failed: {ex.Message}");
        }
    }
}
