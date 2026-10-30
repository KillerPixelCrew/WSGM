using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
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
///     Render-only Game Tools captures. An explicit fake owner publishes each case's state and a fixed library
///     stands in for Steam, so no game folder is scanned, nothing is installed and nothing is downloaded.
/// </summary>
public static class PreviewGameTools
{
    private const uint AppId = 1245620;

    private static readonly string[] Scenarios =
    [
        "game-tools-list", "game-tools-ready", "game-tools-ready-end", "game-tools-proxy", "game-tools-update",
        "game-tools-update-end", "game-tools-choice", "game-tools-candidates", "game-tools-conflict",
        "game-tools-conflict-end", "game-tools-running", "game-tools-unavailable", "game-tools-busy",
        "game-tools-loading", "game-tools-ready-tool", "game-tools-update-tool", "game-tools-running-tool"
    ];

    /// <summary>Exports every Game Tools case at one viewport.</summary>
    /// <param name="directory">The output folder.</param>
    /// <param name="width">The viewport width in DIPs.</param>
    /// <param name="height">The viewport height in DIPs.</param>
    public static void Export(string directory, int width, int height)
    {
        Directory.CreateDirectory(directory);
        foreach (var scenario in Scenarios)
        {
            using var fixture = new UiFixture();
            var source = new FakeGameToolsSource(State(scenario));
            var window = PreviewTools.Create(fixture, width, height);
            var view = UiFixture.Named<GameToolsView>(window, "GameToolsHost");
            // Without a Steam client the page finds no open game and shows this library.
            view.LibraryReader = () => Task.FromResult(new OverlayLibraryResult(
            [
                new SteamLibraryApp(AppId, "ELDEN RING"),
                new SteamLibraryApp(1091500, "Cyberpunk 2077"),
                new SteamLibraryApp(2050650, "Resident Evil 4"),
                new SteamLibraryApp(3000000001,
                    "A non-Steam game with a very long name that should wrap or trim cleanly", true),
                new SteamLibraryApp(1174180, "Red Dead Redemption 2"),
                new SteamLibraryApp(990080, "Hogwarts Legacy")
            ], null));
            window.AttachGameTools(source);
            ((OverlayViewModel)window.DataContext!).ShowGameTools = true;
            Dispatcher.UIThread.RunJobs();
            UiFixture.Tab(window, 1).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            UiFixture.Click(window, UiFixture.Rail(window, OverlayPage.SteamGameTools));
            Dispatcher.UIThread.RunJobs();
            if (scenario != "game-tools-list")
            {
                Click(window, "ELDEN RING");
            }

            if (scenario == "game-tools-proxy")
            {
                Click(window, "File name: dxgi.dll");
            }

            if (scenario == "game-tools-candidates")
            {
                Click(window, "Choose the program…");
            }

            Dispatcher.UIThread.RunJobs();
            window.FocusManager!.Focus(null);
            if (scenario.EndsWith("-end", StringComparison.Ordinal))
            {
                var back = FocusSearch.First<ActionButton>(window,
                               button => button.Title == "Back" && button.IsEffectivelyVisible)
                           ?? throw new InvalidOperationException("Back is missing.");
                back.BringIntoView();
                Dispatcher.UIThread.RunJobs();
            }

            if (scenario.EndsWith("-tool", StringComparison.Ordinal))
            {
                // The tool card's middle: its actions sit just above the file name row.
                var name = FocusSearch.First<ActionButton>(window,
                               button => button.Title?.StartsWith("File name:", StringComparison.Ordinal) == true
                                         && button.IsEffectivelyVisible)
                           ?? throw new InvalidOperationException("The file name row is missing.");
                name.BringIntoView();
                Dispatcher.UIThread.RunJobs();
            }

            window.MouseMove(new Point(-20, -20));
            using var image = window.CaptureRenderedFrame() ??
                              throw new InvalidOperationException("No Game Tools preview was rendered.");
            image.Save(Path.Combine(directory, $"{scenario}-{width}x{height}.png"), new PngBitmapEncoderOptions());
            window.Close();
        }
    }

    private static void Click(Window window, string title)
    {
        var button = FocusSearch.First<ActionButton>(window,
                         button => button.Title == title && button.IsEffectivelyVisible)
                     ?? throw new InvalidOperationException("Preview action is missing: " + title + ". Visible: "
                         + string.Join(" | ", window.GetVisualDescendants().OfType<ActionButton>()
                             .Where(candidate => candidate.IsEffectivelyVisible).Select(candidate => candidate.Title)));
        UiFixture.Click(window, button);
        Dispatcher.UIThread.RunJobs();
    }

    private static GameToolsState? State(string scenario)
    {
        const string folder = @"D:\SteamLibrary\steamapps\common\ELDEN RING";
        var runtime = new GameToolsRuntime("Game/eldenring.exe", "eldenring.exe", "x64", "64-bit", "structural",
            "Identified from the folder layout or Steam's launch target");
        GameToolsCandidate[] candidates =
        [
            new("Game/eldenring.exe", "eldenring.exe", "x64", "64-bit", "game", "Game",
                ["Named like the game", "Largest program in the folder"], true),
            new("Game/start_protected_game.exe", "start_protected_game.exe", "x64", "64-bit", "launcher",
                "Launcher or settings", ["Starts the anti-cheat"], false),
            new("Game/EasyAntiCheat/EasyAntiCheat_EOS_Setup.exe", "EasyAntiCheat_EOS_Setup.exe", "x86", "32-bit",
                "helper", "Installer or helper", ["Installer"], false)
        ];
        var ready = new GameToolsState(AppId, "ELDEN RING", false, folder, "ready", null, false,
            "Unknown engine", runtime, candidates, false, false,
            [new GameToolsUpscaler("FSR 3", "Game/amd_fidelityfx_dx12.dll", "3.1.2.0")],
            [], null, ["amd"], [Tool()], null, null, null);
        return scenario switch
        {
            "game-tools-list" => null,
            "game-tools-ready" or "game-tools-ready-end" or "game-tools-ready-tool" or "game-tools-proxy" => ready,
            "game-tools-update" or "game-tools-update-end" or "game-tools-update-tool" => ready with
            {
                Notice = "Checked for updates.",
                Tools =
                [
                    Tool() with
                    {
                        Status = "updateAvailable", StatusText = "Version v0.9.5 is available.",
                        InstalledVersion = "0.9.4-final (7534ad0)", LatestVersion = "v0.9.5",
                        UpdateChangesConfig = true,
                        Warnings = ["This update changes the settings file format. Your OptiScaler.ini is replaced."],
                        Actions = ["update", "repair", "configure", "remove"],
                        Files = new GameToolsFiles("Game", 6, 1, [], [])
                    }
                ]
            },
            "game-tools-choice" or "game-tools-candidates" => ready with
            {
                Runtime = null, RememberedMissing = true, ScanTruncated = true,
                Candidates = [.. candidates.Select(candidate => candidate with { Chosen = false })],
                Tools =
                [
                    Tool() with
                    {
                        IncompatibleReason = "Choose the game's program first.", Actions = [],
                        ShowOptions = false, CanEditOptions = false
                    }
                ]
            },
            "game-tools-conflict" or "game-tools-conflict-end" => ready with
            {
                AntiCheat = ["Easy Anti-Cheat"],
                AntiCheatWarning = "Anti-cheat found: Easy Anti-Cheat. Do not use with online games. Anti-cheat may ban accounts.",
                Error = "OptiScaler is already installed outside WSGM (dxgi.dll). Use \"Replace existing installation\" to let WSGM take over.",
                Tools =
                [
                    Tool() with
                    {
                        Status = "conflict", StatusText = "Installed outside WSGM.",
                        Conflicts =
                        [
                            new GameToolsConflict("dxgi.dll", "unmanagedTool", "Installed outside WSGM",
                                "OptiScaler 0.9.4-final installed outside WSGM."),
                            new GameToolsConflict("winmm.dll", "needsReview", "Needs review", "ReShade uses this name.")
                        ],
                        Actions = ["replaceExisting"], CanEditOptions = false
                    }
                ]
            },
            "game-tools-running" or "game-tools-running-tool" => ready with
            {
                Availability = "running", Reason = "Close the game to change its tools.",
                Tools =
                [
                    Tool() with
                    {
                        Status = "installed", StatusText = "Installed as dxgi.dll.",
                        InstalledVersion = "0.9.4-final (7534ad0)", Actions = [], CanEditOptions = false,
                        Files = new GameToolsFiles("Game", 6, 1, [], [])
                    }
                ]
            },
            "game-tools-unavailable" => ready with
            {
                Availability = "folderUnavailable",
                Reason = "The game's folder isn't available. Connect the drive or library it is on.",
                Runtime = null, Candidates = [], Upscalers = [],
                Tools =
                [
                    Tool() with
                    {
                        Status = "unavailable", StatusText = "The game's folder isn't available.", Actions = [],
                        ShowOptions = false, CanEditOptions = false
                    }
                ]
            },
            "game-tools-busy" => ready with
            {
                Busy = new GameToolsBusy(AppId, "install", "Downloading OptiScaler", 12_582_912,
                    31_457_280, true)
            },
            "game-tools-loading" => ready with { Loading = true, Tools = [], Candidates = [], Runtime = null },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null)
        };
    }

    private static GameToolsTool Tool()
    {
        return new GameToolsTool("optiscaler", "OptiScaler",
            "Swaps a game's DLSS, FSR or XeSS upscaler for another one, and can add frame generation.",
            "https://github.com/optiscaler/OptiScaler", "GPL-3.0",
            "WSGM is not affiliated with OptiScaler; report problems to WSGM.", null, "notInstalled",
            "Not installed.", null, "v0.9.4", null, false, false, "dxgi.dll",
            "OptiScaler loads by taking the name of a Windows library the game loads. Try the first one first.",
            [
                new GameToolsProxyOption("dxgi.dll", "Most compatible. Try this first.", true, null),
                new GameToolsProxyOption("winmm.dll", "Use for Vulkan games.", false, "ReShade uses this name."),
                new GameToolsProxyOption("version.dll", "Try when dxgi.dll doesn't load.", true, null),
                new GameToolsProxyOption("dbghelp.dll", "Try when dxgi.dll doesn't load.", true, null),
                new GameToolsProxyOption("d3d12.dll", "For DirectX 12 games that ignore dxgi.dll.", true, null),
                new GameToolsProxyOption("wininet.dll", "Last resort.", true, null),
                new GameToolsProxyOption("winhttp.dll", "Last resort.", true, null),
                new GameToolsProxyOption("OptiScaler.asi", "Needs an ASI loader.", true, null)
            ],
            [
                new GameToolsSetting("dlssInputs", "DLSS inputs on AMD and Intel",
                    "Lets the game offer DLSS so OptiScaler can replace it.", "on", null,
                    [new GameToolsSettingOption("on", "On"), new GameToolsSettingOption("off", "Off")], true, null),
                new GameToolsSetting("menuScale", "Menu size", "Scales OptiScaler's in-game menu.", "auto", null,
                    [
                        new GameToolsSettingOption("auto", "Automatic"), new GameToolsSettingOption("1.0", "100%"),
                        new GameToolsSettingOption("1.5", "150%")
                    ], true, null),
                new GameToolsSetting("hideSplash", "Hide the start message", "Hides OptiScaler's splash text.", "off",
                    null, [new GameToolsSettingOption("off", "Off"), new GameToolsSettingOption("on", "On")], true, null)
            ],
            true, true, [], [], ["install"], new GameToolsFiles("Game", 0, 0, [], []));
    }

    /// <summary>
    ///     A Game Tools owner that publishes one seeded state and admits every command without doing anything,
    ///     so the page draws exactly the case.
    /// </summary>
    private sealed class FakeGameToolsSource(GameToolsState? state) : IGameToolsSource
    {
        /// <inheritdoc />
        public event Action? Changed
        {
            add { }
            remove { }
        }

        /// <inheritdoc />
        public GameToolsState Peek(uint appId)
        {
            return state is not null && appId == state.AppId
                ? state
                : throw new InvalidOperationException("The preview has no state for game " + appId + ".");
        }

        /// <inheritdoc />
        public Task<GameToolsState> ReadAsync(uint appId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Peek(appId));
        }

        /// <inheritdoc />
        public Task<SteamUiCommandResult> RefreshAsync(uint appId, CancellationToken cancellationToken)
        {
            return Admitted();
        }

        /// <inheritdoc />
        public Task<SteamUiCommandResult> CheckUpdatesAsync(uint appId, CancellationToken cancellationToken)
        {
            return Admitted();
        }

        /// <inheritdoc />
        public Task<SteamUiCommandResult> ChooseRuntimeAsync(uint appId, string path,
            CancellationToken cancellationToken)
        {
            return Admitted();
        }

        /// <inheritdoc />
        public Task<SteamUiCommandResult> InstallAsync(GameToolsInstallRequest request,
            CancellationToken cancellationToken)
        {
            return Admitted();
        }

        /// <inheritdoc />
        public Task<SteamUiCommandResult> UpdateAsync(uint appId, string toolId, bool replaceConfig,
            CancellationToken cancellationToken)
        {
            return Admitted();
        }

        /// <inheritdoc />
        public Task<SteamUiCommandResult> RepairAsync(uint appId, string toolId, CancellationToken cancellationToken)
        {
            return Admitted();
        }

        /// <inheritdoc />
        public Task<SteamUiCommandResult> ConfigureAsync(uint appId, string toolId, string? proxyName,
            IReadOnlyDictionary<string, string> settings, CancellationToken cancellationToken)
        {
            return Admitted();
        }

        /// <inheritdoc />
        public Task<SteamUiCommandResult> RemoveAsync(uint appId, string toolId, CancellationToken cancellationToken)
        {
            return Admitted();
        }

        /// <inheritdoc />
        public Task<SteamUiCommandResult> CancelAsync(uint appId, CancellationToken cancellationToken)
        {
            return Admitted();
        }

        private static Task<SteamUiCommandResult> Admitted()
        {
            return Task.FromResult(SteamUiCommandResult.Applied);
        }
    }
}
