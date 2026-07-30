using System.Collections.Generic;
using System.Text.Json.Serialization;

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
    public bool Enabled { get; set; } = true;
    public bool Ctrl { get; set; } = true;
    public bool Alt { get; set; } = true;
    public bool Shift { get; set; }
    public bool Win { get; set; }
    /// <summary>Win32 virtual-key code. Default VK_HOME (0x24).</summary>
    public int VirtualKey { get; set; } = 0x24;
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
    public GestureConfig Gestures { get; set; } = new();
    public GlyphStyle GlyphStyle { get; set; } = GlyphStyle.Xbox;
    /// <summary>The Winlogon Shell value that existed before OpenFSE installed itself
    /// (null/empty = there was none; restore means delete the value).</summary>
    public string? PreviousShellValue { get; set; }
}

[JsonSerializable(typeof(AppConfig))]
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
public partial class ConfigJsonContext : JsonSerializerContext
{
}
