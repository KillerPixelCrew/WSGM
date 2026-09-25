using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WSGM.Core;

/// <summary>The feature switches setup's profile page shows, one per line of its Customize panel.</summary>
public sealed record SetupFeatures
{
    /// <summary>WSGM hands controllers to Steam in games and takes them back for its own menus.</summary>
    public bool SteamInputManagement { get; init; }

    /// <summary>Steam Input lease around WSGM's own surfaces.</summary>
    public bool SteamInputLease { get; init; }

    /// <summary>Steam UI integration master switch.</summary>
    public bool SteamUi { get; init; }

    /// <summary>Library tabs per SD card.</summary>
    public bool LibraryTabs { get; init; }

    /// <summary>Card manager.</summary>
    public bool CardManager { get; init; }

    /// <summary>Connected-library carousel on Home.</summary>
    public bool ConnectedLibraryCarousel { get; init; }

    /// <summary>Format SD cards from Steam.</summary>
    public bool SdFormat { get; init; }

    /// <summary>Wi-Fi indicator in Big Picture's header.</summary>
    public bool WifiIndicator { get; init; }

    /// <summary>WSGM rows in Steam's Quick Access menu.</summary>
    public bool NativeQuickAccess { get; init; }

    /// <summary>Stay awake while Steam downloads.</summary>
    public bool DownloadKeepAwake { get; init; }

    /// <summary>Download queue sorting.</summary>
    public bool DownloadQueueSort { get; init; }

    /// <summary>Edge swipes: top opens WSGM, left and right open Steam's menus.</summary>
    public bool EdgeGestures { get; init; }

    /// <summary>The keyboard shortcut.</summary>
    public bool Hotkey { get; init; }

    /// <summary>The gamepad chord.</summary>
    public bool GamepadChord { get; init; }

    /// <summary>The boot splash over the desktop while Game Mode starts.</summary>
    public bool BootSplash { get; init; }

    /// <summary>Everything on.</summary>
    public static SetupFeatures Full { get; } = new()
    {
        SteamInputManagement = true, SteamInputLease = true, SteamUi = true, LibraryTabs = true,
        CardManager = true, ConnectedLibraryCarousel = true, SdFormat = true, WifiIndicator = true,
        NativeQuickAccess = true, DownloadKeepAwake = true, DownloadQueueSort = true, EdgeGestures = true,
        Hotkey = true, GamepadChord = true, BootSplash = true
    };

    /// <summary>The WSGM session only: nothing in or around Steam, but WSGM stays reachable.</summary>
    public static SetupFeatures Minimal { get; } = new()
    {
        EdgeGestures = true, Hotkey = true, GamepadChord = true, BootSplash = true
    };
}

/// <summary>A named starting point on setup's profile page.</summary>
/// <param name="Features">Its feature switches.</param>
/// <param name="SteamAutostartTakeover">Whether it takes over how Steam starts.</param>
/// <param name="OtherManagersTakeover">Whether it turns off other handheld managers' autostart and services.</param>
public sealed record SetupPreset(SetupFeatures Features, bool SteamAutostartTakeover, bool OtherManagersTakeover);

/// <summary>
///     Everything setup asks about WSGM itself, as one JSON document. WSGM exports it from the current
///     configuration (<c>--export-setup-answers</c>) and applies what the user chose
///     (<c>--setup --answers</c>), so setup never reads or writes <c>config.json</c> itself.
/// </summary>
public sealed record SetupAnswers
{
    /// <summary>The only schema this build reads and writes.</summary>
    public const int CurrentSchema = 1;

    /// <summary>Largest accepted document.</summary>
    public const int MaxBytes = 256 * 1024;

    /// <summary>Schema version.</summary>
    public int SchemaVersion { get; init; } = CurrentSchema;

    /// <summary>Export only: true when this machine had no configuration yet.</summary>
    public bool FreshInstall { get; init; }

    /// <summary>Start WSGM when the user signs in.</summary>
    public bool StartAtSignIn { get; init; }

    /// <summary>Start in Game Mode (Steam first) or Desktop Mode.</summary>
    public SessionStartMode StartMode { get; init; }

    /// <summary>Device integration; setup sets it exactly when it installs a device plugin.</summary>
    public bool DeviceIntegration { get; init; }

    /// <summary>Let WSGM start Steam instead of Windows. Applied only on the user's explicit consent.</summary>
    public bool SteamAutostartTakeover { get; init; }

    /// <summary>
    ///     Turn off how Handheld Companion and the device maker's apps start (their logon tasks and services),
    ///     so WSGM is the one manager of the device. Applied only on the user's choice; uninstall restores it.
    /// </summary>
    public bool OtherManagersTakeover { get; init; }

    /// <summary>The feature switches.</summary>
    public required SetupFeatures Features { get; init; }

    /// <summary>Export only: the enabled Steam startup entries a takeover would turn off.</summary>
    public IReadOnlyList<string> SteamAutostartEntries { get; init; } = [];

    /// <summary>Export only: the other handheld managers found here, one line each, for setup to name.</summary>
    public IReadOnlyList<string> OtherManagers { get; init; } = [];

    /// <summary>Export only: the presets, keyed <c>full</c> and <c>minimal</c>.</summary>
    public IReadOnlyDictionary<string, SetupPreset> Presets { get; init; } =
        new Dictionary<string, SetupPreset>();

    /// <summary>The presets setup offers.</summary>
    public static IReadOnlyDictionary<string, SetupPreset> DefaultPresets { get; } =
        new Dictionary<string, SetupPreset>
        {
            ["full"] = new(SetupFeatures.Full, true, true),
            ["minimal"] = new(SetupFeatures.Minimal, false, false)
        };

    /// <summary>Reads the answers out of a configuration.</summary>
    /// <param name="config">The current configuration.</param>
    /// <param name="freshInstall">Whether the machine had no configuration.</param>
    /// <param name="steamAutostartEntries">Descriptions of the enabled Steam startup entries.</param>
    /// <param name="otherManagers">Descriptions of the other handheld managers found here.</param>
    /// <returns>The answers, with the presets.</returns>
    public static SetupAnswers Export(AppConfig config, bool freshInstall, IReadOnlyList<string> steamAutostartEntries,
        IReadOnlyList<string>? otherManagers = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(steamAutostartEntries);
        return new SetupAnswers
        {
            FreshInstall = freshInstall,
            StartAtSignIn = config.StartAtSignIn,
            StartMode = config.StartMode,
            DeviceIntegration = config.DeviceIntegration.Enabled,
            SteamAutostartTakeover = config.SteamAutostartTakeoverAccepted,
            OtherManagersTakeover = config.OtherManagersTakeoverAccepted,
            OtherManagers = otherManagers ?? [],
            Features = new SetupFeatures
            {
                SteamInputManagement = config.SteamInputManagementEnabled,
                SteamInputLease = config.SteamInputLeaseEnabled,
                SteamUi = config.Cef.Enabled,
                LibraryTabs = config.Cef.LibraryTabs,
                CardManager = config.Cef.CardManager,
                ConnectedLibraryCarousel = config.Cef.ConnectedLibraryCarousel,
                SdFormat = config.Cef.SdFormat,
                WifiIndicator = config.Cef.WifiIndicator,
                NativeQuickAccess = config.Cef.NativeQuickAccess,
                DownloadKeepAwake = config.Cef.DownloadKeepAwake,
                DownloadQueueSort = config.Cef.DownloadQueueSort,
                EdgeGestures = config.Gestures.TopEdge || config.Gestures.LeftEdgeSteamMenu
                                                       || config.Gestures.RightEdgeSteamQuickAccess,
                Hotkey = config.Hotkey.Enabled,
                GamepadChord = config.GamepadChord.Enabled,
                BootSplash = config.BootSplashEnabled
            },
            SteamAutostartEntries = steamAutostartEntries,
            Presets = DefaultPresets
        };
    }

    /// <summary>Writes the answers into a configuration.</summary>
    /// <param name="config">The configuration to change.</param>
    public void ApplyTo(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.StartAtSignIn = StartAtSignIn;
        config.StartMode = StartMode;
        config.DeviceIntegration.Enabled = DeviceIntegration;
        config.SteamAutostartTakeoverAccepted = SteamAutostartTakeover;
        config.OtherManagersTakeoverAccepted = OtherManagersTakeover;
        config.SteamInputManagementEnabled = Features.SteamInputManagement;
        config.SteamInputLeaseEnabled = Features.SteamInputLease;
        config.Cef.Enabled = Features.SteamUi;
        config.Cef.LibraryTabs = Features.LibraryTabs;
        config.Cef.CardManager = Features.CardManager;
        config.Cef.ConnectedLibraryCarousel = Features.ConnectedLibraryCarousel;
        config.Cef.SdFormat = Features.SdFormat;
        config.Cef.WifiIndicator = Features.WifiIndicator;
        config.Cef.NativeQuickAccess = Features.NativeQuickAccess;
        config.Cef.DownloadKeepAwake = Features.DownloadKeepAwake;
        config.Cef.DownloadQueueSort = Features.DownloadQueueSort;
        config.Gestures.TopEdge = Features.EdgeGestures;
        config.Gestures.LeftEdgeSteamMenu = Features.EdgeGestures;
        config.Gestures.RightEdgeSteamQuickAccess = Features.EdgeGestures;
        config.Hotkey.Enabled = Features.Hotkey;
        config.GamepadChord.Enabled = Features.GamepadChord;
        config.BootSplashEnabled = Features.BootSplash;
    }

    /// <summary>Parses an answers document.</summary>
    /// <param name="utf8Json">The document.</param>
    /// <returns>The answers.</returns>
    /// <exception cref="InvalidDataException">The document is malformed, too large or of another schema.</exception>
    public static SetupAnswers Parse(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.Length is 0 or > MaxBytes)
        {
            throw new InvalidDataException("The setup answers are empty or too large.");
        }

        SetupAnswers? answers;
        try
        {
            answers = JsonSerializer.Deserialize(utf8Json, SetupAnswersJsonContext.Default.SetupAnswers);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The setup answers are malformed: " + ex.Message, ex);
        }

        return answers switch
        {
            null => throw new InvalidDataException("The setup answers are empty."),
            { SchemaVersion: not CurrentSchema } => throw new InvalidDataException(
                $"The setup answers use schema {answers.SchemaVersion}; this build reads {CurrentSchema}."),
            _ when !Enum.IsDefined(answers.StartMode) => throw new InvalidDataException(
                "The setup answers name an unknown start mode."),
            _ => answers
        };
    }

    /// <summary>Serializes the answers.</summary>
    /// <returns>UTF-8 JSON.</returns>
    public byte[] ToUtf8Json()
    {
        return JsonSerializer.SerializeToUtf8Bytes(this, SetupAnswersJsonContext.Default.SetupAnswers);
    }

    /// <summary>Describes the answers for the log, without any path or user data.</summary>
    /// <returns>One line.</returns>
    public string Describe()
    {
        (string Name, bool On)[] features =
        [
            ("steamInputManagement", Features.SteamInputManagement), ("steamInputLease", Features.SteamInputLease),
            ("steamUi", Features.SteamUi), ("libraryTabs", Features.LibraryTabs), ("cardManager", Features.CardManager),
            ("carousel", Features.ConnectedLibraryCarousel), ("sdFormat", Features.SdFormat),
            ("wifiIndicator", Features.WifiIndicator), ("nativeQuickAccess", Features.NativeQuickAccess),
            ("downloadKeepAwake", Features.DownloadKeepAwake), ("downloadQueueSort", Features.DownloadQueueSort),
            ("edgeGestures", Features.EdgeGestures), ("hotkey", Features.Hotkey),
            ("gamepadChord", Features.GamepadChord), ("bootSplash", Features.BootSplash)
        ];
        var off = features.Where(feature => !feature.On).Select(feature => feature.Name);
        return $"startAtSignIn={StartAtSignIn}, startMode={StartMode}, deviceIntegration={DeviceIntegration}, "
               + $"steamAutostartTakeover={SteamAutostartTakeover}, otherManagersTakeover={OtherManagersTakeover}, "
               + $"off=[{string.Join(",", off)}]";
    }
}

/// <summary>Source-generated JSON metadata for <see cref="SetupAnswers" />.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    WriteIndented = true)]
[JsonSerializable(typeof(SetupAnswers))]
internal sealed partial class SetupAnswersJsonContext : JsonSerializerContext;
