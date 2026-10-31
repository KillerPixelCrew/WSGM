using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SteamUiToolkit;
using WSGM.Controls;
using WSGM.Input;
using WSGM.Overlay;
using WSGM.Shell;
using WSGM.UiTests.Infrastructure;

namespace WSGM.UiTests.Visual;

/// <summary>
///     Render-only captures of the Overlay card manager. An explicit fake owner publishes each case's card state, so
///     no drive is scanned, no card is renamed and Steam is never asked. Every card and size is fictional.
/// </summary>
public static class PreviewCardManager
{
    private const long Gigabyte = 1024L * 1024 * 1024;

    // The service's own wording for a failed drive scan.
    private const string ReadError = "Couldn't read your cards. Try again.";

    private static readonly string[] Scenarios =
    [
        "card-manager-list", "card-manager-list-focus", "card-manager-list-end", "card-manager-long-name",
        "card-manager-empty", "card-manager-scanning", "card-manager-error", "card-manager-editor"
    ];

    /// <summary>Exports every card manager case at one viewport.</summary>
    /// <param name="directory">The output folder.</param>
    /// <param name="width">The viewport width in DIPs.</param>
    /// <param name="height">The viewport height in DIPs.</param>
    public static void Export(string directory, int width, int height)
    {
        Directory.CreateDirectory(directory);
        foreach (var scenario in Scenarios)
        {
            using var fixture = new UiFixture();
            var window = PreviewTools.Create(fixture, width, height);
            var source = new FakeCardManagerSource(State(scenario));
            UiFixture.Named<CardManagerView>(window, "CardManagerHost").Attach(source);
            ((OverlayViewModel)window.DataContext!).ShowCardManager = true;
            Dispatcher.UIThread.RunJobs();
            UiFixture.Tab(window, 1).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            UiFixture.Click(window, UiFixture.Rail(window, OverlayPage.SteamLibrary));
            Dispatcher.UIThread.RunJobs();
            var open = FocusSearch.First<ActionButton>(window,
                           button => Equals(button.Tag, "steam.card-manager") && button.IsEffectivelyVisible)
                       ?? throw new InvalidOperationException("The Card Manager entry is missing.");
            UiFixture.Click(window, open);
            Dispatcher.UIThread.RunJobs();
            Require(source.Refreshes == 1, "Opening the card manager did not ask for one scan.");
            switch (scenario)
            {
                case "card-manager-list" or "card-manager-list-focus" or "card-manager-list-end":
                    RequireMeters(window, 3);
                    // A change from Steam's page or a card insertion redraws the list in place.
                    source.Publish();
                    Dispatcher.UIThread.RunJobs();
                    RequireMeters(window, 3);
                    break;
                case "card-manager-long-name":
                    RequireMeters(window, 2);
                    break;
                case "card-manager-error":
                    RequireMeters(window, 0);
                    RequireCaption(window, ReadError);
                    break;
                case "card-manager-empty":
                    RequireMeters(window, 0);
                    break;
                case "card-manager-scanning":
                    RequireCaption(window, "Scanning cards…");
                    break;
                case "card-manager-editor":
                    Click(window, "Travel SSD");
                    break;
            }

            window.FocusManager!.Focus(null);
            if (scenario.EndsWith("-end", StringComparison.Ordinal))
            {
                var back = FocusSearch.First<ActionButton>(window,
                               button => button.Title == "Back" && button.IsEffectivelyVisible)
                           ?? throw new InvalidOperationException("Back is missing.");
                back.BringIntoView();
            }

            if (scenario == "card-manager-list-focus")
            {
                // The meter on the focused row's own surface, where a controller user reads it.
                var travel = FocusSearch.First<ActionButton>(window,
                                 button => Equals(button.Tag, "card-travel") && button.IsEffectivelyVisible)
                             ?? throw new InvalidOperationException("The Travel SSD row is missing.");
                travel.Focus(NavigationMethod.Directional);
            }

            Dispatcher.UIThread.RunJobs();
            window.MouseMove(new Point(-20, -20));
            using var image = window.CaptureRenderedFrame() ??
                              throw new InvalidOperationException("No card manager preview was rendered.");
            image.Save(Path.Combine(directory, $"{scenario}-{width}x{height}.png"), new PngBitmapEncoderOptions());
            window.Close();
        }
    }

    private static SteamCardManagerState State(string scenario)
    {
        LibraryCardView[] cards =
        [
            // Nothing installed yet: an empty bar.
            Card("card-retro", "Retro card", inserted: true, games: 0, capacity: 59.5, free: 59.5),
            // About half.
            Card("card-travel", "Travel SSD", inserted: true, games: 14, capacity: 476.9, free: 238.4),
            // Nearly full, and remembered from its last reading while it is out.
            Card("card-handheld", "Handheld microSD", inserted: false, games: 41, capacity: 931.5, free: 12.2) with
            {
                Enabled = false
            },
            // Never mounted with a size reading: no meter.
            Card("card-new", "New card", inserted: true, games: 0, capacity: 0, free: 0) with { Hidden = true }
        ];
        var loaded = new SteamCardManagerState(cards, Loaded: true, Busy: false, Error: null, Notice: null,
            Games: null, Revision: 1);
        return scenario switch
        {
            "card-manager-list" or "card-manager-list-focus" or "card-manager-list-end" or "card-manager-editor" =>
                loaded,
            "card-manager-long-name" => loaded with
            {
                Cards =
                [
                    Card("card-long",
                        "Extremely long removable storage library name for checking wrapping (Example 1 TB card)",
                        inserted: true, games: 128, capacity: 931.5, free: 401.7),
                    Card("card-path", @"E:\SteamLibrary\steamapps\common\A very deep library folder on the card",
                        inserted: false, games: 3, capacity: 238.4, free: 230.1) with
                    {
                        Enabled = false, Hidden = true
                    }
                ]
            },
            "card-manager-empty" => loaded with { Cards = [] },
            "card-manager-scanning" => loaded with { Cards = [], Loaded = false, Busy = true },
            "card-manager-error" => loaded with { Cards = [], Error = ReadError },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null)
        };
    }

    private static LibraryCardView Card(string id, string name, bool inserted, int games, double capacity,
        double free)
    {
        return new LibraryCardView(id, name, Enabled: true, Hidden: false, inserted, games,
            Enumerable.Range(1, games).Select(index => 1_000_000L + index).ToArray(),
            (long)(capacity * Gigabyte), (long)(free * Gigabyte));
    }

    private static void RequireMeters(Window window, int count)
    {
        var view = UiFixture.Named<CardManagerView>(window, "CardManagerHost");
        var meters = view.GetVisualDescendants().OfType<ProgressBar>()
            .Count(bar => bar.Classes.Contains("overlay-progress") && bar.IsEffectivelyVisible
                          && bar.FindAncestorOfType<ActionButton>() is not null);
        Require(meters == count, $"The card list shows {meters} capacity meters, not {count}.");
    }

    private static void RequireCaption(Window window, string text)
    {
        Require(window.GetVisualDescendants().OfType<TextBlock>()
                .Any(block => block.Text == text && block.IsEffectivelyVisible),
            "The card list does not show: " + text);
    }

    private static void Click(Window window, string title)
    {
        var button = FocusSearch.First<ActionButton>(window,
                         candidate => candidate.Title == title && candidate.IsEffectivelyVisible)
                     ?? throw new InvalidOperationException("Preview action is missing: " + title + ".");
        UiFixture.Click(window, button);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Require(bool condition, string failure)
    {
        if (!condition)
        {
            throw new InvalidOperationException(failure);
        }
    }

    /// <summary>
    ///     A card manager owner that publishes a seeded state, announces a change through <see cref="Publish" /> and
    ///     admits every command without doing anything, so the list draws exactly the case.
    /// </summary>
    private sealed class FakeCardManagerSource(SteamCardManagerState state) : ICardManagerSource
    {
        /// <summary>How many scans the view asked for.</summary>
        internal int Refreshes { get; private set; }

        /// <inheritdoc />
        public event Action? Changed;

        /// <inheritdoc />
        public SteamCardManagerState ReadCardState()
        {
            return state;
        }

        /// <inheritdoc />
        public Task RefreshAsync(CancellationToken cancellationToken = default)
        {
            Refreshes++;
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task<SteamUiCommandResult> DismissAsync(CancellationToken cancellationToken = default)
        {
            return Admitted();
        }

        /// <inheritdoc />
        public Task<SteamUiCommandResult> RenameCardAsync(string contentId, string name,
            CancellationToken cancellationToken = default)
        {
            return Admitted();
        }

        /// <inheritdoc />
        public Task<SteamUiCommandResult> SetCardEnabledAsync(string contentId, bool enabled,
            CancellationToken cancellationToken = default)
        {
            return Admitted();
        }

        /// <inheritdoc />
        public Task<SteamUiCommandResult> SetCardHiddenAsync(string contentId, bool hidden,
            CancellationToken cancellationToken = default)
        {
            return Admitted();
        }

        /// <inheritdoc />
        public Task<SteamUiCommandResult> ForgetCardAsync(string contentId,
            CancellationToken cancellationToken = default)
        {
            return Admitted();
        }

        /// <inheritdoc />
        public Task<LibraryCardGames> LoadCardGamesAsync(string contentId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new LibraryCardGames(contentId, [], Loading: false, Error: null));
        }

        /// <summary>Publishes a change, as the service does after one.</summary>
        internal void Publish()
        {
            Changed?.Invoke();
        }

        private static Task<SteamUiCommandResult> Admitted()
        {
            return Task.FromResult(SteamUiCommandResult.Applied);
        }
    }
}
