using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SteamUiToolkit;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Input;
using WSGM.Overlay;
using WSGM.Shell;
using WSGM.UiTests.Infrastructure;

namespace WSGM.UiTests.Visual;

/// <summary>
///     Render-only Game Tools captures. The real service runs without Steam, network or GPU access; each case
///     seeds its published state, so no game folder is scanned and nothing is installed.
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
            var root = Path.Combine(Path.GetTempPath(), "wsgm-game-tools-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            using var fixture = new UiFixture();
            var service = new GameToolsService(new UserDataContext(root, @"Local\WSGM.UiTests.GameTools." +
                                                                         Guid.NewGuid().ToString("N")),
                () => null, _ => false, new RefusingHandler(), () => []);
            try
            {
                Seed(service, scenario);
                var window = PreviewTools.Create(fixture, width, height);
                var view = UiFixture.Named<GameToolsView>(window, "GameToolsHost");
                SetField(view, "_games", new List<SteamLibraryApp>
                {
                    new(AppId, "ELDEN RING"),
                    new(1091500, "Cyberpunk 2077"),
                    new(2050650, "Resident Evil 4"),
                    new(3000000001, "A non-Steam game with a very long name that should wrap or trim cleanly", true),
                    new(1174180, "Red Dead Redemption 2"),
                    new(990080, "Hogwarts Legacy")
                });
                SetField(view, "_gamesRead", true);
                SetField(view, "_resolveCurrent", false);
                window.AttachGameTools(service);
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
            finally
            {
                service.Dispose();
            }
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

    // Holding the service gate keeps ReadAsync waiting, so the page shows the seeded state instead of a scan.
    private static void Seed(GameToolsService service, string scenario)
    {
        ((SemaphoreSlim)GetField(service, "_gate")).Wait();
        var state = State(scenario);
        if (state is null)
        {
            return;
        }

        ((Dictionary<uint, GameToolsState>)GetField(service, "_states"))[AppId] = state with { Busy = null };
        if (state.Busy is { } busy)
        {
            SetField(service, "_busy", busy);
        }

        if (state.Loading)
        {
            ((HashSet<uint>)GetField(service, "_loading")).Add(AppId);
        }

        if (state.Error is not null || state.Notice is not null)
        {
            ((Dictionary<uint, (string? Error, string? Notice)>)GetField(service, "_messages"))[AppId] =
                (state.Error, state.Notice);
        }
    }

    private static GameToolsState? State(string scenario)
    {
        const string folder = @"D:\SteamLibrary\steamapps\common\ELDEN RING";
        var runtime = new GameToolsRuntime("Game/eldenring.exe", "eldenring.exe", "x64", "structural",
            ["Named like the game", "Largest program in the folder"]);
        GameToolsCandidate[] candidates =
        [
            new("Game/eldenring.exe", "eldenring.exe", "x64", "unknown", "game",
                ["Named like the game", "Largest program in the folder"], 25, true),
            new("Game/start_protected_game.exe", "start_protected_game.exe", "x64", "unknown", "launcher",
                ["Starts the anti-cheat"], 4, false),
            new("Game/EasyAntiCheat/EasyAntiCheat_EOS_Setup.exe", "EasyAntiCheat_EOS_Setup.exe", "x86", "unknown",
                "helper", ["Installer"], -10, false)
        ];
        var ready = new GameToolsState(AppId, "ELDEN RING", false, folder, "ready", null, false, "unknown",
            "Unknown engine", runtime, candidates, false, false,
            [new GameToolsUpscaler("FSR 3", "fsr", "upscaler", "Game/amd_fidelityfx_dx12.dll", "3.1.2.0")],
            [], ["amd"], [Tool()], null, null, null);
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
                        Compatible = false, IncompatibleReason = "Choose the game's program first.", Actions = []
                    }
                ]
            },
            "game-tools-conflict" or "game-tools-conflict-end" => ready with
            {
                AntiCheat = ["Easy Anti-Cheat"],
                Error = "OptiScaler is already installed outside WSGM (dxgi.dll). Use \"Replace existing installation\" to let WSGM take over.",
                Tools =
                [
                    Tool() with
                    {
                        Status = "conflict", StatusText = "Installed outside WSGM.",
                        Conflicts =
                        [
                            new GameToolsConflict("dxgi.dll", "unmanagedTool",
                                "OptiScaler 0.9.4-final installed outside WSGM."),
                            new GameToolsConflict("winmm.dll", "otherMod", "ReShade uses this name.")
                        ],
                        Warnings =
                        [
                            "Do not use with online games. Anti-cheat may ban accounts. Found: Easy Anti-Cheat."
                        ],
                        Actions = ["replaceExisting"], RequiresRiskAcknowledgement = true,
                        RiskWarning = "Do not use with online games. Anti-cheat may ban accounts."
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
                        InstalledVersion = "0.9.4-final (7534ad0)", Actions = [],
                        Files = new GameToolsFiles("Game", 6, 1, [], [])
                    }
                ]
            },
            "game-tools-unavailable" => ready with
            {
                Availability = "folderUnavailable",
                Reason = "The game's folder isn't available. Connect the drive or library it is on.",
                Runtime = null, Candidates = [], Upscalers = [],
                Tools = [Tool() with { Status = "unavailable", StatusText = "The game's folder isn't available.", Actions = [] }]
            },
            "game-tools-busy" => ready with
            {
                Busy = new GameToolsBusy(AppId, "optiscaler", "install", "Downloading OptiScaler", 12_582_912,
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
            "WSGM is not affiliated with OptiScaler; report problems to WSGM.", true, null, "notInstalled",
            "Not installed.", null, "v0.9.4", null, false, false, "dxgi.dll",
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
            [], [], ["install"], false, null, new GameToolsFiles("Game", 0, 0, [], []));
    }

    private static object GetField(object target, string name)
    {
        return Field(target, name).GetValue(target)!;
    }

    private static void SetField(object target, string name, object? value)
    {
        Field(target, name).SetValue(target, value);
    }

    private static FieldInfo Field(object target, string name)
    {
        return target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
               ?? throw new InvalidOperationException($"{target.GetType().Name} has no field {name}.");
    }

    private sealed class RefusingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            throw new HttpRequestException("Previews never use the network.");
        }
    }
}
