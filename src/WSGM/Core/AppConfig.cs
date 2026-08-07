using System.Collections.Generic;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace WSGM.Core;

/// <summary>Describes one optional program started as part of a shell session.</summary>
public sealed class StartupAppConfig
{
    /// <summary>Executable path or protocol URL to launch.</summary>
    public string Path { get; set; } = "";

    /// <summary>Command-line arguments passed to an executable target.</summary>
    public string Args { get; set; } = "";

    /// <summary>Whether this entry participates in the shell startup sequence.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Whether the executable must inherit WSGM's elevated token.</summary>
    public bool Elevated { get; set; }
    /// <summary>Relaunch this tool automatically when its process dies (e.g. a
    /// crashed Handheld Companion leaves the device without controller input).</summary>
    public bool AutoRelaunch { get; set; }
}

/// <summary>Describes the optional system-wide keyboard shortcut for quick access.</summary>
public sealed class HotkeyConfig
{
    /// <summary>False = no keyboard shortcut at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Whether Control is required.</summary>
    public bool Ctrl { get; set; } = true;

    /// <summary>Whether Alt is required.</summary>
    public bool Alt { get; set; } = true;

    /// <summary>Whether Shift is required.</summary>
    public bool Shift { get; set; }

    /// <summary>Whether either Windows key is required.</summary>
    public bool Win { get; set; }
    /// <summary>Win32 virtual-key code. Default VK_HOME (0x24). 0 = unset.</summary>
    public int VirtualKey { get; set; } = 0x24;
}

/// <summary>Controller shortcut: a set of buttons pressed together, optionally held.
/// Modelled on Handheld Companion's chords — buttons accumulate until every button is
/// released, so they don't have to be pressed on the same frame.</summary>
public sealed class GamepadChordConfig
{
    /// <summary>False = no controller shortcut.</summary>
    public bool Enabled { get; set; }
    /// <summary>Bit mask of XInput buttons (see Input.GamepadButtons).</summary>
    public int Buttons { get; set; }
    /// <summary>True = must be held (~600 ms); false = a normal press.</summary>
    public bool Hold { get; set; }
}

/// <summary>What an inward edge swipe opens.</summary>
public enum EdgeAction
{
    /// <summary>The quick-access panel.</summary>
    QuickAccess,

    /// <summary>The game-mode taskbar (the swipe is ignored in desktop mode,
    /// where explorer's real taskbar owns the bottom edge).</summary>
    Taskbar,
}

/// <summary>Controls the raw-input edge-swipe activation areas for quick access.</summary>
public sealed class GestureConfig
{
    /// <summary>Whether a swipe from the bottom edge is recognized.</summary>
    public bool BottomEdge { get; set; } = true;

    /// <summary>Whether a swipe from the right edge opens the overlay.</summary>
    public bool RightEdge { get; set; } = true;
    /// <summary>Strip thickness in physical pixels.</summary>
    public int StripThickness { get; set; } = 16;

    /// <summary>What the bottom-edge swipe opens (the right edge always opens
    /// quick access).</summary>
    public EdgeAction BottomEdgeAction { get; set; } = EdgeAction.Taskbar;
}

/// <summary>Selects the controller-button glyph family rendered by the UI.</summary>
public enum GlyphStyle
{
    /// <summary>Xbox ABXY labels and artwork.</summary>
    Xbox,

    /// <summary>PlayStation Cross/Circle/Square/Triangle artwork.</summary>
    PlayStation,

    /// <summary>Nintendo ABXY labels and artwork.</summary>
    Nintendo,
}

/// <summary>One display's pre-game scaling, keyed by the GDI source device name
/// (\\.\DISPLAYn) so restore survives topology changes and later boots.</summary>
public sealed class DisplayScaleEntry
{
    /// <summary>GDI source device name, such as <c>\\.\DISPLAY1</c>.</summary>
    public string DeviceName { get; set; } = "";

    /// <summary>Saved scale percentage to restore for this display.</summary>
    public int Percent { get; set; }
}

/// <summary>One power scheme's CONSOLELOCK values as they were before WSGM wrote
/// them. -1 = value absent (Windows default applies).</summary>
public sealed class PowerSchemeConsoleLock
{
    /// <summary>Power-scheme GUID without surrounding braces.</summary>
    public string SchemeGuid { get; set; } = "";

    /// <summary>Saved AC value; <c>-1</c> means the setting was absent.</summary>
    public int AcValue { get; set; } = -1;

    /// <summary>Saved DC value; <c>-1</c> means the setting was absent.</summary>
    public int DcValue { get; set; } = -1;
}

/// <summary>Persisted user settings and exact Windows-state snapshots for WSGM.</summary>
public sealed class AppConfig
{
    /// <summary>Restart Steam automatically when it exits. Steam itself is located
    /// via the registry (see Core.Steam) — there is nothing else to configure.</summary>
    public bool SteamAutoRelaunch { get; set; }

    /// <summary>Programs to start before Steam, in launch order.</summary>
    public List<StartupAppConfig> StartupApps { get; set; } = [];
    /// <summary>Delay before the FIRST startup app. Apps launch a few hundred ms
    /// into the logon session, right after the game-mode display-scale change —
    /// tools started into that window can hang (device-observed with Handheld
    /// Companion, intermittent). This lets the session and the DPI change settle.</summary>
    public int StartupDelayMs { get; set; } = 3000;

    /// <summary>Delay between enabled startup-app launches, in milliseconds.</summary>
    public int StaggerDelayMs { get; set; } = 1500;

    /// <summary>Extra delay before Steam Big Picture is started at logon.</summary>
    public int SteamDelayMs { get; set; } = 0;
    /// <summary>Fullscreen "Please wait" cover at logon that hides startup-app
    /// window flashes until Steam Big Picture is on screen (see Shell\BootSplash).</summary>
    public bool BootSplashEnabled { get; set; } = true;

    /// <summary>Keyboard shortcut configuration for opening the overlay.</summary>
    public HotkeyConfig Hotkey { get; set; } = new();

    /// <summary>Controller shortcut configuration for opening the overlay.</summary>
    public GamepadChordConfig GamepadChord { get; set; } = new();

    /// <summary>Touch-edge gesture configuration for opening the overlay.</summary>
    public GestureConfig Gestures { get; set; } = new();

    /// <summary>Controller glyph family displayed by the UI.</summary>
    public GlyphStyle GlyphStyle { get; set; } = GlyphStyle.Xbox;

    /// <summary>When > 0 and the home app is Steam, WSGM fires
    /// steam://forceinputappid/&lt;this&gt; so Steam Input keeps that app's controller
    /// layout active everywhere — desktop, Big Picture, in game — and the desktop
    /// profile never swallows the controller. This is what lets the overlay take
    /// focus (device-confirmed). Default 480 (Spacewar): every account owns it and
    /// its layout is a stock gamepad passthrough. 0 = off. The /0 reset fires on
    /// every exit and recovery path (see SteamInputPin).</summary>
    public int SteamForceInputAppId { get; set; } = 480;

    /// <summary>Legacy pre-device-identity list: scaling percentages in active-source
    /// enumeration order. Kept only so configs written by older versions still
    /// restore (migrated into SavedDisplayScaleEntries on the next restore).</summary>
    public List<int> SavedDisplayScales { get; set; } = [];

    /// <summary>Per-display scaling captured before game mode forced 100%. Non-empty
    /// means "not yet restored" — survives crashes so recovery paths can put
    /// scaling back, matched per display via the GDI source device name.</summary>
    public List<DisplayScaleEntry> SavedDisplayScaleEntries { get; set; } = [];
    /// <summary>The Winlogon Shell snapshot that existed before WSGM installed itself.
    /// Presence is separate from the string so an empty value remains distinguishable
    /// from an absent value; kind preserves REG_EXPAND_SZ as well as REG_SZ.</summary>
    public string? PreviousShellValue { get; set; }

    /// <summary>Whether the original Winlogon Shell value has been captured.</summary>
    public bool PreviousShellSnapshotCaptured { get; set; }

    /// <summary>Whether the original Winlogon Shell value existed.</summary>
    public bool PreviousShellValueExists { get; set; }

    /// <summary>Registry type of the original Winlogon Shell value.</summary>
    public RegistryValueKind PreviousShellValueKind { get; set; } = RegistryValueKind.String;

    /// <summary>Snapshot of GamingConfiguration\StartupToGamingHome, which is changed
    /// while WSGM is installed to keep Xbox Full Screen Experience from competing
    /// for the session.</summary>
    public int PreviousStartupToGamingHomeValue { get; set; }

    /// <summary>Whether the original GamingConfiguration value has been captured.</summary>
    public bool PreviousStartupToGamingHomeSnapshotCaptured { get; set; }

    /// <summary>Whether the original GamingConfiguration value existed.</summary>
    public bool PreviousStartupToGamingHomeValueExists { get; set; }

    /// <summary>Registry type of the original GamingConfiguration value.</summary>
    public RegistryValueKind PreviousStartupToGamingHomeValueKind { get; set; } = RegistryValueKind.DWord;

    /// <summary>UAC prompt-level values as they were before WSGM lowered them,
    /// so the change can be undone exactly.</summary>
    public bool PreviousUacSnapshotCaptured { get; set; }

    /// <summary>Original administrator-consent prompt level.</summary>
    public int PreviousUacConsentPrompt { get; set; } = 5;

    /// <summary>Original secure-desktop prompt setting.</summary>
    public int PreviousUacSecureDesktop { get; set; } = 1;

    /// <summary>Whether Windows required a sign-in on wake before WSGM changed it.
    /// Kept for configs captured by older versions; new snapshots also store the
    /// exact per-scheme values below.</summary>
    public bool PreviousLockOnWakeSnapshotCaptured { get; set; }

    /// <summary>Legacy original sign-in-on-wake state for older configurations.</summary>
    public bool PreviousLockOnWakeRequired { get; set; } = true;

    /// <summary>Previous HKLM Personalization\NoLockScreen value (-1 = absent).</summary>
    public int PreviousNoLockScreen { get; set; } = -1;

    /// <summary>Per-power-scheme CONSOLELOCK values (AC and DC) as they were before
    /// WSGM flattened them to 0, so restore is exact even for mixed setups.</summary>
    public List<PowerSchemeConsoleLock> PreviousConsoleLockSchemeValues { get; set; } = [];
    /// <summary>True when the CONSOLELOCK policy key already existed before WSGM;
    /// false means WSGM created it and restore deletes the whole key.</summary>
    public bool PreviousConsoleLockPolicyKeyExisted { get; set; }

    /// <summary>Pre-existing CONSOLELOCK policy values (-1 = value absent).</summary>
    public int PreviousConsoleLockPolicyAc { get; set; } = -1;

    /// <summary>Pre-existing DC CONSOLELOCK policy value; <c>-1</c> means absent.</summary>
    public int PreviousConsoleLockPolicyDc { get; set; } = -1;

    /// <summary>Legacy cleanup state for posture/keyboard values written by older
    /// WSGM builds. Current builds retain these fields only to restore and clear
    /// that old snapshot; they never capture a new one.</summary>
    public bool SlateModeSnapshotCaptured { get; set; }

    /// <summary>Original ConvertibleSlateMode value; <c>-1</c> means absent.</summary>
    public int PreviousSlateMode { get; set; } = -1;

    /// <summary>Original TouchKeyboardTapInvoke value; <c>-1</c> means absent.</summary>
    public int PreviousTouchKeyboardTapInvoke { get; set; } = -1;

    /// <summary>Legacy marker recording whether WSGM changed ConvertibleSlateMode.</summary>
    public bool? ConvertibleSlateModeModifiedByWsgm { get; set; }
}

/// <summary>Source-generated JSON metadata for the persisted <see cref="AppConfig"/> contract.</summary>
[JsonSerializable(typeof(AppConfig))]
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
public partial class ConfigJsonContext : JsonSerializerContext
{
}
