using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using OpenFSE.Core;

namespace OpenFSE.Settings;

public sealed class StartupAppRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private string _path = "";
    private string _args = "";
    private bool _enabled = true;
    private bool _elevated;

    public string Path { get => _path; set { _path = value; Raise(nameof(Path)); } }
    public string Args { get => _args; set { _args = value; Raise(nameof(Args)); } }
    public bool Enabled { get => _enabled; set { _enabled = value; Raise(nameof(Enabled)); } }
    public bool Elevated { get => _elevated; set { _elevated = value; Raise(nameof(Elevated)); } }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class SettingsViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly AppConfig _config;

    public SettingsViewModel()
    {
        _config = ConfigStore.Load();

        HomeAppPath = _config.HomeApp.Path;
        HomeAppArgs = _config.HomeApp.Args;
        HomeAppElevated = _config.HomeApp.Elevated;
        HomeAppAutoRelaunch = _config.HomeApp.AutoRelaunch;
        StaggerDelayMs = _config.StaggerDelayMs;
        HotkeyCtrl = _config.Hotkey.Ctrl;
        HotkeyAlt = _config.Hotkey.Alt;
        HotkeyShift = _config.Hotkey.Shift;
        HotkeyWin = _config.Hotkey.Win;
        HotkeyKeyIndex = Math.Max(0, KeyOptions.FindIndex(k => k.Vk == _config.Hotkey.VirtualKey));
        GestureBottom = _config.Gestures.BottomEdge;
        GestureRight = _config.Gestures.RightEdge;
        GlyphStyleIndex = (int)_config.GlyphStyle;

        foreach (var app in _config.StartupApps)
        {
            StartupApps.Add(new StartupAppRow
            {
                Path = app.Path, Args = app.Args, Enabled = app.Enabled, Elevated = app.Elevated,
            });
        }
    }

    // --- Shell status ---
    public bool ShellInstalled => ShellRegistration.IsInstalledForThisExe();
    public string ShellStatusText => ShellInstalled
        ? "OpenFSE IS your Windows shell for this account. Sign out and back in for changes to take effect."
        : "OpenFSE is NOT your Windows shell.";

    // --- UAC prompt level ---
    public bool UacPromptsDisabled => UacSettings.Read().PromptsDisabled;

    public string UacStatusText => UacPromptsDisabled
        ? "UAC prompts are OFF — elevated apps start silently. Windows still runs with UAC enabled, but anything that asks for administrator rights gets them without asking you."
        : "UAC prompts are ON (Windows default). Each elevated launch shows a consent dialog, which interrupts boot-to-game on a handheld.";

    /// <summary>Toggles the machine UAC prompt level. Needs one elevation prompt.
    /// Returns false when elevation was declined or the write failed.</summary>
    public bool SetUacPrompts(bool disable)
    {
        var ok = UacSettings.RequestChange(disable);
        Raise(nameof(UacPromptsDisabled));
        Raise(nameof(UacStatusText));
        return ok;
    }

    // --- Lock on wake ---
    public bool LockOnWakeDisabled => LockScreenSettings.SignInOnWakeDisabled();

    public string LockOnWakeStatusText => LockOnWakeDisabled
        ? "Waking the device goes straight back to your game — no sign-in screen."
        : "Windows currently asks you to sign in again after the screen sleeps (Windows default).";

    public bool SetLockOnWake(bool disable)
    {
        var ok = LockScreenSettings.RequestChange(disable);
        Raise(nameof(LockOnWakeDisabled));
        Raise(nameof(LockOnWakeStatusText));
        return ok;
    }

    public bool AppInstalled => Installer.IsAppInstalled;
    public string AppStatusText => Installer.IsRunningFromInstallDir
        ? $"Installed at {Installer.InstallDir}."
        : Installer.IsAppInstalled
            ? $"Running portable — an installed copy exists at {Installer.InstallDir}. \"Install app\" updates it."
            : "Running portable — not installed yet. Installing copies OpenFSE to a stable per-user location and adds it to Start Menu and Settings → Apps.";

    /// <summary>Installs/updates the app files without touching the shell registration.</summary>
    public void InstallApp()
    {
        Installer.InstallApp();
        RaiseShellStatus();
    }

    public void Install()
    {
        ApplyTo(_config);
        // Always anchor the shell registration to the stable installed copy,
        // never to a Downloads/dev path.
        Installer.InstallApp();
        ShellRegistration.Install(_config);
        RaiseShellStatus();
    }

    public void Uninstall()
    {
        ShellRegistration.Uninstall();
        RaiseShellStatus();
    }

    private void RaiseShellStatus()
    {
        Raise(nameof(ShellInstalled));
        Raise(nameof(ShellStatusText));
        Raise(nameof(AppInstalled));
        Raise(nameof(AppStatusText));
    }

    // --- Home app ---
    private string _homeAppPath = "";
    private string _homeAppArgs = "";
    private bool _homeAppElevated;
    private bool _homeAppAutoRelaunch;
    public string HomeAppPath { get => _homeAppPath; set { _homeAppPath = value; Raise(nameof(HomeAppPath)); } }
    public string HomeAppArgs { get => _homeAppArgs; set { _homeAppArgs = value; Raise(nameof(HomeAppArgs)); } }
    public bool HomeAppElevated { get => _homeAppElevated; set { _homeAppElevated = value; Raise(nameof(HomeAppElevated)); } }
    public bool HomeAppAutoRelaunch { get => _homeAppAutoRelaunch; set { _homeAppAutoRelaunch = value; Raise(nameof(HomeAppAutoRelaunch)); } }

    // --- Startup apps ---
    public ObservableCollection<StartupAppRow> StartupApps { get; } = [];

    private int _staggerDelayMs;
    public int StaggerDelayMs { get => _staggerDelayMs; set { _staggerDelayMs = value; Raise(nameof(StaggerDelayMs)); } }

    public void AddStartupApp() => StartupApps.Add(new StartupAppRow());
    public void RemoveStartupApp(StartupAppRow row) => StartupApps.Remove(row);

    public void MoveStartupApp(StartupAppRow row, int delta)
    {
        var index = StartupApps.IndexOf(row);
        var target = index + delta;
        if (index >= 0 && target >= 0 && target < StartupApps.Count)
        {
            StartupApps.Move(index, target);
        }
    }

    // --- Hotkey ---
    public sealed record KeyOption(string Name, int Vk)
    {
        public override string ToString() => Name;
    }

    public static readonly List<KeyOption> KeyOptions =
    [
        new("Home", 0x24), new("End", 0x23), new("Insert", 0x2D), new("Delete", 0x2E),
        new("Page Up", 0x21), new("Page Down", 0x22), new("Pause", 0x13),
        new("F1", 0x70), new("F2", 0x71), new("F3", 0x72), new("F4", 0x73),
        new("F5", 0x74), new("F6", 0x75), new("F7", 0x76), new("F8", 0x77),
        new("F9", 0x78), new("F10", 0x79), new("F11", 0x7A), new("F12", 0x7B),
        new("F13", 0x7C), new("F14", 0x7D), new("F15", 0x7E), new("F16", 0x7F),
        new("O", 0x4F), new("G", 0x47),
    ];

    public List<KeyOption> HotkeyKeys => KeyOptions;

    private bool _hotkeyCtrl, _hotkeyAlt, _hotkeyShift, _hotkeyWin;
    private int _hotkeyKeyIndex;
    public bool HotkeyCtrl { get => _hotkeyCtrl; set { _hotkeyCtrl = value; Raise(nameof(HotkeyCtrl)); } }
    public bool HotkeyAlt { get => _hotkeyAlt; set { _hotkeyAlt = value; Raise(nameof(HotkeyAlt)); } }
    public bool HotkeyShift { get => _hotkeyShift; set { _hotkeyShift = value; Raise(nameof(HotkeyShift)); } }
    public bool HotkeyWin { get => _hotkeyWin; set { _hotkeyWin = value; Raise(nameof(HotkeyWin)); } }
    public int HotkeyKeyIndex { get => _hotkeyKeyIndex; set { _hotkeyKeyIndex = value; Raise(nameof(HotkeyKeyIndex)); } }

    // --- Gestures / glyphs ---
    private bool _gestureBottom, _gestureRight;
    private int _glyphStyleIndex;
    public bool GestureBottom { get => _gestureBottom; set { _gestureBottom = value; Raise(nameof(GestureBottom)); } }
    public bool GestureRight { get => _gestureRight; set { _gestureRight = value; Raise(nameof(GestureRight)); } }
    public int GlyphStyleIndex { get => _glyphStyleIndex; set { _glyphStyleIndex = value; Raise(nameof(GlyphStyleIndex)); } }

    public List<string> GlyphStyles { get; } = ["Xbox", "PlayStation", "Nintendo"];

    // --- Save ---
    private void ApplyTo(AppConfig config)
    {
        config.HomeApp.Path = HomeAppPath.Trim();
        config.HomeApp.Args = HomeAppArgs.Trim();
        config.HomeApp.Elevated = HomeAppElevated;
        config.HomeApp.AutoRelaunch = HomeAppAutoRelaunch;
        config.StaggerDelayMs = StaggerDelayMs;
        config.Hotkey.Ctrl = HotkeyCtrl;
        config.Hotkey.Alt = HotkeyAlt;
        config.Hotkey.Shift = HotkeyShift;
        config.Hotkey.Win = HotkeyWin;
        config.Hotkey.VirtualKey = KeyOptions[Math.Clamp(HotkeyKeyIndex, 0, KeyOptions.Count - 1)].Vk;
        config.Gestures.BottomEdge = GestureBottom;
        config.Gestures.RightEdge = GestureRight;
        config.GlyphStyle = (GlyphStyle)Math.Clamp(GlyphStyleIndex, 0, 2);
        config.StartupApps = StartupApps
            .Where(r => !string.IsNullOrWhiteSpace(r.Path))
            .Select(r => new StartupAppConfig
            {
                Path = r.Path.Trim(), Args = r.Args.Trim(), Enabled = r.Enabled, Elevated = r.Elevated,
            })
            .ToList();
    }

    public void Save()
    {
        ApplyTo(_config);
        ConfigStore.Save(_config);
        Log.Info("Settings saved.");
    }

    public AppConfig SnapshotForTest()
    {
        ApplyTo(_config);
        return _config;
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
