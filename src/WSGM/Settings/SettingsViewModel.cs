using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using WSGM.Core;
using WSGM.Input;

namespace WSGM.Settings;

/// <summary>Editable settings for one program launched after the shell starts.</summary>
public sealed class StartupAppRow : INotifyPropertyChanged
{
    /// <summary>Raised after an editable startup-app field changes.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;
    private string _path = "";
    private string _args = "";
    private bool _enabled = true;
    private bool _elevated;
    private bool _autoRelaunch;

    /// <summary>Gets or sets the executable or protocol to launch.</summary>
    public string Path { get => _path; set { _path = value; Raise(nameof(Path)); } }

    /// <summary>Gets or sets the command-line arguments passed to the program.</summary>
    public string Args { get => _args; set { _args = value; Raise(nameof(Args)); } }

    /// <summary>Gets or sets whether this program participates in startup.</summary>
    public bool Enabled { get => _enabled; set { _enabled = value; Raise(nameof(Enabled)); } }

    /// <summary>Gets or sets whether the program needs an elevated launch.</summary>
    public bool Elevated { get => _elevated; set { _elevated = value; Raise(nameof(Elevated)); } }

    /// <summary>Gets or sets whether the program is watched and restarted when it exits.</summary>
    public bool AutoRelaunch { get => _autoRelaunch; set { _autoRelaunch = value; Raise(nameof(AutoRelaunch)); } }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Binds persisted shell, startup, input, and display settings to the Settings window.</summary>
public sealed class SettingsViewModel : INotifyPropertyChanged
{
    /// <summary>Raised after a settings value or dependent display value changes.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly AppConfig _config;

    /// <summary>Loads the current configuration and discovers locally installed startup suggestions.</summary>
    public SettingsViewModel()
    {
        _config = ConfigStore.Load();

        SteamAutoRelaunch = _config.SteamAutoRelaunch;
        StartupDelayMs = _config.StartupDelayMs;
        StaggerDelayMs = _config.StaggerDelayMs;
        BootSplashEnabled = _config.BootSplashEnabled;
        GameModeBootEnabled = _config.GameModeBootEnabled;
        _hotkey = _config.Hotkey;
        _chord = _config.GamepadChord;
        GestureBottom = _config.Gestures.BottomEdge;
        GestureRight = _config.Gestures.RightEdge;
        BottomEdgeActionIndex = (int)_config.Gestures.BottomEdgeAction;
        GlyphStyleIndex = (int)_config.GlyphStyle;

        foreach (var app in _config.StartupApps)
        {
            StartupApps.Add(new StartupAppRow
            {
                Path = app.Path,
                Args = app.Args,
                Enabled = app.Enabled,
                Elevated = app.Elevated,
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
    /// <summary>Gets or sets the selected discovered startup-app suggestion.</summary>
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

    /// <summary>Adds the selected discovered program when it has a concrete executable path.</summary>
    /// <returns><see langword="true"/> when a startup row was added; otherwise the caller should open a file picker.</returns>
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

    // --- Sign-in behavior ---
    private bool _gameModeBootEnabled = true;

    /// <summary>Gets or sets whether the logon service boots the session into game
    /// mode at sign-in. Persisted via Save; the boot manifest is rewritten there.</summary>
    public bool GameModeBootEnabled { get => _gameModeBootEnabled; set { _gameModeBootEnabled = value; Raise(nameof(GameModeBootEnabled)); } }

    /// <summary>Gets whether the LEGACY shell registration is still active for this
    /// account (pre-service installs). Shows the migration Restore card.</summary>
    public bool ShellInstalled => ShellRegistration.IsInstalledForThisExe();

    /// <summary>Gets a user-facing explanation of the sign-in behavior.</summary>
    public string ShellStatusText => ShellInstalled
        ? "LEGACY shell registration is still active for this account — use Restore below. Game mode now starts through the WSGM logon service instead."
        : "Game mode starts at sign-in through the WSGM logon service. Explorer stays your Windows shell.";

    // --- UAC prompt level ---
    /// <summary>Gets whether UAC consent prompts are disabled for the machine.</summary>
    public bool UacPromptsDisabled => UacSettings.Read().PromptsDisabled;

    /// <summary>Gets a user-facing explanation of the current UAC prompt policy.</summary>
    public string UacStatusText => UacPromptsDisabled
        ? "UAC prompts are OFF — elevated apps start silently. Windows still runs with UAC enabled, but anything that asks for administrator rights gets them without asking you."
        : "UAC prompts are ON (Windows default). Each elevated launch shows a consent dialog, which interrupts boot-to-game on a handheld.";

    /// <summary>Toggles the machine UAC prompt level. Needs one elevation prompt.
    /// Returns false when elevation was declined or the write failed.</summary>
    /// <param name="disable">Whether to suppress consent prompts.</param>
    /// <returns><see langword="true"/> when Windows accepted the policy change.</returns>
    public bool SetUacPrompts(bool disable)
    {
        var ok = UacSettings.RequestChange(disable);
        Raise(nameof(UacPromptsDisabled));
        Raise(nameof(UacStatusText));
        return ok;
    }

    // --- Lock on wake ---
    /// <summary>Gets whether Windows will skip a sign-in prompt after display sleep.</summary>
    public bool LockOnWakeDisabled => LockScreenSettings.SignInOnWakeDisabled();

    /// <summary>Gets a user-facing explanation of the wake sign-in policy.</summary>
    public string LockOnWakeStatusText => LockOnWakeDisabled
        ? "Waking the device goes straight back to your game — no sign-in screen."
        : "Windows currently asks you to sign in again after the screen sleeps (Windows default).";

    /// <summary>Changes the Windows wake sign-in policy through the elevated helper.</summary>
    /// <param name="disable">Whether to bypass the sign-in prompt after display sleep.</param>
    /// <returns><see langword="true"/> when Windows accepted the policy change.</returns>
    public bool SetLockOnWake(bool disable)
    {
        var ok = LockScreenSettings.RequestChange(disable);
        Raise(nameof(LockOnWakeDisabled));
        Raise(nameof(LockOnWakeStatusText));
        return ok;
    }

    /// <summary>Gets whether WSGM has a copy in its stable per-user install directory.</summary>
    public bool AppInstalled => Installer.IsAppInstalled;

    /// <summary>Gets a user-facing explanation of the current installation state.</summary>
    public string AppStatusText => Installer.IsRunningFromInstallDir
        ? $"Installed at {Installer.InstallDir}."
        : Installer.IsAppInstalled
            ? $"Running portable — an installed copy exists at {Installer.InstallDir}. \"Install app\" updates it."
            : "Running portable — not installed yet. Installing copies WSGM to a stable per-user location and adds it to Start Menu and Settings → Apps.";

    /// <summary>Installs or updates the app files without changing shell registration.</summary>
    public void InstallApp()
    {
        Installer.InstallApp();
        RaiseShellStatus();
    }

    /// <summary>Migration: removes the legacy shell registration and restores the
    /// saved previous shell. The logon service keeps starting game mode afterwards.</summary>
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
    /// <summary>Gets Steam discovery status because game mode requires Steam.</summary>
    public string SteamStatusText => Steam.ExePath is { } exe
        ? $"Detected: {exe}"
        : "Steam was not found on this PC. Install Steam first — WSGM is Steam-exclusive.";

    private bool _steamAutoRelaunch;

    /// <summary>Gets or sets whether the Steam monitor restarts Steam after an unexpected exit.</summary>
    public bool SteamAutoRelaunch { get => _steamAutoRelaunch; set { _steamAutoRelaunch = value; Raise(nameof(SteamAutoRelaunch)); } }

    // --- Startup apps ---
    /// <summary>Gets the ordered startup programs shown in the settings editor.</summary>
    public ObservableCollection<StartupAppRow> StartupApps { get; } = [];

    private int _startupDelayMs;

    /// <summary>Gets or sets the initial delay before launching configured startup programs.</summary>
    public int StartupDelayMs { get => _startupDelayMs; set { _startupDelayMs = value; Raise(nameof(StartupDelayMs)); } }

    private int _staggerDelayMs;

    /// <summary>Gets or sets the delay between successive configured startup programs.</summary>
    public int StaggerDelayMs { get => _staggerDelayMs; set { _staggerDelayMs = value; Raise(nameof(StaggerDelayMs)); } }

    private bool _bootSplashEnabled;

    /// <summary>Gets or sets whether a splash window is shown while game mode starts.</summary>
    public bool BootSplashEnabled { get => _bootSplashEnabled; set { _bootSplashEnabled = value; Raise(nameof(BootSplashEnabled)); } }

    /// <summary>Adds a blank startup-program row for manual configuration.</summary>
    public void AddStartupApp() => StartupApps.Add(new StartupAppRow());

    /// <summary>Removes a startup-program row.</summary>
    /// <param name="row">The row to remove.</param>
    public void RemoveStartupApp(StartupAppRow row) => StartupApps.Remove(row);

    /// <summary>Moves a startup-program row by one or more positions when the target remains in range.</summary>
    /// <param name="row">The row to move.</param>
    /// <param name="delta">The signed number of positions to move the row.</param>
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

    /// <summary>Gets the current keyboard shortcut or the key-recording prompt.</summary>
    public string HotkeyText => _hotkeyRecording ? "Press keys…" : KeyRecorder.Describe(_hotkey);

    /// <summary>Gets the current controller chord or the button-recording prompt.</summary>
    public string ChordText => _chordRecording
        ? "Press buttons…"
        : _chord.Enabled && _chord.Buttons != 0
            ? GamepadService.Describe((GamepadButtons)_chord.Buttons, _chord.Hold)
            : "None";

    private bool _hotkeyRecording;
    private bool _chordRecording;

    /// <summary>Gets whether either shortcut recorder currently owns input.</summary>
    public bool IsRecording => _hotkeyRecording || _chordRecording;

    /// <summary>Starts or stops keyboard-shortcut recording.</summary>
    /// <param name="recording">Whether the next eligible key combination should be captured.</param>
    public void SetHotkeyRecording(bool recording)
    {
        _hotkeyRecording = recording;
        Raise(nameof(HotkeyText));
        Raise(nameof(IsRecording));
    }

    /// <summary>Starts or stops controller-chord recording.</summary>
    /// <param name="recording">Whether the next eligible controller chord should be captured.</param>
    public void SetChordRecording(bool recording)
    {
        _chordRecording = recording;
        Raise(nameof(ChordText));
        Raise(nameof(IsRecording));
    }

    /// <summary>Stores a recorded keyboard shortcut. A zero virtual key clears it.</summary>
    /// <param name="modifiers">Win32 modifier flags captured with the virtual key.</param>
    /// <param name="vk">The captured Win32 virtual key, or zero to clear the shortcut.</param>
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

    /// <summary>Stores a recorded controller chord. No buttons clears it.</summary>
    /// <param name="buttons">The buttons captured from one controller.</param>
    /// <param name="hold">Whether the chord activates on a hold rather than an edge.</param>
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

    /// <summary>Clears the keyboard shortcut.</summary>
    public void ClearHotkey() => ApplyRecordedHotkey(0, 0);

    /// <summary>Clears the controller chord.</summary>
    public void ClearChord() => ApplyRecordedChord(0, false);

    // --- Gestures / glyphs ---
    private bool _gestureBottom, _gestureRight;
    private int _glyphStyleIndex;

    /// <summary>Gets or sets whether a bottom-edge swipe opens the overlay.</summary>
    public bool GestureBottom { get => _gestureBottom; set { _gestureBottom = value; Raise(nameof(GestureBottom)); } }

    /// <summary>Gets or sets whether a right-edge swipe opens the overlay.</summary>
    public bool GestureRight { get => _gestureRight; set { _gestureRight = value; Raise(nameof(GestureRight)); } }

    private int _bottomEdgeActionIndex;
    /// <summary>Gets or sets the selected bottom-edge swipe action index
    /// (matches the <see cref="EdgeAction"/> enum order).</summary>
    public int BottomEdgeActionIndex { get => _bottomEdgeActionIndex; set { _bottomEdgeActionIndex = value; Raise(nameof(BottomEdgeActionIndex)); } }

    /// <summary>Gets the bottom-edge action names presented by the settings selector.</summary>
    public List<string> BottomEdgeActions { get; } = ["Quick access", "Taskbar"];

    /// <summary>Gets or sets the selected controller-glyph family index.</summary>
    public int GlyphStyleIndex { get => _glyphStyleIndex; set { _glyphStyleIndex = value; Raise(nameof(GlyphStyleIndex)); } }

    /// <summary>Gets the controller-glyph family names presented by the settings selector.</summary>
    public List<string> GlyphStyles { get; } = ["Xbox", "PlayStation", "Nintendo"];

    // --- Save ---
    private void ApplyTo(AppConfig config)
    {
        config.SteamAutoRelaunch = SteamAutoRelaunch;
        config.StartupDelayMs = StartupDelayMs;
        config.StaggerDelayMs = StaggerDelayMs;
        config.BootSplashEnabled = BootSplashEnabled;
        config.GameModeBootEnabled = GameModeBootEnabled;
        config.Hotkey = _hotkey;
        config.GamepadChord = _chord;
        config.Gestures.BottomEdge = GestureBottom;
        config.Gestures.RightEdge = GestureRight;
        config.Gestures.BottomEdgeAction = (EdgeAction)Math.Clamp(BottomEdgeActionIndex, 0, 1);
        config.GlyphStyle = (GlyphStyle)Math.Clamp(GlyphStyleIndex, 0, 2);
        config.StartupApps = StartupApps
            .Where(r => !string.IsNullOrWhiteSpace(r.Path))
            .Select(r => new StartupAppConfig
            {
                Path = r.Path.Trim(),
                Args = r.Args.Trim(),
                Enabled = r.Enabled,
                Elevated = r.Elevated,
                AutoRelaunch = r.AutoRelaunch,
            })
            .ToList();
    }

    /// <summary>Merges UI-owned values with fresh persisted state and saves the result.</summary>
    public void Save()
    {
        SaveMerged();
    }

    /// <summary>Applies the UI-owned fields over a FRESH load and saves that.
    /// While this window is open, the elevated one-shots (UAC, lock-on-wake) and
    /// the shell persist registry snapshots and display-scale/slate state to the
    /// same file; serializing the startup-time _config would reset every one of
    /// those fields to defaults on disk, breaking exact restore on uninstall.
    /// The config mutex only serializes individual reads/writes — it cannot
    /// merge — so the merge has to happen here.</summary>
    private AppConfig SaveMerged()
    {
        var config = ConfigStore.Load();
        ApplyTo(config);
        ConfigStore.Save(config);
        // Keep the logon service's view in sync — every save may change the
        // enabled flag or the elevation inputs (elevated startup apps).
        BootManifestWriter.WriteCurrent(config);
        Log.Info("Settings saved.");
        return config;
    }

    /// <summary>Builds an isolated configuration snapshot for controller tests.</summary>
    /// <returns>A copy that will not change when this view model is later saved or installed.</returns>
    public AppConfig SnapshotForTest()
    {
        ApplyTo(_config);
        // A real copy (source-gen JSON round-trip, AOT-safe): the test
        // OverlayController must not see later Save()/Install() mutations of the
        // live _config outside its ApplyConfig wholesale-replace contract.
        var json = JsonSerializer.Serialize(_config, ConfigJsonContext.Default.AppConfig);
        return JsonSerializer.Deserialize(json, ConfigJsonContext.Default.AppConfig) ?? new AppConfig();
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
