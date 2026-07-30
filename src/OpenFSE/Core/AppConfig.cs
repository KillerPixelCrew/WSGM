using System.Collections.Generic;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace OpenFSE.Core;

public sealed class HomeAppConfig
{
    public string Path { get; set; } = "";
    public string Args { get; set; } = "";
    public bool Elevated { get; set; }
    public bool AutoRelaunch { get; set; }
    /// <summary>Semicolon-separated process names that count as "home app is running"
    /// (Steam's window lives in steamwebhelper.exe, not Steam.exe).</summary>
    public string ProcessNames { get; set; } = "steam;steamwebhelper";
    /// <summary>Window class of the home app's main window (Steam BPM: SDL_app).</summary>
    public string WindowClass { get; set; } = "SDL_app";
    /// <summary>Protocol used to (re)activate the app; empty = focus the window instead.</summary>
    public string ActivationProtocol { get; set; } = "steam://open/bigpicture";
}

public sealed class StartupAppConfig
{
    public string Path { get; set; } = "";
    public string Args { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool Elevated { get; set; }
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

public sealed class AppConfig
{
    public HomeAppConfig HomeApp { get; set; } = new();
    public List<StartupAppConfig> StartupApps { get; set; } = [];
    public int StaggerDelayMs { get; set; } = 1500;
    public int HomeAppDelayMs { get; set; } = 0;
    public HotkeyConfig Hotkey { get; set; } = new();
    public GamepadChordConfig GamepadChord { get; set; } = new();
    public GestureConfig Gestures { get; set; } = new();
    public GlyphStyle GlyphStyle { get; set; } = GlyphStyle.Xbox;
    /// <summary>The Winlogon Shell snapshot that existed before OpenFSE installed itself.
    /// Presence is separate from the string so an empty value remains distinguishable
    /// from an absent value; kind preserves REG_EXPAND_SZ as well as REG_SZ.</summary>
    public string? PreviousShellValue { get; set; }
    public bool PreviousShellSnapshotCaptured { get; set; }
    public bool PreviousShellValueExists { get; set; }
    public RegistryValueKind PreviousShellValueKind { get; set; } = RegistryValueKind.String;

    /// <summary>Snapshot of GamingConfiguration\StartupToGamingHome, which is changed
    /// while OpenFSE is installed to keep Xbox Full Screen Experience from competing
    /// for the session.</summary>
    public int PreviousStartupToGamingHomeValue { get; set; }
    public bool PreviousStartupToGamingHomeSnapshotCaptured { get; set; }
    public bool PreviousStartupToGamingHomeValueExists { get; set; }
    public RegistryValueKind PreviousStartupToGamingHomeValueKind { get; set; } = RegistryValueKind.DWord;

    /// <summary>UAC prompt-level values as they were before OpenFSE lowered them,
    /// so the change can be undone exactly.</summary>
    public bool PreviousUacSnapshotCaptured { get; set; }
    public int PreviousUacConsentPrompt { get; set; } = 5;
    public int PreviousUacSecureDesktop { get; set; } = 1;

    /// <summary>Whether Windows required a sign-in on wake before OpenFSE changed it.</summary>
    public bool PreviousLockOnWakeSnapshotCaptured { get; set; }
    public bool PreviousLockOnWakeRequired { get; set; } = true;
    /// <summary>Previous HKLM Personalization\NoLockScreen value (-1 = absent).</summary>
    public int PreviousNoLockScreen { get; set; } = -1;
}

[JsonSerializable(typeof(AppConfig))]
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
public partial class ConfigJsonContext : JsonSerializerContext
{
}
