using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using OpenFSE.Core;
using OpenFSE.Input;

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
            });
        }

        BuildLauncherChoices();
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

    // --- Launcher picker ---
    /// <summary>Detected launchers first, then any not-installed ones (marked), then
    /// "Custom…". Selecting an entry fills in every technical field for the user.</summary>
    public List<string> LauncherChoices { get; private set; } = [];
    private List<LauncherPreset?> _launcherPresets = [];

    private int _selectedLauncherIndex;
    public int SelectedLauncherIndex
    {
        get => _selectedLauncherIndex;
        set
        {
            if (_selectedLauncherIndex == value)
            {
                return;
            }
            _selectedLauncherIndex = value;
            Raise(nameof(SelectedLauncherIndex));
            ApplySelectedLauncher();
        }
    }

    private void BuildLauncherChoices()
    {
        var choices = new List<string>();
        var presets = new List<LauncherPreset?>();

        var detected = KnownLaunchers.Detected();
        foreach (var preset in detected)
        {
            choices.Add(preset.Name);
            presets.Add(preset);
        }
        foreach (var preset in KnownLaunchers.All)
        {
            if (!detected.Contains(preset))
            {
                choices.Add($"{preset.Name}  (not installed)");
                presets.Add(preset);
            }
        }
        choices.Add("Custom…");
        presets.Add(null);

        LauncherChoices = choices;
        _launcherPresets = presets;

        // Preselect whatever the config already points at.
        var current = KnownLaunchers.MatchByPath(_config.HomeApp.Path);
        var index = current is null ? -1 : presets.IndexOf(current);
        _selectedLauncherIndex = index >= 0
            ? index
            : string.IsNullOrWhiteSpace(_config.HomeApp.Path) && detected.Count > 0 ? 0 : choices.Count - 1;

        // A fresh config with a detected launcher: fill it in immediately so the
        // user can just hit "Install as shell".
        if (string.IsNullOrWhiteSpace(_config.HomeApp.Path) && detected.Count > 0)
        {
            ApplySelectedLauncher();
        }
    }

    private void ApplySelectedLauncher()
    {
        if (_selectedLauncherIndex < 0 || _selectedLauncherIndex >= _launcherPresets.Count)
        {
            return;
        }
        var preset = _launcherPresets[_selectedLauncherIndex];
        if (preset is null)
        {
            IsCustomLauncher = true;
            return;     // Custom: leave the user's own values alone
        }

        IsCustomLauncher = false;
        HomeAppPath = preset.InstalledPath ?? preset.ExeName;
        HomeAppArgs = preset.Args;
        _config.HomeApp.ProcessNames = preset.ProcessNames;
        _config.HomeApp.WindowClass = preset.WindowClass;
        _config.HomeApp.ActivationProtocol = preset.ActivationProtocol;
        Raise(nameof(LauncherHintText));
    }

    private bool _isCustomLauncher;
    public bool IsCustomLauncher
    {
        get => _isCustomLauncher;
        private set { _isCustomLauncher = value; Raise(nameof(IsCustomLauncher)); Raise(nameof(LauncherHintText)); }
    }

    public string LauncherHintText
    {
        get
        {
            if (IsCustomLauncher)
            {
                return "Custom: pick the executable yourself. Advanced settings below control how OpenFSE recognises its window.";
            }
            var preset = _selectedLauncherIndex >= 0 && _selectedLauncherIndex < _launcherPresets.Count
                ? _launcherPresets[_selectedLauncherIndex]
                : null;
            if (preset is null)
            {
                return "";
            }
            return string.IsNullOrEmpty(preset.InstalledPath)
                ? $"Not installed on this PC — get it from {preset.DownloadUrl}"
                : $"Detected: {preset.InstalledPath}";
        }
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
        config.HomeApp.Path = HomeAppPath.Trim();
        config.HomeApp.Args = HomeAppArgs.Trim();
        config.HomeApp.Elevated = HomeAppElevated;
        config.HomeApp.AutoRelaunch = HomeAppAutoRelaunch;
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
