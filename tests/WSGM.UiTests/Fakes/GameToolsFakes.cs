using SkiaSharp;
using SteamUiToolkit;
using WSGM.Shell;
using WSGM.Testing;

namespace WSGM.UiTests.Fakes;

/// <summary>
///     A Game Tools owner that publishes seeded game states and a seeded game list, records every command and
///     admits it without doing anything, so a page draws exactly the case and no folder is scanned, nothing is
///     downloaded and nothing is installed.
/// </summary>
internal sealed class FakeGameToolsSource(IEnumerable<GameToolsState> states, GameToolsLibrary library)
    : IGameToolsSource
{
    private readonly SemaphoreSlim _sent = new(0);
    private readonly Dictionary<uint, GameToolsState> _states = states.ToDictionary(state => state.AppId);

    /// <summary>The game list published now.</summary>
    internal GameToolsLibrary Library { get; private set; } = library;

    /// <summary>Every command the page sent, in order, as <c>name</c> and its arguments.</summary>
    internal List<(string Command, object?[] Arguments)> Commands { get; } = [];

    /// <inheritdoc />
    public event Action? Changed;

    /// <inheritdoc />
    public GameToolsState Peek(uint appId)
    {
        return _states.TryGetValue(appId, out var state)
            ? state
            : throw new InvalidOperationException("The fake has no state for game " + appId + ".");
    }

    /// <inheritdoc />
    public GameToolsLibrary PeekLibrary()
    {
        return Library;
    }

    /// <inheritdoc />
    public Task<GameToolsLibrary> ReadLibraryAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Library);
    }

    /// <inheritdoc />
    public Task<GameToolsState> ReadAsync(uint appId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Peek(appId));
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> RefreshAsync(uint appId, CancellationToken cancellationToken)
    {
        return Record("refresh", appId);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> CheckUpdatesAsync(uint appId, CancellationToken cancellationToken)
    {
        return Record("checkUpdates", appId);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> ChooseRuntimeAsync(uint appId, string path, CancellationToken cancellationToken)
    {
        return Record("chooseRuntime", appId, path);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> InstallAsync(GameToolsInstallRequest request,
        CancellationToken cancellationToken)
    {
        return Record("install", request);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> UpdateAsync(uint appId, string toolId, bool replaceConfig,
        CancellationToken cancellationToken)
    {
        return Record("update", appId, toolId, replaceConfig);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> SwitchChannelAsync(uint appId, string toolId, string channel,
        bool replaceConfig, CancellationToken cancellationToken)
    {
        return Record("switchChannel", appId, toolId, channel, replaceConfig);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> RepairAsync(uint appId, string toolId, CancellationToken cancellationToken)
    {
        return Record("repair", appId, toolId);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> ConfigureAsync(uint appId, string toolId, string? proxyName,
        IReadOnlyDictionary<string, string> settings, CancellationToken cancellationToken)
    {
        return Record("configure", appId, toolId, proxyName, settings);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> SetAddonAsync(uint appId, string toolId, string addonId, bool enabled,
        CancellationToken cancellationToken)
    {
        return Record("setAddon", appId, toolId, addonId, enabled);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> RemoveAsync(uint appId, string toolId, CancellationToken cancellationToken)
    {
        return Record("remove", appId, toolId);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> CancelAsync(uint appId, CancellationToken cancellationToken)
    {
        return Record("cancel", appId);
    }

    /// <summary>Completes once the page has sent another command; commands are sent off the UI thread.</summary>
    /// <returns>The wait, failing after the shared time limit.</returns>
    internal Task NextCommandAsync()
    {
        return _sent.WaitAsync(AsyncConditions.TimeLimit).ContinueWith(task =>
        {
            if (!task.Result)
            {
                throw new TimeoutException("The page sent no command.");
            }
        }, TaskScheduler.Default);
    }

    /// <summary>Publishes a new game state, as the service does after a change.</summary>
    /// <param name="next">The state.</param>
    internal void Publish(GameToolsState next)
    {
        _states[next.AppId] = next;
        Changed?.Invoke();
    }

    /// <summary>Publishes a new game list.</summary>
    /// <param name="next">The list.</param>
    internal void Publish(GameToolsLibrary next)
    {
        Library = next;
        Changed?.Invoke();
    }

    private Task<SteamUiCommandResult> Record(string command, params object?[] arguments)
    {
        lock (Commands)
        {
            Commands.Add((command, arguments));
        }

        _sent.Release();

        return Task.FromResult(SteamUiCommandResult.Applied);
    }
}

/// <summary>The synthetic games, states and capsule images every Game Tools capture draws.</summary>
internal static class GameToolsFixtures
{
    internal const uint AlanWake = 2_388_300;
    internal const uint Cyberpunk = 1_091_500;
    internal const uint EldenRing = 1_245_620;
    internal const uint Hogwarts = 990_080;
    internal const uint RedDead = 1_174_180;
    internal const uint Starfield = 1_716_740;

    /// <summary>The Steam library the list reads.</summary>
    internal static SteamLibraryReadResult SteamLibrary { get; } = new(
    [
        new SteamLibraryApp(AlanWake, "Alan Wake 2"),
        new SteamLibraryApp(Cyberpunk, "Cyberpunk 2077"),
        new SteamLibraryApp(EldenRing, "ELDEN RING"),
        new SteamLibraryApp(2_215_430, "Ghost of Tsushima"),
        new SteamLibraryApp(1_145_350, "Hades II"),
        new SteamLibraryApp(Hogwarts, "Hogwarts Legacy"),
        new SteamLibraryApp(RedDead, "Red Dead Redemption 2"),
        new SteamLibraryApp(Starfield, "Starfield"),
        new SteamLibraryApp(3_000_000_001, "A non-Steam game with a very long name that should trim cleanly", true)
    ], null);

    /// <summary>The games with tools.</summary>
    internal static GameToolsLibrary Library { get; } = new(false, null,
    [
        Game(Cyberpunk, "Cyberpunk 2077", Summary("0.9.3", "updateAvailable", "Update to 0.9.4",
            "OptiScaler 0.9.3 as dxgi.dll", "0.9.4")),
        Game(Hogwarts, "Hogwarts Legacy", Summary("0.9.4", "modified", "Needs repair",
            "OptiScaler 0.9.4 · 2 files changed outside WSGM", null)),
        Game(Starfield, "Starfield", Summary("10.0.0-pre1", "upToDate", "Up to date",
                "OptiScaler 10.0.0-pre1 · nightly 31 Oct 2026 as dxgi.dll", null) with
            {
                Channel = "nightly", ChannelLabel = "Nightly", DefaultChannel = false,
                VersionLabel = "10.0.0-pre1 · nightly 31 Oct 2026"
            })
    ], 3, 1);

    /// <summary>Alan Wake 2 with nothing installed.</summary>
    internal static GameToolsState NotInstalled { get; } = new(AlanWake, "Alan Wake 2", false,
        @"E:\SteamLibrary\steamapps\common\Alan Wake 2", "ready", null, false, "Unknown engine",
        new GameToolsRuntime("AlanWake2.exe", "AlanWake2.exe", "x64", "64-bit", "single",
            "The only likely program in the folder"),
        [
            new GameToolsCandidate("AlanWake2.exe", "AlanWake2.exe", "x64", "64-bit", "game", "Likely the game",
                ["Largest program in the folder"], true)
        ], false, false,
        [
            new GameToolsUpscaler("DLSS", "nvngx_dlss.dll", "3.7.10.0"),
            new GameToolsUpscaler("FSR 3.1", "amd_fidelityfx_dx12.dll", "3.1.2.0"),
            new GameToolsUpscaler("XeSS", "libxess.dll", "1.3.1.0")
        ], [], null, ["amd"], [Tool()], null, null, null);

    /// <summary>Cyberpunk 2077 with OptiScaler installed, an update known and a configure notice.</summary>
    internal static GameToolsState Installed { get; } = NotInstalled with
    {
        AppId = Cyberpunk,
        Name = "Cyberpunk 2077",
        InstallFolder = @"D:\SteamLibrary\steamapps\common\Cyberpunk 2077",
        Runtime = new GameToolsRuntime("bin/x64/Cyberpunk2077.exe", "Cyberpunk2077.exe", "x64", "64-bit",
            "confirmed", "You chose this program"),
        Upscalers =
        [
            new GameToolsUpscaler("DLSS", "bin/x64/nvngx_dlss.dll", "3.7.10.0"),
            new GameToolsUpscaler("FSR 3.1", "bin/x64/amd_fidelityfx_dx12.dll", "3.1.2.0")
        ],
        Notice = "Menu scale saved to OptiScaler.ini.",
        Tools =
        [
            Tool() with
            {
                Status = "updateAvailable", StatusText = "Version 0.9.4 is available.", InstalledVersion = "0.9.3",
                InstalledLabel = "0.9.3",
                Settings =
                [
                    .. Tool().Settings.Select(setting => setting.Id switch
                    {
                        "menuScale" => setting with { Value = "1.2", IniValue = "1.2" },
                        "hideSplash" => setting with { Value = "on", IniValue = "true" },
                        _ => setting with { IniValue = "auto" }
                    })
                ],
                ProxyIntro = "WSGM renames the installed file. Try another name when the game ignores OptiScaler or "
                             + "crashes.",
                Actions = ["update", "switchChannel", "configure", "repair", "remove"],
                Files = new GameToolsFiles("bin/x64", 14, 3, [], []),
                Addons =
                [
                    OptiPatcher() with
                    {
                        Installed = true, Text = "The build of 30 Oct 2026 is available.", UpdateAvailable = true
                    }
                ]
            }
        ]
    };

    /// <summary>Alan Wake 2 while OptiScaler downloads.</summary>
    internal static GameToolsState Installing { get; } = NotInstalled with
    {
        Busy = new GameToolsBusy(AlanWake, "optiscaler", "install", "download",
            "Downloading OptiScaler 0.9.4 from GitHub", 13_002_342, 32_505_856, true, "stable", "0.9.4")
    };

    /// <summary>Red Dead Redemption 2, whose program the user must choose.</summary>
    internal static GameToolsState NeedsProgram { get; } = NotInstalled with
    {
        AppId = RedDead,
        Name = "Red Dead Redemption 2",
        InstallFolder = @"E:\SteamLibrary\steamapps\common\Red Dead Redemption 2",
        Runtime = null,
        ScanTruncated = true,
        Candidates =
        [
            new GameToolsCandidate("RDR2.exe", "RDR2.exe", "x64", "64-bit", "game", "Likely the game",
                ["Largest program in the folder", "Named like the game's folder"], false),
            new GameToolsCandidate("PlayRDR2.exe", "PlayRDR2.exe", "x64", "64-bit", "launcher",
                "Launcher or settings", ["Name suggests a launcher", "Small program"], false),
            new GameToolsCandidate("Launcher/Launcher.exe", "Launcher.exe", "x64", "64-bit", "launcher",
                "Launcher or settings", ["Name says launcher", "In a subfolder"], false),
            new GameToolsCandidate("_CommonRedist/vc_redist.x64.exe", "vc_redist.x64.exe", "x64", "64-bit",
                "helper", "Installer or helper", ["A runtime installer, not the game"], false)
        ],
        Tools =
        [
            Tool() with
            {
                IncompatibleReason = "Choose the game's program first.",
                StatusText = "Choose the game's program first.",
                Actions = [], ShowOptions = false, CanEditOptions = false
            }
        ]
    };

    /// <summary>Elden Ring, which ships Easy Anti-Cheat and no standalone upscaler.</summary>
    internal static GameToolsState AntiCheat { get; } = NotInstalled with
    {
        AppId = EldenRing,
        Name = "ELDEN RING",
        InstallFolder = @"D:\SteamLibrary\steamapps\common\ELDEN RING",
        Runtime = new GameToolsRuntime("Game/eldenring.exe", "eldenring.exe", "x64", "64-bit", "structural",
            "Identified from the folder layout or Steam's launch target"),
        Upscalers = [],
        AntiCheat = ["Easy Anti-Cheat"],
        AntiCheatWarning =
        "Anti-cheat found: Easy Anti-Cheat. Do not use with online games. Anti-cheat may ban accounts.",
        Tools =
        [
            Tool() with
            {
                Warnings =
                [
                    "No standalone DLSS, FSR or XeSS library was found. The game may still support one built in; "
                    + "OptiScaler only works when the game offers an upscaler."
                ],
                Files = new GameToolsFiles("Game", 0, 0, [], [])
            }
        ]
    };

    /// <summary>Every game state the fake publishes.</summary>
    internal static IReadOnlyList<GameToolsState> States { get; } = [NotInstalled, Installed, NeedsProgram, AntiCheat];

    /// <summary>A deterministic landscape capsule for a game, so captures show artwork without Steam.</summary>
    /// <param name="appId">The game.</param>
    /// <param name="token">Unused.</param>
    /// <returns>PNG bytes.</returns>
    internal static Task<byte[]> CapsuleAsync(uint appId, CancellationToken token)
    {
        var hue = appId % 360;
        using SKBitmap bitmap = new(460, 215);
        using (SKCanvas canvas = new(bitmap))
        using (SKPaint paint = new())
        {
            paint.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(460, 215),
                [SKColor.FromHsl(hue, 45, 32), SKColor.FromHsl((hue + 40) % 360, 55, 18)], SKShaderTileMode.Clamp);
            canvas.DrawRect(0, 0, 460, 215, paint);
            paint.Shader = null;
            paint.Color = SKColor.FromHsl((hue + 180) % 360, 60, 62).WithAlpha(150);
            canvas.DrawCircle(360, 70, 54, paint);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return Task.FromResult(encoded.ToArray());
    }

    /// <summary>OptiScaler, not installed, with both channels' latest releases known.</summary>
    internal static GameToolsTool Tool()
    {
        return new GameToolsTool("optiscaler", "OptiScaler",
            "Replaces the game's DLSS, FSR or XeSS upscaler with another one and can add frame generation.",
            "https://github.com/optiscaler/OptiScaler", "GPL-3.0",
            "WSGM is not affiliated with OptiScaler. Report problems to WSGM.", null, "notInstalled",
            "Not installed.", null, null, "stable",
            [
                new GameToolsChannel("stable", "Stable", "", true, "0.9.4", "0.9.4", null, null, false, false),
                new GameToolsChannel("nightly", "Nightly", "Daily test builds of the next version; may be unstable",
                    false, "10.0.0-pre1", "10.0.0-pre1 · nightly 31 Oct 2026", "2026-10-31", null, false, false)
            ], "0.9.4", "0.9.4", null, false, false, "dxgi.dll",
            "OptiScaler loads by taking the name of a Windows library the game loads. Try the first one first.",
            [
                new GameToolsProxyOption("dxgi.dll", "Most compatible. Try this first.", true, null),
                new GameToolsProxyOption("winmm.dll", "Use for Vulkan games.", false, "ReShade uses this name."),
                new GameToolsProxyOption("version.dll", "Often works better for Xbox and Microsoft Store games.", true,
                    null),
                new GameToolsProxyOption("dbghelp.dll", "An alternative when the others do not load.", true, null),
                new GameToolsProxyOption("d3d12.dll", "Try this when DLSS crashes with the other names.", true, null),
                new GameToolsProxyOption("OptiScaler.asi", "Only with an ASI loader already installed in this game.",
                    true, null)
            ],
            [
                new GameToolsSetting("dlssInputs", "DLSS inputs on AMD and Intel",
                    "Lets the game offer DLSS so OptiScaler can replace it. Turn off if the game misbehaves.", "on",
                    null, [new GameToolsSettingOption("on", "Automatic"), new GameToolsSettingOption("off", "Off")],
                    true, null),
                new GameToolsSetting("menuScale", "Menu scale",
                    "Size of OptiScaler's in-game menu. About 1.2 suits handheld screens.", "auto", null,
                    [
                        new GameToolsSettingOption("auto", "Automatic"), new GameToolsSettingOption("1.0", "1.0"),
                        new GameToolsSettingOption("1.2", "1.2"), new GameToolsSettingOption("1.4", "1.4")
                    ], true, null),
                new GameToolsSetting("hideSplash", "Hide the startup message",
                    "Skips the message OptiScaler shows when the game starts.", "off", null,
                    [new GameToolsSettingOption("off", "Off"), new GameToolsSettingOption("on", "On")], true, null)
            ],
            true, true, [], [], ["install"], new GameToolsFiles("", 0, 0, [], []), [OptiPatcher()]);
    }

    /// <summary>OptiPatcher, supported by the game's program and recommended on AMD graphics.</summary>
    internal static GameToolsAddon OptiPatcher()
    {
        return new GameToolsAddon("optipatcher", "OptiPatcher",
            "An OptiScaler plugin that unlocks this game's DLSS and DLSS frame generation inputs without spoofing an "
            + "NVIDIA graphics card, which saves the spoofing's overhead.",
            "https://github.com/optiscaler/OptiPatcher", "MIT", "supported", false, true, true,
            "OptiPatcher supports AlanWake2.exe.", null, false);
    }

    private static GameToolsGameSummary Game(uint appId, string name, GameToolsToolSummary tool)
    {
        return new GameToolsGameSummary(appId, name, false, [tool]);
    }

    private static GameToolsToolSummary Summary(string version, string status, string label, string detail,
        string? latest)
    {
        return new GameToolsToolSummary("optiscaler", "OptiScaler", version, version, "stable", "Stable", true,
            "dxgi.dll", status, label, detail, latest, status == "modified" ? 2 : 0);
    }
}
