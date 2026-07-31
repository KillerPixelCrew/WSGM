using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using WSGM.Core;
using WSGM.Input;

namespace WSGM.Settings;

public sealed class StartupAppRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private string _path = "";
    private string _args = "";
    private bool _enabled = true;
    private bool _elevated;
    private bool _autoRelaunch;

    public string Path { get => _path; set { _path = value; Raise(nameof(Path)); } }
    public string Args { get => _args; set { _args = value; Raise(nameof(Args)); } }
    public bool Enabled { get => _enabled; set { _enabled = value; Raise(nameof(Enabled)); } }
    public bool Elevated { get => _elevated; set { _elevated = value; Raise(nameof(Elevated)); } }
    public bool AutoRelaunch { get => _autoRelaunch; set { _autoRelaunch = value; Raise(nameof(AutoRelaunch)); } }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class SettingsViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly AppConfig _config;

    public SettingsViewModel()
    {
        _config = ConfigStore.Load();

        SteamAutoRelaunch = _config.SteamAutoRelaunch;
        StartupDelayMs = _config.StartupDelayMs;
        StaggerDelayMs = _config.StaggerDelayMs;
        _hotkey = _config.Hotkey;
        _chord = _config.GamepadChord;
        GestureBottom = _config.Gestures.BottomEdge;
        GestureRight = _config.Gestures.RightEdge;
        GlyphStyleIndex = (int)_config.GlyphStyle;

        foreach (var app in _config.StartupApps)
        {
            StartupApps.Add(new StartupAppRow
            {
                Path = app.Path, Args = app.Args, Enabled = app.Enabled, Elevated = app.Elevated,
                AutoRelaunch = app.AutoRelaunch,
            });
        }

        BuildStartupSuggestions();
    }

    // --- Startup app suggestions ---
    /// <summary>Common handheld companions found on this PC, offered as one-click adds
    /// instead of making the user hunt for exe paths.</summary>
    public List<string> StartupSuggestions { get; private set; } = [];
    private List<(string Path, bool Elevated)> _startupSuggestionTargets = [];

    private int _selectedSuggestionIndex;
    public int SelectedSuggestionIndex
    {
        get => _selectedSuggestionIndex;
        set { _selectedSuggestionIndex = value; Raise(nameof(SelectedSuggestionIndex)); }
    }

    private void BuildStartupSuggestions()
    {
        var names = new List<string>();
        var targets = new List<(string, bool)>();

        foreach (var (label, path, elevated) in KnownStartupApps.Detected())
        {
            names.Add(label);
            targets.Add((path, elevated));
        }
        names.Add("Choose a program…");
        targets.Add(("", false));

        StartupSuggestions = names;
        _startupSuggestionTargets = targets;
        _selectedSuggestionIndex = 0;
    }

    /// <summary>Adds the selected suggestion (or an empty row for a manual pick).</summary>
    public bool AddSelectedStartupApp()
    {
        if (_selectedSuggestionIndex < 0 || _selectedSuggestionIndex >= _startupSuggestionTargets.Count)
        {
            return false;
        }
        var (path, elevated) = _startupSuggestionTargets[_selectedSuggestionIndex];
        if (string.IsNullOrEmpty(path))
        {
            return false;   // caller opens the file picker
        }
        StartupApps.Add(new StartupAppRow { Path = path, Elevated = elevated, Enabled = true });
        return true;
    }

    // --- Shell status ---
    public bool ShellInstalled => ShellRegistration.IsInstalledForThisExe();
    public string ShellStatusText => ShellInstalled
        ? "WSGM IS your Windows shell for this account. Sign out and back in for changes to take effect."
        : "WSGM is NOT your Windows shell.";

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
            : "Running portable — not installed yet. Installing copies WSGM to a stable per-user location and adds it to Start Menu and Settings → Apps.";

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

    // --- Steam (the only launcher; located via registry, nothing to configure) ---
    public string SteamStatusText => Steam.ExePath is { } exe
        ? $"Detected: {exe}"
        : "Steam was not found on this PC. Install Steam first — WSGM is Steam-exclusive.";

    private bool _steamAutoRelaunch;
    public bool SteamAutoRelaunch { get => _steamAutoRelaunch; set { _steamAutoRelaunch = value; Raise(nameof(SteamAutoRelaunch)); } }

    // --- Startup apps ---
    public ObservableCollection<StartupAppRow> StartupApps { get; } = [];

    private int _startupDelayMs;
    public int StartupDelayMs { get => _startupDelayMs; set { _startupDelayMs = value; Raise(nameof(StartupDelayMs)); } }

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

    // --- Overlay shortcuts (recorded, not picked from a list) ---
    private HotkeyConfig _hotkey = new();
    private GamepadChordConfig _chord = new();

    public string HotkeyText => _hotkeyRecording ? "Press keys…" : KeyRecorder.Describe(_hotkey);
    public string ChordText => _chordRecording
        ? "Press buttons…"
        : _chord.Enabled && _chord.Buttons != 0
            ? GamepadService.Describe((GamepadButtons)_chord.Buttons, _chord.Hold)
            : "None";

    private bool _hotkeyRecording;
    private bool _chordRecording;
    public bool IsRecording => _hotkeyRecording || _chordRecording;

    public void SetHotkeyRecording(bool recording)
    {
        _hotkeyRecording = recording;
        Raise(nameof(HotkeyText));
        Raise(nameof(IsRecording));
    }

    public void SetChordRecording(bool recording)
    {
        _chordRecording = recording;
        Raise(nameof(ChordText));
        Raise(nameof(IsRecording));
    }

    /// <summary>Stores a recorded keyboard shortcut. vk == 0 clears it.</summary>
    public void ApplyRecordedHotkey(uint modifiers, int vk)
    {
        _hotkey = new HotkeyConfig
        {
            Enabled = vk != 0,
            Ctrl = (modifiers & Interop.NativeMethods.ModControl) != 0,
            Alt = (modifiers & Interop.NativeMethods.ModAlt) != 0,
            Shift = (modifiers & Interop.NativeMethods.ModShift) != 0,
            Win = (modifiers & Interop.NativeMethods.ModWin) != 0,
            VirtualKey = vk,
        };
        SetHotkeyRecording(false);
    }

    /// <summary>Stores a recorded controller chord. Empty buttons clears it.</summary>
    public void ApplyRecordedChord(GamepadButtons buttons, bool hold)
    {
        _chord = new GamepadChordConfig
        {
            Enabled = buttons != 0,
            Buttons = (int)buttons,
            Hold = hold,
        };
        SetChordRecording(false);
    }

    public void ClearHotkey() => ApplyRecordedHotkey(0, 0);
    public void ClearChord() => ApplyRecordedChord(0, false);

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
        config.SteamAutoRelaunch = SteamAutoRelaunch;
        config.StartupDelayMs = StartupDelayMs;
        config.StaggerDelayMs = StaggerDelayMs;
        config.Hotkey = _hotkey;
        config.GamepadChord = _chord;
        config.Gestures.BottomEdge = GestureBottom;
        config.Gestures.RightEdge = GestureRight;
        config.GlyphStyle = (GlyphStyle)Math.Clamp(GlyphStyleIndex, 0, 2);
        config.StartupApps = StartupApps
            .Where(r => !string.IsNullOrWhiteSpace(r.Path))
            .Select(r => new StartupAppConfig
            {
                Path = r.Path.Trim(), Args = r.Args.Trim(), Enabled = r.Enabled, Elevated = r.Elevated,
                AutoRelaunch = r.AutoRelaunch,
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
