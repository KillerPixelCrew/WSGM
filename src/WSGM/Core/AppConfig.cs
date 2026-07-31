using System.Collections.Generic;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace WSGM.Core;

public sealed class StartupAppConfig
{
    public string Path { get; set; } = "";
    public string Args { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool Elevated { get; set; }
    /// <summary>Relaunch this tool automatically when its process dies (e.g. a
    /// crashed Handheld Companion leaves the device without controller input).</summary>
    public bool AutoRelaunch { get; set; }
}

public sealed class HotkeyConfig
{
    /// <summary>False = no keyboard shortcut at all.</summary>
    public bool Enabled { get; set; } = true;
    public bool Ctrl { get; set; } = true;
    public bool Alt { get; set; } = true;
    public bool Shift { get; set; }
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

public sealed class GestureConfig
{
    public bool BottomEdge { get; set; } = true;
    public bool RightEdge { get; set; } = true;
    /// <summary>Strip thickness in physical pixels.</summary>
    public int StripThickness { get; set; } = 16;
}

public enum GlyphStyle
{
    Xbox,
    PlayStation,
    Nintendo,
}

/// <summary>One display's pre-game scaling, keyed by the GDI source device name
/// (\\.\DISPLAYn) so restore survives topology changes and later boots.</summary>
public sealed class DisplayScaleEntry
{
    public string DeviceName { get; set; } = "";
    public int Percent { get; set; }
}

/// <summary>One power scheme's CONSOLELOCK values as they were before WSGM wrote
/// them. -1 = value absent (Windows default applies).</summary>
public sealed class PowerSchemeConsoleLock
{
    public string SchemeGuid { get; set; } = "";
    public int AcValue { get; set; } = -1;
    public int DcValue { get; set; } = -1;
}

public sealed class AppConfig
{
    /// <summary>Restart Steam automatically when it exits. Steam itself is located
    /// via the registry (see Core.Steam) — there is nothing else to configure.</summary>
    public bool SteamAutoRelaunch { get; set; }
    public List<StartupAppConfig> StartupApps { get; set; } = [];
    public int StaggerDelayMs { get; set; } = 1500;
    /// <summary>Extra delay before Steam Big Picture is started at logon.</summary>
    public int SteamDelayMs { get; set; } = 0;
    public HotkeyConfig Hotkey { get; set; } = new();
    public GamepadChordConfig GamepadChord { get; set; } = new();
    public GestureConfig Gestures { get; set; } = new();
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
    public bool PreviousShellSnapshotCaptured { get; set; }
    public bool PreviousShellValueExists { get; set; }
    public RegistryValueKind PreviousShellValueKind { get; set; } = RegistryValueKind.String;

    /// <summary>Snapshot of GamingConfiguration\StartupToGamingHome, which is changed
    /// while WSGM is installed to keep Xbox Full Screen Experience from competing
    /// for the session.</summary>
    public int PreviousStartupToGamingHomeValue { get; set; }
    public bool PreviousStartupToGamingHomeSnapshotCaptured { get; set; }
    public bool PreviousStartupToGamingHomeValueExists { get; set; }
    public RegistryValueKind PreviousStartupToGamingHomeValueKind { get; set; } = RegistryValueKind.DWord;

    /// <summary>UAC prompt-level values as they were before WSGM lowered them,
    /// so the change can be undone exactly.</summary>
    public bool PreviousUacSnapshotCaptured { get; set; }
    public int PreviousUacConsentPrompt { get; set; } = 5;
    public int PreviousUacSecureDesktop { get; set; } = 1;

    /// <summary>Whether Windows required a sign-in on wake before WSGM changed it.
    /// Kept for configs captured by older versions; new snapshots also store the
    /// exact per-scheme values below.</summary>
    public bool PreviousLockOnWakeSnapshotCaptured { get; set; }
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
    public int PreviousConsoleLockPolicyDc { get; set; } = -1;

    /// <summary>ConvertibleSlateMode / TouchKeyboardTapInvoke as they were before
    /// WSGM's first write (-1 = value absent), so a clean exit restores the boot
    /// state exactly instead of a hardcoded guess.</summary>
    public bool SlateModeSnapshotCaptured { get; set; }
    public int PreviousSlateMode { get; set; } = -1;
    public int PreviousTouchKeyboardTapInvoke { get; set; } = -1;
}

[JsonSerializable(typeof(AppConfig))]
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
public partial class ConfigJsonContext : JsonSerializerContext
{
}
