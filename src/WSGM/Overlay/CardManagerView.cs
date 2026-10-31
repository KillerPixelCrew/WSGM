using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
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
///     Steam or by a card insertion redraws the list here. The view reaches it through
///     <see cref="ICardManagerSource" />, so a headless capture supplies an explicit fake.
/// </summary>
public sealed class CardManagerView : ServiceSubView
{
    private ICardManagerSource? _service;

    /// <inheritdoc />
    protected override string LogScope => "Card manager";

    private ICardManagerSource Service =>
        _service ?? throw new InvalidOperationException("The session library manager is unavailable.");

    /// <summary>Attaches the view to the session's shared manager, or detaches it with null.</summary>
    /// <param name="service">
    ///     The session's <see cref="LibraryManagerService" />, or an explicit fake in a headless capture; borrowed,
    ///     null when the overlay closes or the session has none.
    /// </param>
    internal void Attach(ICardManagerSource? service)
    {
        if (ReferenceEquals(_service, service))
        {
            return;
        }

        _service = service;
        AttachSource(service);
        if (service is null)
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
        base.Open();
        if (_service is not null)
        {
            // The list shows the last reading at once; the scan replaces it through Changed.
            _ = RunSafelyAsync(_service.RefreshAsync(), "card list");
        }
    }

    // ---- Level: card list ----

    // A change from Steam's page, from a card insertion or from this view's own operation redraws
    // the current level in place, keeping the finger on the same card or row.
    private protected override void RenderHome()
    {
        if (_service is null)
        {
            RenderMessage("Card Manager", "Card management is unavailable in this session.");
            return;
        }

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
            if (c.HasCapacity)
            {
                // Inside the card's own row, under the amounts it draws, as Steam's page shows it: a bar
                // between two rows reads as a divider and belongs to neither.
                var meter = OverlayProgress.UsageMeter(c.UsedBytes, c.CapacityBytes, c.Name + " space used");
                meter.Margin = new Thickness(0, 2, 0, 2);
                row.Footer = meter;
            }

            stack.Children.Add(row);
        }

        stack.Children.Add(SectionLabel(""));
        stack.Children.Add(Row("Back", "Return to Steam", Icons.ExitFullscreen, () => Back()));
        SetContent(stack);
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
        if (Service.ReadCardState().Cards.FirstOrDefault(c => c.ContentId == contentId) is not { } card)
        {
            // Forgotten on the other surface while this editor was open. Back first, so the notice
            // redraws the card list rather than this editor again.
            Back();
            Toast("That card is no longer tracked.");
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
                () => Service.RenameCardAsync(card.ContentId, v)))));
        stack.Children.Add(ToggleRow("Steam tab", card.Enabled, enabled =>
            _ = RunCardMutationAsync(() => Service.SetCardEnabledAsync(card.ContentId, enabled))));
        stack.Children.Add(ChoiceRow("Hidden", [(false, "No"), (true, "Yes")], card.Hidden, hidden =>
            _ = RunCardMutationAsync(() => Service.SetCardHiddenAsync(card.ContentId, hidden))));
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
            () => _ = RunCardMutationAsync(() => Service.ForgetCardAsync(card.ContentId))));
        stack.Children.Add(Row("Cancel", "Keep the card", Icons.ExitFullscreen, () => Back()));
        SetContent(stack);
    }

    // An applied change drops the editor and any confirmation, landing on a freshly drawn card list.
    private async Task RunCardMutationAsync(Func<Task<SteamUiCommandResult>> mutation)
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
            var notice = Service.ReadCardState().Notice;
            if (notice is not null)
            {
                _ = Service.DismissAsync();
            }

            NavigationStack.Clear();
            NavigationGeneration++;
            Replace(RenderHome);
            if (notice is not null)
            {
                Toast(notice);
            }
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

        // A new level over the loading message, drawn afresh rather than reconciled into it.
        NavigationGeneration++;
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
