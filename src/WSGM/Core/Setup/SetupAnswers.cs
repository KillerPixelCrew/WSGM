using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace WSGM.Core;

/// <summary>
///     Everything setup asks about WSGM itself, as one JSON document. WSGM exports it from the current
///     configuration (<c>--export-setup-answers</c>) and applies what the user chose
///     (<c>--setup --answers</c>), so setup never reads or writes <c>config.json</c> itself.
/// </summary>
public sealed record SetupAnswers
{
    /// <summary>The only schema this build reads and writes.</summary>
    public const int CurrentSchema = 1;

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
                BootSplash = config.BootSplashEnabled,
                Rtss = config.Performance.Enabled
            },
            SteamAutostartEntries = steamAutostartEntries,
            Presets = DefaultPresets
        };
    }

    /// <summary>Writes the answers into a configuration.</summary>
    /// <param name="config">The configuration to change.</param>
    /// <param name="freshInstall">Whether setup found no configuration before loading it.</param>
    public void ApplyTo(AppConfig config, bool freshInstall)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.StartAtSignIn = StartAtSignIn;
        config.StartMode = StartMode;
        config.DeviceIntegration.Enabled = DeviceIntegration;
        if (freshInstall)
        {
            config.DeviceIntegration.ControllerManagementEnabled = DeviceIntegration;
        }

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
        var edgeOn = config.Gestures.TopEdge || config.Gestures.LeftEdgeSteamMenu
                                             || config.Gestures.RightEdgeSteamQuickAccess;
        if (Features.EdgeGestures != edgeOn)
        {
            config.Gestures.TopEdge = Features.EdgeGestures;
            config.Gestures.LeftEdgeSteamMenu = Features.EdgeGestures;
            config.Gestures.RightEdgeSteamQuickAccess = Features.EdgeGestures;
        }

        config.Hotkey.Enabled = Features.Hotkey;
        config.GamepadChord.Enabled = Features.GamepadChord;
        config.BootSplashEnabled = Features.BootSplash;
        config.Performance.Enabled = Features.Rtss;
    }

    /// <summary>Parses an answers document.</summary>
    /// <param name="utf8Json">The document.</param>
    /// <returns>The answers.</returns>
    /// <exception cref="InvalidDataException">The document is empty, malformed or of another schema.</exception>
    public static SetupAnswers Parse(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.Length is 0)
        {
            throw new InvalidDataException("The setup answers are empty.");
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
            ("gamepadChord", Features.GamepadChord), ("bootSplash", Features.BootSplash), ("rtss", Features.Rtss)
        ];
        var off = features.Where(feature => !feature.On).Select(feature => feature.Name);
        return $"startAtSignIn={StartAtSignIn}, startMode={StartMode}, deviceIntegration={DeviceIntegration}, "
               + $"steamAutostartTakeover={SteamAutostartTakeover}, otherManagersTakeover={OtherManagersTakeover}, "
               + $"off=[{string.Join(",", off)}]";
    }
}
