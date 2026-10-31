using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using SteamUiToolkit;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>
///     The gamepad-driven SD-card library manager, hosted as a Tools sub-view of
///     the overlay (the <c>PanelFormat</c> idiom): rename, hide, enable, inspect, and
///     forget tracked card libraries. Split out of <see cref="LibraryTabsView" /> — cards
///     are their own thing, not a tab-builder level. The view is one projection of
///     <see cref="LibraryManagerService" />, which Steam's SD Card Manager page projects too:
///     every operation, the card model and its capacity come from there, and a change made in
///     Steam or by a card insertion redraws the list here.
/// </summary>
public sealed class CardManagerView : OverlaySubView
{
    private bool _listShown;

    private LibraryManagerService? _service;

    /// <inheritdoc />
    protected override string LogScope => "Card manager";

    private LibraryManagerService Service =>
        _service ?? throw new InvalidOperationException("The session library manager is unavailable.");

    /// <summary>Attaches the view to the session's shared manager, or detaches it with null.</summary>
    /// <param name="service">The session's service, borrowed; null when the overlay closes or the session has none.</param>
    internal void Attach(LibraryManagerService? service)
    {
        if (ReferenceEquals(_service, service))
        {
            return;
        }

        if (_service is not null)
        {
            _service.Changed -= OnServiceChanged;
        }

        _service = service;
        if (service is not null)
        {
            service.Changed += OnServiceChanged;
        }
        else
        {
            Leave();
        }
    }

    /// <summary>
    ///     Resets navigation and renders the card list. Called by the overlay
    ///     when the sub-view opens.
    /// </summary>
    public override void Open()
    {
        NavigationStack.Clear();
        CurrentLevel = null;
        if (_service is null)
        {
            RenderMessage("Card Manager", "Card management is unavailable in this session.");
            return;
        }

        Navigate(RenderCardList);
        // The list shows the last reading at once; the scan replaces it through Changed.
        _ = RunSafelyAsync(Service.RefreshAsync(), "card list");
    }

    /// <inheritdoc />
    internal override void Leave()
    {
        _listShown = false;
        base.Leave();
    }

    // A change from Steam's page, from a card insertion or from this view's own operation: the
    // list redraws in place, keeping the finger on the same card. Deeper levels redraw after their
    // own operation, so a card ejected while its editor is open does not move the focus.
    private void OnServiceChanged()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_service is not null && _listShown && IsVisible && Content is not null)
            {
                RenderCardList(FocusedContentId());
            }
        });
    }

    private string? FocusedContentId()
    {
        return TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is ActionButton { Tag: string id }
            ? id
            : null;
    }

    // ---- Level: card list ----

    private void RenderCardList()
    {
        RenderCardList(null);
    }

    private void RenderCardList(string? focusContentId)
    {
        _listShown = true;
        var state = Service.ReadCardState();
        var stack = NewStack("Card Manager");
        if (!state.Loaded)
        {
            stack.Children.Add(Caption("Scanning cards…"));
        }
        else if (state.Error is not null && state.Cards.Count == 0)
        {
            // Never dress a read failure up as "you have no cards": that invites
            // re-formatting a card that is in fact still tracked.
            stack.Children.Add(Caption(state.Error));
        }
        else if (state.Cards.Count == 0)
        {
            stack.Children.Add(Caption(
                "No SD-card libraries tracked yet. Use Tools > Storage to format a card, or add a library first."));
        }

        foreach (var card in state.Cards)
        {
            var c = card;
            var row = Row(c.Name, Describe(c), Icons.SdCard, () => RenderCardEditor(c.ContentId));
            row.Tag = c.ContentId;
            stack.Children.Add(row);
        }

        stack.Children.Add(SectionLabel(""));
        stack.Children.Add(Row("Back", "Return to Steam", Icons.ExitFullscreen, () => Back()));
        SetContent(stack);
        if (focusContentId is null)
        {
            return;
        }

        // Posted after SetContent's own first-row focus post, so it wins: a card list redrawn under
        // the user keeps the focus on the card they were on.
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var child in stack.Children)
            {
                if (child is ActionButton { Tag: string id } button && id == focusContentId)
                {
                    button.Focus(NavigationMethod.Directional);
                    return;
                }
            }
        });
    }

    private static string Describe(LibraryCardView card)
    {
        var marker = card.Inserted ? "★ inserted" : "⦸ ejected";
        var state = $"{card.GameCount} games · {marker}";
        if (card.HasCapacity)
        {
            state += $" · {LibraryCardView.FormatBytes(card.FreeBytes)} free of "
                     + LibraryCardView.FormatBytes(card.CapacityBytes);
        }

        return state + (card.Enabled ? "" : " · tab off") + (card.Hidden ? " · hidden" : "");
    }

    // ---- Level: card editor ----

    private void RenderCardEditor(string contentId)
    {
        Navigate(() => DrawCardEditor(contentId));
    }

    private void DrawCardEditor(string contentId)
    {
        _listShown = false;
        if (Service.ReadCardState().Cards.FirstOrDefault(c => c.ContentId == contentId) is not { } card)
        {
            // Forgotten on the other surface while this editor was open.
            Toast("That card is no longer tracked.");
            Back();
            return;
        }

        var stack = NewStack(card.Name);
        stack.Children.Add(Caption(card.Inserted ? "Currently inserted." : "Not inserted (remembered)."));
        stack.Children.Add(Caption(card.HasCapacity
            ? $"{LibraryCardView.FormatBytes(card.UsedBytes)} used · "
              + $"{LibraryCardView.FormatBytes(card.FreeBytes)} free · "
              + $"{LibraryCardView.FormatBytes(card.CapacityBytes)} in all"
              + (card.Inserted ? "" : " (last reading)")
            : "Size not read yet; connect the card once."));
        stack.Children.Add(Row("Rename", card.Name, Icons.CopyDoc, () =>
            EditText("Card name", card.Name, 0, v => _ = RunCardMutationAsync(
                // Renames the drive itself first, then the tab, the Steam library label and the
                // Windows volume. A refusal (an unmounted library) comes back as the failure, a
                // partial write as the notice to show.
                () => Service.RenameCardAsync(card.ContentId, v), () =>
                {
                    // Drop the text-entry level and the card editor, landing on a fresh card list.
                    PopIfAny();
                    Replace(RenderCardList);
                }))));
        stack.Children.Add(ToggleRow("Steam tab", card.Enabled, enabled =>
            _ = RunCardMutationAsync(() => Service.SetCardEnabledAsync(card.ContentId, enabled), () =>
            {
                PopIfAny();
                Replace(RenderCardList);
            })));
        stack.Children.Add(ChoiceRow("Hidden", [(false, "No"), (true, "Yes")], card.Hidden, hidden =>
            _ = RunCardMutationAsync(() => Service.SetCardHiddenAsync(card.ContentId, hidden), () =>
            {
                PopIfAny();
                Replace(RenderCardList);
            })));
        stack.Children.Add(Row("View games", $"{card.GameCount} installed", Icons.Grid4,
            () => OpenGameList(card)));
        stack.Children.Add(SectionLabel(""));
        stack.Children.Add(DangerRow("Forget card", "Remove its tab and tracking", Icons.Close,
            () => Navigate(() => DrawForgetConfirmation(card))));
        stack.Children.Add(Row("Back", "Return to cards", Icons.ExitFullscreen, () => Back()));
        SetContent(stack);
    }

    // ---- Level: forget confirmation ----

    private void DrawForgetConfirmation(LibraryCardView card)
    {
        var stack = NewStack($"Forget {card.Name}?");
        stack.Children.Add(Caption("Its Steam tab and tracking are removed. The games on the card stay; "
                                   + "reinserting the card tracks it again as new."));
        stack.Children.Add(DangerRow("Forget card", "Remove its tab and tracking", Icons.Close,
            () => _ = RunCardMutationAsync(() => Service.ForgetCardAsync(card.ContentId), () =>
            {
                // Drop the confirmation and the editor, landing on a fresh card list.
                PopIfAny();
                PopIfAny();
                Replace(RenderCardList);
            })));
        stack.Children.Add(Row("Cancel", "Keep the card", Icons.ExitFullscreen, () => Back()));
        SetContent(stack);
    }

    private async Task RunCardMutationAsync(Func<Task<SteamUiCommandResult>> mutation, Action completed)
    {
        try
        {
            var result = await mutation();
            if (!result.Succeeded)
            {
                Toast(result.Error ?? "Could not save the card change. Try again.");
                return;
            }

            // A partial rename leaves a note; it is shown once here and cleared for Steam's page.
            if (Service.ReadCardState().Notice is { } notice)
            {
                _ = Service.DismissAsync();
                completed();
                Toast(notice);
                return;
            }

            completed();
        }
        catch (Exception ex)
        {
            Log.Warn($"Card manager change failed: {ex.Message}");
            Toast("Could not save the card change. Try again.");
        }
    }

    // ---- Level: game list ----

    private void OpenGameList(LibraryCardView card)
    {
        _ = RunSafelyAsync(OpenGameListAsync(card), "card games");
    }

    private async Task OpenGameListAsync(LibraryCardView card)
    {
        Navigate(() => RenderLoading(card.Name));
        var generation = NavigationGeneration;
        // A failed read shows the ids alone, as an empty library did before.
        var games = await Service.LoadCardGamesAsync(card.ContentId);
        if (generation != NavigationGeneration)
        {
            return;
        }

        Replace(() =>
        {
            var stack = NewStack($"{card.Name} — Games");
            if (games.Error is not null)
            {
                stack.Children.Add(Caption(games.Error));
            }

            if (games.Games.Count == 0)
            {
                stack.Children.Add(Caption("No games recorded on this card."));
            }

            foreach (var game in games.Games)
            {
                stack.Children.Add(Row(game.Name, "", null, null));
            }

            stack.Children.Add(SectionLabel(""));
            stack.Children.Add(Row("Back", "Return", Icons.ExitFullscreen, () => Back()));
            SetContent(stack);
        });
    }
}
