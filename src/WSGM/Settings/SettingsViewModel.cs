using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using Avalonia;
using WSGM.Core;
using WSGM.Input;
using WSGM.Themes;

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
        : this(ConfigStore.Load()) { }

    /// <summary>Builds the view model over an ALREADY LOADED configuration instead of
    /// reading <c>%LOCALAPPDATA%\WSGM\config.json</c>. Tests must use this overload: the
    /// parameterless constructor's <see cref="ConfigStore.Load"/> reads the developer's
    /// real config, and its corrupt-file branch writes <c>config.bad.json</c> next to it,
    /// so merely constructing the view model touches the real per-user directory.</summary>
    /// <param name="config">The configuration this view model edits. It is taken over,
    /// not copied — the save path re-loads and merges before persisting anyway.</param>
    internal SettingsViewModel(AppConfig config)
    {
        SaveCommand = new RelayCommand(() =>
        {
            try
            {
                Save();
                StatusText = $"Saved {DateTime.Now:HH:mm:ss}";
            }
            catch (Exception ex)
            {
                // A failed config write (locked/read-only file) must not escape a
                // command invocation — in-shell it would hit the panic path.
                Log.Error("Saving settings failed", ex);
                StatusText = $"Save failed: {ex.Message}";
            }
        });
        InstallAppCommand = new RelayCommand(() =>
        {
            try
            {
                InstallApp();
            }
            catch (Exception ex)
            {
                Log.Error("App install failed", ex);
                StatusText = $"Install failed: {ex.Message}";
            }
        });
        UninstallCommand = new RelayCommand(Uninstall);
        OpenLogLocationCommand = new RelayCommand(OpenLogLocation);
        RemoveAppCommand = new RelayCommand<StartupAppRow>(row =>
        {
            if (row is not null)
            {
                RemoveStartupApp(row);
            }
        });
        MoveUpCommand = new RelayCommand<StartupAppRow>(row =>
        {
            if (row is not null)
            {
                MoveStartupApp(row, -1);
            }
        });
        MoveDownCommand = new RelayCommand<StartupAppRow>(row =>
        {
            if (row is not null)
            {
                MoveStartupApp(row, +1);
            }
        });

        // Normalize so an injected bare AppConfig gets the same non-null nested
        // sections (and clamped splash numbers) the load path guarantees.
        _config = ConfigStore.Normalize(config);

        SteamAutoRelaunch = _config.SteamAutoRelaunch;
        SteamGridDbApiKey = _config.SteamGridDbApiKey;
        StartupDelayMs = _config.StartupDelayMs;
        StaggerDelayMs = _config.StaggerDelayMs;
        BootSplashEnabled = _config.BootSplashEnabled;
        GameModeBootEnabled = _config.GameModeBootEnabled;
        SteamInputLeaseEnabled = _config.SteamInputLeaseEnabled;
        CefEnabled = _config.Cef.Enabled;
        CefLibraryTabs = _config.Cef.LibraryTabs;
        CefCardManager = _config.Cef.CardManager;
        CefSdFormat = _config.Cef.SdFormat;
        CefArtwork = _config.Cef.Artwork;
        CefWifiIndicator = _config.Cef.WifiIndicator;
        _hotkey = _config.Hotkey;
        _chord = _config.GamepadChord;
        GestureBottom = _config.Gestures.BottomEdge;
        GestureRight = _config.Gestures.RightEdge;
        GestureLeftSteamMenu = _config.Gestures.LeftEdgeSteamMenu;
        GestureTopSteamQuickAccess = _config.Gestures.TopEdgeSteamQuickAccess;
        BottomEdgeActionIndex = (int)_config.Gestures.BottomEdgeAction;
        GlyphStyleIndex = (int)_config.GlyphStyle;
        AccentColorHex = _config.AccentColor;
        LoadSplash(_config.Splash);

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

    // --- Commands (bound by the Settings pages; bodies stay on the named methods) ---
    /// <summary>Gets the command that merges and persists the edited settings,
    /// reporting the outcome (including the last-save time) via <see cref="StatusText"/>.</summary>
    public RelayCommand SaveCommand { get; }

    /// <summary>Gets the command that installs or updates the per-user app copy.</summary>
    public RelayCommand InstallAppCommand { get; }

    /// <summary>Gets the command that removes the legacy shell registration and
    /// restores the saved previous shell.</summary>
    public RelayCommand UninstallCommand { get; }

    /// <summary>Gets the command that reveals wsgm.log in Explorer.</summary>
    public RelayCommand OpenLogLocationCommand { get; }

    /// <summary>Gets the command that removes one startup-program row.</summary>
    public RelayCommand<StartupAppRow> RemoveAppCommand { get; }

    /// <summary>Gets the command that moves one startup-program row up.</summary>
    public RelayCommand<StartupAppRow> MoveUpCommand { get; }

    /// <summary>Gets the command that moves one startup-program row down.</summary>
    public RelayCommand<StartupAppRow> MoveDownCommand { get; }

    private string _statusText = "";

    /// <summary>Gets or sets the transient status line shown in the window's
    /// bottom strip: last-save time on success, otherwise the failure text.</summary>
    public string StatusText { get => _statusText; set { _statusText = value; Raise(nameof(StatusText)); } }

    /// <summary>Gets the compact logon-service state for the status strip,
    /// derived from the same flag the boot manifest is projected from.</summary>
    public string ServiceStateText => GameModeBootEnabled
        ? "Game-mode boot: on"
        : "Game-mode boot: off";

    /// <summary>Gets the compact shell state for the status strip, derived from
    /// the same legacy-registration check as <see cref="ShellStatusText"/>.</summary>
    public string ShellStateText => ShellInstalled
        ? "Shell: legacy WSGM registration"
        : "Shell: Explorer";

    private void OpenLogLocation()
    {
        try
        {
            var log = System.IO.Path.Combine(Log.Directory, "wsgm.log");
            // Absolute system path: a relative name would resolve via the process
            // working directory, which is the user-writable install dir.
            var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var explorer = System.IO.Path.Combine(windir, "explorer.exe");
            // Select the file when it exists so the user lands right on it;
            // otherwise just open the folder.
            var psi = System.IO.File.Exists(log)
                ? new System.Diagnostics.ProcessStartInfo(explorer, $"/select,\"{log}\"")
                : new System.Diagnostics.ProcessStartInfo(Log.Directory);
            psi.UseShellExecute = true;
            psi.WorkingDirectory = windir;
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not open the log location: {ex.Message}");
            StatusText = $"Could not open the log location: {ex.Message}";
        }
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
    public bool GameModeBootEnabled { get => _gameModeBootEnabled; set { _gameModeBootEnabled = value; Raise(nameof(GameModeBootEnabled)); Raise(nameof(ServiceStateText)); } }

    private bool _steamInputLeaseEnabled = true;

    /// <summary>Gets or sets whether WSGM leases the controller away from Steam
    /// Input while its focused surfaces are open. Off = Steam is never touched.</summary>
    public bool SteamInputLeaseEnabled { get => _steamInputLeaseEnabled; set { _steamInputLeaseEnabled = value; Raise(nameof(SteamInputLeaseEnabled)); } }

    private bool _cefEnabled = true;
    private bool _cefLibraryTabs = true;
    private bool _cefCardManager = true;
    private bool _cefSdFormat = true;
    private bool _cefArtwork = true;
    private bool _cefWifiIndicator = true;

    /// <summary>Gets or sets the master Steam CEF integration switch. Off closes the
    /// debug port, injects nothing, and hides the sub-toggles below and the overlay
    /// feature buttons.</summary>
    public bool CefEnabled { get => _cefEnabled; set { _cefEnabled = value; Raise(nameof(CefEnabled)); } }

    /// <summary>Gets or sets the injected library filter tabs, tab order, and native-tab hiding.</summary>
    public bool CefLibraryTabs { get => _cefLibraryTabs; set { _cefLibraryTabs = value; Raise(nameof(CefLibraryTabs)); } }

    /// <summary>Gets or sets the SD-card library manager (card tabs, badges, live labels).</summary>
    public bool CefCardManager { get => _cefCardManager; set { _cefCardManager = value; Raise(nameof(CefCardManager)); } }

    /// <summary>Gets or sets Format SD Card + live library registration.</summary>
    public bool CefSdFormat { get => _cefSdFormat; set { _cefSdFormat = value; Raise(nameof(CefSdFormat)); } }

    /// <summary>Gets or sets the shortcut-artwork changer.</summary>
    public bool CefArtwork { get => _cefArtwork; set { _cefArtwork = value; Raise(nameof(CefArtwork)); } }

    /// <summary>Gets or sets the Big Picture Wi-Fi indicator.</summary>
    public bool CefWifiIndicator { get => _cefWifiIndicator; set { _cefWifiIndicator = value; Raise(nameof(CefWifiIndicator)); } }

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
        Raise(nameof(ShellStateText));
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

    private string _steamGridDbApiKey = "";

    /// <summary>Gets or sets the user's SteamGridDB API key (for the Change Artwork
    /// feature). Empty disables it; get a free key at <see cref="Core.SteamGridDb.KeyPageUrl"/>.</summary>
    public string SteamGridDbApiKey { get => _steamGridDbApiKey; set { _steamGridDbApiKey = value; Raise(nameof(SteamGridDbApiKey)); } }

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
    private bool _gestureBottom;
    private bool _gestureRight;
    private bool _gestureLeftSteamMenu;
    private bool _gestureTopSteamQuickAccess;
    private int _glyphStyleIndex;

    /// <summary>Gets or sets whether a bottom-edge swipe opens the overlay.</summary>
    public bool GestureBottom { get => _gestureBottom; set { _gestureBottom = value; Raise(nameof(GestureBottom)); } }

    /// <summary>Gets or sets whether a right-edge swipe opens the overlay.</summary>
    public bool GestureRight { get => _gestureRight; set { _gestureRight = value; Raise(nameof(GestureRight)); } }

    /// <summary>Gets or sets whether a left-edge swipe opens Steam's Big Picture menu.</summary>
    public bool GestureLeftSteamMenu { get => _gestureLeftSteamMenu; set { _gestureLeftSteamMenu = value; Raise(nameof(GestureLeftSteamMenu)); } }

    /// <summary>Gets or sets whether a top-edge swipe opens Steam's Big Picture quick-access menu.</summary>
    public bool GestureTopSteamQuickAccess { get => _gestureTopSteamQuickAccess; set { _gestureTopSteamQuickAccess = value; Raise(nameof(GestureTopSteamQuickAccess)); } }

    private int _bottomEdgeActionIndex;
    /// <summary>Gets or sets the selected bottom-edge swipe action index
    /// (matches the <see cref="EdgeAction"/> enum order).</summary>
    public int BottomEdgeActionIndex { get => _bottomEdgeActionIndex; set { _bottomEdgeActionIndex = value; Raise(nameof(BottomEdgeActionIndex)); } }

    /// <summary>Gets the bottom-edge action names presented by the settings selector.</summary>
    public List<string> BottomEdgeActions { get; } = ["Quick access", "Taskbar"];

    /// <summary>Gets or sets the selected controller-glyph family index.</summary>
    public int GlyphStyleIndex { get => _glyphStyleIndex; set { _glyphStyleIndex = value; Raise(nameof(GlyphStyleIndex)); Raise(nameof(GlyphStyle)); } }

    /// <summary>Gets the selected glyph family as its enum value — what the
    /// status strip's A/B glyph icons bind to.</summary>
    public GlyphStyle GlyphStyle => (GlyphStyle)Math.Clamp(_glyphStyleIndex, 0, 2);

    /// <summary>Gets the controller-glyph family names presented by the settings selector.</summary>
    public List<string> GlyphStyles { get; } = ["Xbox", "PlayStation", "Nintendo"];

    // --- Appearance: accent color ---
    private string _accentColorHex = AccentPalette.DefaultAccent;

    /// <summary>Gets or sets the UI accent color as a hex string (e.g. "#FF9D3D").
    /// An unparsable value falls back to the default accent when applied.</summary>
    public string AccentColorHex { get => _accentColorHex; set { _accentColorHex = value; Raise(nameof(AccentColorHex)); } }

    // --- Appearance: boot splash ---
    private string _splashText = "";
    private bool _splashTextEnabled = true;
    private string _splashCaption = "";
    private int _splashTitleFontSize = 26;
    private int _splashCaptionFontSize = 12;
    private string _splashTextColorHex = "#FFFFFF";
    private string _splashCaptionColorHex = "#666666";
    private int _splashSpinnerStyleIndex;
    private string _splashSpinnerColorHex = "#FFFFFF";
    private int _splashSpinnerSize = 36;
    private string _splashBackgroundColorHex = "#000000";
    private bool _splashVignetteEnabled;
    private string _splashLogoPath = "";
    private string _splashBackgroundImagePath = "";
    private int _splashLogoMaxSize = 200;
    private int _splashSweepEdgeIndex;

    /// <summary>Gets or sets the splash title text.</summary>
    public string SplashText { get => _splashText; set { _splashText = value; Raise(nameof(SplashText)); } }

    /// <summary>Gets or sets whether the splash text block (title + caption) is rendered.</summary>
    public bool SplashTextEnabled { get => _splashTextEnabled; set { _splashTextEnabled = value; Raise(nameof(SplashTextEnabled)); } }

    /// <summary>Gets or sets the splash caption line; empty = no caption.</summary>
    public string SplashCaption { get => _splashCaption; set { _splashCaption = value; Raise(nameof(SplashCaption)); } }

    /// <summary>Gets or sets the splash title font size in logical pixels.</summary>
    public int SplashTitleFontSize { get => _splashTitleFontSize; set { _splashTitleFontSize = value; Raise(nameof(SplashTitleFontSize)); } }

    /// <summary>Gets or sets the splash caption font size in logical pixels.</summary>
    public int SplashCaptionFontSize { get => _splashCaptionFontSize; set { _splashCaptionFontSize = value; Raise(nameof(SplashCaptionFontSize)); } }

    /// <summary>Gets or sets the splash title color as a hex string.</summary>
    public string SplashTextColorHex { get => _splashTextColorHex; set { _splashTextColorHex = value; Raise(nameof(SplashTextColorHex)); } }

    /// <summary>Gets or sets the splash caption color as a hex string.</summary>
    public string SplashCaptionColorHex { get => _splashCaptionColorHex; set { _splashCaptionColorHex = value; Raise(nameof(SplashCaptionColorHex)); } }

    /// <summary>Gets or sets the selected spinner style index
    /// (matches the <see cref="SplashSpinnerStyle"/> enum order).</summary>
    public int SplashSpinnerStyleIndex { get => _splashSpinnerStyleIndex; set { _splashSpinnerStyleIndex = value; Raise(nameof(SplashSpinnerStyleIndex)); } }

    /// <summary>Gets the spinner style names presented by the settings selector,
    /// order-matched to the <see cref="SplashSpinnerStyle"/> enum.</summary>
    public List<string> SplashSpinnerStyles { get; } =
    [
        "Ring (classic)",
        "Arc",
        "Arcs",
        "Arcs ring",
        "Double bounce",
        "Flip plane",
        "Pulse",
        "Ring",
        "Three dots",
        "Wave",
        "Sweep line",
        "Off",
    ];

    /// <summary>Gets or sets the spinner color as a hex string.</summary>
    public string SplashSpinnerColorHex { get => _splashSpinnerColorHex; set { _splashSpinnerColorHex = value; Raise(nameof(SplashSpinnerColorHex)); } }

    /// <summary>Gets or sets the spinner size in logical pixels.</summary>
    public int SplashSpinnerSize { get => _splashSpinnerSize; set { _splashSpinnerSize = value; Raise(nameof(SplashSpinnerSize)); } }

    /// <summary>Gets or sets the splash background fill color as a hex string.</summary>
    public string SplashBackgroundColorHex { get => _splashBackgroundColorHex; set { _splashBackgroundColorHex = value; Raise(nameof(SplashBackgroundColorHex)); } }

    /// <summary>Gets or sets whether a radial vignette darkens the splash edges.</summary>
    public bool SplashVignetteEnabled { get => _splashVignetteEnabled; set { _splashVignetteEnabled = value; Raise(nameof(SplashVignetteEnabled)); } }

    /// <summary>Gets or sets the splash logo image path; empty = no logo.</summary>
    public string SplashLogoPath { get => _splashLogoPath; set { _splashLogoPath = value; Raise(nameof(SplashLogoPath)); } }

    /// <summary>Gets or sets the splash background image path; empty = solid color.</summary>
    public string SplashBackgroundImagePath { get => _splashBackgroundImagePath; set { _splashBackgroundImagePath = value; Raise(nameof(SplashBackgroundImagePath)); } }

    /// <summary>Gets or sets the maximum logo edge length in logical pixels.</summary>
    public int SplashLogoMaxSize { get => _splashLogoMaxSize; set { _splashLogoMaxSize = value; Raise(nameof(SplashLogoMaxSize)); } }

    /// <summary>Gets the sweep-line edge names presented by the settings selector,
    /// order-matched to the <see cref="SweepEdge"/> enum.</summary>
    public List<string> SplashSweepEdges { get; } = ["Bottom", "Top"];

    /// <summary>Gets or sets the sweep-line edge as an index into <see cref="SplashSweepEdges"/>.</summary>
    public int SplashSweepEdgeIndex
    {
        get => _splashSweepEdgeIndex;
        set { _splashSweepEdgeIndex = value; Raise(nameof(SplashSweepEdgeIndex)); }
    }

    /// <summary>Gets the placement mode names presented by the settings selectors,
    /// order-matched to the <see cref="SplashPlacementMode"/> enum ("With text" is
    /// honored by the splash for the spinner and logo only).</summary>
    public List<string> SplashPlacementModes { get; } = ["Anchored", "Absolute", "With text"];

    /// <summary>Gets the placement mode names offered for the text element itself,
    /// which cannot ride its own stack — a prefix of <see cref="SplashPlacementModes"/>
    /// so the shared enum-index mapping stays valid.</summary>
    public List<string> SplashTextPlacementModes { get; } = ["Anchored", "Absolute"];

    /// <summary>Gets the nine-grid anchor names presented by the settings selectors,
    /// order-matched to the <see cref="SplashPlacementAnchor"/> enum.</summary>
    public List<string> SplashPlacementAnchors { get; } =
    [
        "Top left",
        "Top center",
        "Top right",
        "Center left",
        "Center",
        "Center right",
        "Bottom left",
        "Bottom center",
        "Bottom right",
    ];

    // Text placement
    private int _splashTextPlacementModeIndex;
    private int _splashTextAnchorIndex = (int)SplashPlacementAnchor.Center;
    private int _splashTextPaddingX = 64;
    private int _splashTextPaddingY = 64;
    private int _splashTextX;
    private int _splashTextY;

    /// <summary>Gets or sets the text placement mode index
    /// (matches the <see cref="SplashPlacementMode"/> enum order).</summary>
    public int SplashTextPlacementModeIndex { get => _splashTextPlacementModeIndex; set { _splashTextPlacementModeIndex = value; Raise(nameof(SplashTextPlacementModeIndex)); Raise(nameof(SplashTextPlacementIsAnchor)); Raise(nameof(SplashTextPlacementIsAbsolute)); } }

    /// <summary>Gets whether the text placement editor shows the anchor + padding fields.</summary>
    public bool SplashTextPlacementIsAnchor => _splashTextPlacementModeIndex == (int)SplashPlacementMode.Anchor;

    /// <summary>Gets whether the text placement editor shows the absolute X/Y fields.</summary>
    public bool SplashTextPlacementIsAbsolute => _splashTextPlacementModeIndex == (int)SplashPlacementMode.Absolute;

    /// <summary>Gets or sets the text anchor index
    /// (matches the <see cref="SplashPlacementAnchor"/> enum order).</summary>
    public int SplashTextAnchorIndex { get => _splashTextAnchorIndex; set { _splashTextAnchorIndex = value; Raise(nameof(SplashTextAnchorIndex)); } }

    /// <summary>Gets or sets the text horizontal padding from the anchored edge.</summary>
    public int SplashTextPaddingX { get => _splashTextPaddingX; set { _splashTextPaddingX = value; Raise(nameof(SplashTextPaddingX)); } }

    /// <summary>Gets or sets the text vertical padding from the anchored edge.</summary>
    public int SplashTextPaddingY { get => _splashTextPaddingY; set { _splashTextPaddingY = value; Raise(nameof(SplashTextPaddingY)); } }

    /// <summary>Gets or sets the text absolute X coordinate in logical pixels.</summary>
    public int SplashTextX { get => _splashTextX; set { _splashTextX = value; Raise(nameof(SplashTextX)); } }

    /// <summary>Gets or sets the text absolute Y coordinate in logical pixels.</summary>
    public int SplashTextY { get => _splashTextY; set { _splashTextY = value; Raise(nameof(SplashTextY)); } }

    // Spinner placement
    private int _splashSpinnerPlacementModeIndex = (int)SplashPlacementMode.WithText;
    private int _splashSpinnerAnchorIndex = (int)SplashPlacementAnchor.Center;
    private int _splashSpinnerPaddingX = 64;
    private int _splashSpinnerPaddingY = 64;
    private int _splashSpinnerX;
    private int _splashSpinnerY;

    /// <summary>Gets or sets the spinner placement mode index
    /// (matches the <see cref="SplashPlacementMode"/> enum order).</summary>
    public int SplashSpinnerPlacementModeIndex { get => _splashSpinnerPlacementModeIndex; set { _splashSpinnerPlacementModeIndex = value; Raise(nameof(SplashSpinnerPlacementModeIndex)); Raise(nameof(SplashSpinnerPlacementIsAnchor)); Raise(nameof(SplashSpinnerPlacementIsAbsolute)); } }

    /// <summary>Gets whether the spinner placement editor shows the anchor + padding fields.</summary>
    public bool SplashSpinnerPlacementIsAnchor => _splashSpinnerPlacementModeIndex == (int)SplashPlacementMode.Anchor;

    /// <summary>Gets whether the spinner placement editor shows the absolute X/Y fields.</summary>
    public bool SplashSpinnerPlacementIsAbsolute => _splashSpinnerPlacementModeIndex == (int)SplashPlacementMode.Absolute;

    /// <summary>Gets or sets the spinner anchor index
    /// (matches the <see cref="SplashPlacementAnchor"/> enum order).</summary>
    public int SplashSpinnerAnchorIndex { get => _splashSpinnerAnchorIndex; set { _splashSpinnerAnchorIndex = value; Raise(nameof(SplashSpinnerAnchorIndex)); } }

    /// <summary>Gets or sets the spinner horizontal padding from the anchored edge.</summary>
    public int SplashSpinnerPaddingX { get => _splashSpinnerPaddingX; set { _splashSpinnerPaddingX = value; Raise(nameof(SplashSpinnerPaddingX)); } }

    /// <summary>Gets or sets the spinner vertical padding from the anchored edge.</summary>
    public int SplashSpinnerPaddingY { get => _splashSpinnerPaddingY; set { _splashSpinnerPaddingY = value; Raise(nameof(SplashSpinnerPaddingY)); } }

    /// <summary>Gets or sets the spinner absolute X coordinate in logical pixels.</summary>
    public int SplashSpinnerX { get => _splashSpinnerX; set { _splashSpinnerX = value; Raise(nameof(SplashSpinnerX)); } }

    /// <summary>Gets or sets the spinner absolute Y coordinate in logical pixels.</summary>
    public int SplashSpinnerY { get => _splashSpinnerY; set { _splashSpinnerY = value; Raise(nameof(SplashSpinnerY)); } }

    // Logo placement
    private int _splashLogoPlacementModeIndex = (int)SplashPlacementMode.WithText;
    private int _splashLogoAnchorIndex = (int)SplashPlacementAnchor.Center;
    private int _splashLogoPaddingX = 64;
    private int _splashLogoPaddingY = 64;
    private int _splashLogoX;
    private int _splashLogoY;

    /// <summary>Gets or sets the logo placement mode index
    /// (matches the <see cref="SplashPlacementMode"/> enum order).</summary>
    public int SplashLogoPlacementModeIndex { get => _splashLogoPlacementModeIndex; set { _splashLogoPlacementModeIndex = value; Raise(nameof(SplashLogoPlacementModeIndex)); Raise(nameof(SplashLogoPlacementIsAnchor)); Raise(nameof(SplashLogoPlacementIsAbsolute)); } }

    /// <summary>Gets whether the logo placement editor shows the anchor + padding fields.</summary>
    public bool SplashLogoPlacementIsAnchor => _splashLogoPlacementModeIndex == (int)SplashPlacementMode.Anchor;

    /// <summary>Gets whether the logo placement editor shows the absolute X/Y fields.</summary>
    public bool SplashLogoPlacementIsAbsolute => _splashLogoPlacementModeIndex == (int)SplashPlacementMode.Absolute;

    /// <summary>Gets or sets the logo anchor index
    /// (matches the <see cref="SplashPlacementAnchor"/> enum order).</summary>
    public int SplashLogoAnchorIndex { get => _splashLogoAnchorIndex; set { _splashLogoAnchorIndex = value; Raise(nameof(SplashLogoAnchorIndex)); } }

    /// <summary>Gets or sets the logo horizontal padding from the anchored edge.</summary>
    public int SplashLogoPaddingX { get => _splashLogoPaddingX; set { _splashLogoPaddingX = value; Raise(nameof(SplashLogoPaddingX)); } }

    /// <summary>Gets or sets the logo vertical padding from the anchored edge.</summary>
    public int SplashLogoPaddingY { get => _splashLogoPaddingY; set { _splashLogoPaddingY = value; Raise(nameof(SplashLogoPaddingY)); } }

    /// <summary>Gets or sets the logo absolute X coordinate in logical pixels.</summary>
    public int SplashLogoX { get => _splashLogoX; set { _splashLogoX = value; Raise(nameof(SplashLogoX)); } }

    /// <summary>Gets or sets the logo absolute Y coordinate in logical pixels.</summary>
    public int SplashLogoY { get => _splashLogoY; set { _splashLogoY = value; Raise(nameof(SplashLogoY)); } }

    /// <summary>Builds the splash section from the UI-owned fields — the single
    /// source of truth used by Save, the preview window, and theme export.
    /// Enum-backed indices are clamped into their enum ranges here.</summary>
    internal SplashConfig BuildSplashConfig() => new()
    {
        Text = SplashText,
        TextEnabled = SplashTextEnabled,
        TextColor = SplashTextColorHex,
        TitleFontSize = SplashTitleFontSize,
        Caption = SplashCaption,
        CaptionColor = SplashCaptionColorHex,
        CaptionFontSize = SplashCaptionFontSize,
        SpinnerStyle = (SplashSpinnerStyle)Math.Clamp(SplashSpinnerStyleIndex, 0, (int)SplashSpinnerStyle.Off),
        SpinnerColor = SplashSpinnerColorHex,
        SpinnerSize = SplashSpinnerSize,
        SweepEdge = (SweepEdge)Math.Clamp(SplashSweepEdgeIndex, 0, (int)SweepEdge.Top),
        BackgroundColor = SplashBackgroundColorHex,
        VignetteEnabled = SplashVignetteEnabled,
        BackgroundImagePath = SplashBackgroundImagePath,
        LogoImagePath = SplashLogoPath,
        LogoMaxSize = SplashLogoMaxSize,
        TextPlacement = BuildPlacement(
            SplashTextPlacementModeIndex, SplashTextAnchorIndex,
            SplashTextPaddingX, SplashTextPaddingY, SplashTextX, SplashTextY,
            allowWithText: false),
        SpinnerPlacement = BuildPlacement(
            SplashSpinnerPlacementModeIndex, SplashSpinnerAnchorIndex,
            SplashSpinnerPaddingX, SplashSpinnerPaddingY, SplashSpinnerX, SplashSpinnerY),
        LogoPlacement = BuildPlacement(
            SplashLogoPlacementModeIndex, SplashLogoAnchorIndex,
            SplashLogoPaddingX, SplashLogoPaddingY, SplashLogoX, SplashLogoY),
    };

    private static SplashElementPlacement BuildPlacement(
        int modeIndex, int anchorIndex, int paddingX, int paddingY, int x, int y,
        bool allowWithText = true)
    {
        var mode = (SplashPlacementMode)Math.Clamp(modeIndex, 0, (int)SplashPlacementMode.WithText);
        if (!allowWithText && mode == SplashPlacementMode.WithText)
        {
            // "With text" is a spinner/logo-only mode; the text element itself anchors.
            mode = SplashPlacementMode.Anchor;
        }

        return new()
        {
            Mode = mode,
            Anchor = (SplashPlacementAnchor)Math.Clamp(anchorIndex, 0, (int)SplashPlacementAnchor.BottomRight),
            PaddingX = paddingX,
            PaddingY = paddingY,
            X = x,
            Y = y,
        };
    }

    /// <summary>Loads the UI-owned splash fields from a splash section — used at
    /// startup, on preset apply, and after theme import.</summary>
    internal void LoadSplash(SplashConfig splash)
    {
        SplashText = splash.Text;
        SplashTextEnabled = splash.TextEnabled;
        SplashTextColorHex = splash.TextColor;
        SplashTitleFontSize = splash.TitleFontSize;
        SplashCaption = splash.Caption;
        SplashCaptionColorHex = splash.CaptionColor;
        SplashCaptionFontSize = splash.CaptionFontSize;
        SplashSpinnerStyleIndex = (int)splash.SpinnerStyle;
        SplashSpinnerColorHex = splash.SpinnerColor;
        SplashSpinnerSize = splash.SpinnerSize;
        SplashSweepEdgeIndex = (int)splash.SweepEdge;
        SplashBackgroundColorHex = splash.BackgroundColor;
        SplashVignetteEnabled = splash.VignetteEnabled;
        SplashBackgroundImagePath = splash.BackgroundImagePath;
        SplashLogoPath = splash.LogoImagePath;
        SplashLogoMaxSize = splash.LogoMaxSize;

        SplashTextPlacementModeIndex = (int)splash.TextPlacement.Mode;
        SplashTextAnchorIndex = (int)splash.TextPlacement.Anchor;
        SplashTextPaddingX = splash.TextPlacement.PaddingX;
        SplashTextPaddingY = splash.TextPlacement.PaddingY;
        SplashTextX = splash.TextPlacement.X;
        SplashTextY = splash.TextPlacement.Y;

        SplashSpinnerPlacementModeIndex = (int)splash.SpinnerPlacement.Mode;
        SplashSpinnerAnchorIndex = (int)splash.SpinnerPlacement.Anchor;
        SplashSpinnerPaddingX = splash.SpinnerPlacement.PaddingX;
        SplashSpinnerPaddingY = splash.SpinnerPlacement.PaddingY;
        SplashSpinnerX = splash.SpinnerPlacement.X;
        SplashSpinnerY = splash.SpinnerPlacement.Y;

        SplashLogoPlacementModeIndex = (int)splash.LogoPlacement.Mode;
        SplashLogoAnchorIndex = (int)splash.LogoPlacement.Anchor;
        SplashLogoPaddingX = splash.LogoPlacement.PaddingX;
        SplashLogoPaddingY = splash.LogoPlacement.PaddingY;
        SplashLogoX = splash.LogoPlacement.X;
        SplashLogoY = splash.LogoPlacement.Y;
    }

    // --- Save ---
    private void ApplyTo(AppConfig config) => ApplyTo(config, BuildSplashConfig());

    /// <summary>Applies the UI-owned fields over <paramref name="config"/>, taking the
    /// splash section from <paramref name="splash"/> instead of rebuilding it — the save
    /// path prepares (and thereby path-rewrites) its splash section BEFORE it takes the
    /// config lock, and rebuilding here would throw that rewrite away.</summary>
    private void ApplyTo(AppConfig config, SplashConfig splash)
    {
        config.SteamAutoRelaunch = SteamAutoRelaunch;
        config.SteamGridDbApiKey = (SteamGridDbApiKey ?? "").Trim();
        config.StartupDelayMs = StartupDelayMs;
        config.StaggerDelayMs = StaggerDelayMs;
        config.BootSplashEnabled = BootSplashEnabled;
        config.GameModeBootEnabled = GameModeBootEnabled;
        config.SteamInputLeaseEnabled = SteamInputLeaseEnabled;
        config.Cef.Enabled = CefEnabled;
        config.Cef.LibraryTabs = CefLibraryTabs;
        config.Cef.CardManager = CefCardManager;
        config.Cef.SdFormat = CefSdFormat;
        config.Cef.Artwork = CefArtwork;
        config.Cef.WifiIndicator = CefWifiIndicator;
        config.Hotkey = _hotkey;
        config.GamepadChord = _chord;
        config.Gestures.BottomEdge = GestureBottom;
        config.Gestures.RightEdge = GestureRight;
        config.Gestures.LeftEdgeSteamMenu = GestureLeftSteamMenu;
        config.Gestures.TopEdgeSteamQuickAccess = GestureTopSteamQuickAccess;
        config.Gestures.BottomEdgeAction = (EdgeAction)Math.Clamp(BottomEdgeActionIndex, 0, 1);
        config.GlyphStyle = (GlyphStyle)Math.Clamp(GlyphStyleIndex, 0, 2);
        config.AccentColor = AccentColorHex;
        config.Splash = splash;
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
        // Copy the picked splash images into the stable per-user splash directory
        // FIRST, and deliberately OUTSIDE the cross-process config lock. Two-phase on
        // purpose: the copies are staged as uniquely named sidecars and only replace
        // the live files after the config write succeeded — a failed save must never
        // leave the still-persisted OLD config pointing at already-replaced images.
        //
        // Why outside the lock: a picked or imported image can be tens of megabytes,
        // while ConfigStore's mutex timeout is 2 s, sized for one small JSON write.
        // Holding the lock across the copy would time every other WSGM process out
        // (the shell's config FileSystemWatcher → Load, the elevated one-shots) and
        // print "Config mutex timed out — proceeding without cross-process lock" on
        // the primary remote-diagnosis surface, which is both log noise and real
        // unserialized access. Staging is safe unlocked because it touches no live
        // file and every sidecar name carries its own GUID (see SplashAssets), so two
        // concurrent savers can no longer collide while staging.
        var splash = BuildSplashConfig();
        using var splashAssets = SplashAssets.Prepare(splash);

        AppConfig config;
        IReadOnlyList<string> failedSlots;
        string? failure;
        // The lock now covers exactly four fast operations, and nothing else:
        //   Load → Save → Commit → (repair Save) → boot-manifest write.
        // That is sufficient because
        //   (a) Load..Save is the read-modify-write this merge exists for — another
        //       process must not persist between our read and our write;
        //   (b) Save and Commit stay in ONE scope, so a concurrent saver can never
        //       interleave between the config write and the image promotion it
        //       describes: whoever holds the lock last leaves config.json and the
        //       live images agreeing (the round-3 invariant);
        //   (c) boot.json is a projection of the config we just persisted, so it is
        //       written before another saver can change config.json underneath it.
        // Load/Save re-acquire the same named mutex inside this scope; a Win32 mutex
        // is owned per thread with a recursion count, so those nested acquisitions
        // balance their own releases and the outer hold survives (see AcquireLock).
        using (ConfigStore.AcquireLock())
        {
            config = ConfigStore.Load();
            // Captured BEFORE ApplyTo overwrites them: if a staged copy cannot be
            // promoted the persisted config has to go back to the path whose file is
            // actually there.
            var previousLogoPath = config.Splash.LogoImagePath;
            var previousBackgroundPath = config.Splash.BackgroundImagePath;
            ApplyTo(config, splash);
            // Any throw from here to Commit leaves the transaction uncommitted, and the
            // enclosing `using` rolls it back: the live splash assets stay untouched.
            ConfigStore.Save(config);
            failedSlots = splashAssets.Commit();
            // A slot that could not be promoted (locked file, AV hold, permissions)
            // leaves the just-persisted path pointing at an image that was never
            // written; a slot whose STAGING already failed leaves it pointing at the
            // user's volatile pick (Downloads, a removable drive) instead of a copy
            // WSGM owns. Commit reports both: repair the persisted state, then fail
            // the save — a save that did neither must never log "Settings saved."
            // (A staging failure is therefore written once and immediately corrected,
            // both inside this lock, rather than getting its own earlier repair pass:
            // one reported-failure path is worth more than one avoided write.)
            failure = RestoreSlotsThatFailedToPromote(
                config, failedSlots, previousLogoPath, previousBackgroundPath, ConfigStore.Save);
            // Keep the logon service's view in sync — every save may change the
            // enabled flag or the elevation inputs (elevated startup apps).
            BootManifestWriter.WriteCurrent(config);
        }

        AdoptMaterializedPaths(config.Splash, failedSlots);
        // Re-color the running UI live; Application.Current is null in unit tests.
        if (Application.Current is { } app)
        {
            AccentPalette.Apply(app, AccentPalette.Parse(config.AccentColor));
        }
        if (failure is not null)
        {
            // Everything else was persisted and applied — but the save did not do what
            // it said, so SaveCommand must report "Save failed", never "Saved".
            throw new System.IO.IOException(failure);
        }
        Log.Info("Settings saved.");
        return config;
    }

    private static bool Failed(IReadOnlyList<string> failedSlots, string slot) =>
        failedSlots.Contains(slot, StringComparer.OrdinalIgnoreCase);

    /// <summary>Syncs the editor back to the materialized copies — only once they ARE
    /// the live files: keeping the originally picked paths would re-copy on every save
    /// and, if the source vanished, clobber the stable copy's path with a dead one on
    /// the next save.
    /// <para>A FAILED slot is skipped on purpose. Whether the sidecar could not be
    /// staged (unreadable source, uncreatable target) or not promoted (locked live
    /// file), config.json keeps the conservative PREVIOUS path while the view model
    /// keeps the user's PICK, so pressing Save again after fixing the file actually
    /// retries that image instead of silently re-saving the old one.</para></summary>
    /// <param name="persisted">The splash section as it was just persisted (its paths
    /// are the materialized ones for every slot that went live).</param>
    /// <param name="failedSlots">The slot names reported by the splash-asset commit.</param>
    internal void AdoptMaterializedPaths(SplashConfig persisted, IReadOnlyList<string> failedSlots)
    {
        if (!Failed(failedSlots, SplashAssets.LogoSlot))
        {
            SplashLogoPath = persisted.LogoImagePath;
        }
        if (!Failed(failedSlots, SplashAssets.BackgroundSlot))
        {
            SplashBackgroundImagePath = persisted.BackgroundImagePath;
        }
    }

    /// <summary>Puts the previously persisted image path back for every slot that did
    /// not end up as a live copy — staging failed, or the staged copy could not be
    /// promoted — so the persisted state always names an image WSGM owns and that
    /// exists. Pure: it only mutates <paramref name="config"/> and builds the
    /// message — the caller performs the write (see
    /// <see cref="RestoreSlotsThatFailedToPromote"/>), so this step is testable without
    /// going anywhere near the real per-user config file.</summary>
    /// <param name="config">The just-saved configuration, repaired in place.</param>
    /// <param name="failedSlots">The slot names reported by the splash-asset commit.</param>
    /// <param name="previousLogoPath">The logo path persisted before this save.</param>
    /// <param name="previousBackgroundPath">The background path persisted before this save.</param>
    /// <returns>The message to fail the save with, or null when every slot committed.</returns>
    internal static string? RepairSlotsThatFailedToPromote(
        AppConfig config,
        IReadOnlyList<string> failedSlots,
        string previousLogoPath,
        string previousBackgroundPath)
    {
        if (failedSlots.Count == 0)
        {
            return null;
        }

        foreach (var slot in failedSlots)
        {
            if (string.Equals(slot, SplashAssets.LogoSlot, StringComparison.OrdinalIgnoreCase))
            {
                Log.Error(
                    $"Splash logo image could not be updated — keeping the previously saved '{previousLogoPath}'.");
                config.Splash.LogoImagePath = previousLogoPath;
            }
            else if (string.Equals(slot, SplashAssets.BackgroundSlot, StringComparison.OrdinalIgnoreCase))
            {
                Log.Error(
                    $"Splash background image could not be updated — keeping the previously saved '{previousBackgroundPath}'.");
                config.Splash.BackgroundImagePath = previousBackgroundPath;
            }
        }
        // One message for both halves of the transaction (see SplashAssets.Commit):
        // the copy into WSGM's splash folder failed, or the finished copy could not
        // replace the live file. The user's action is the same either way.
        return $"splash image not updated ({string.Join(", ", failedSlots)}) — "
            + "the picked image could not be copied into WSGM's splash folder, or the live file "
            + "is in use or not writable. The previous image is still configured, and your pick "
            + "is kept: fix the file and press Save again to retry.";
    }

    /// <summary>Repairs the config for every slot whose staged copy could not be
    /// promoted and re-persists it through <paramref name="save"/>.</summary>
    /// <param name="config">The just-saved configuration, repaired in place.</param>
    /// <param name="failedSlots">The slot names reported by the splash-asset commit.</param>
    /// <param name="previousLogoPath">The logo path persisted before this save.</param>
    /// <param name="previousBackgroundPath">The background path persisted before this save.</param>
    /// <param name="save">Writes the repaired configuration (ConfigStore.Save in production).</param>
    /// <returns>The message to fail the save with, or null when every slot committed.
    /// A failing repair write does NOT replace it: the promotion failure is the cause
    /// the user has to act on, and letting the secondary write's exception escape would
    /// mask it — so that one is logged instead.</returns>
    internal static string? RestoreSlotsThatFailedToPromote(
        AppConfig config,
        IReadOnlyList<string> failedSlots,
        string previousLogoPath,
        string previousBackgroundPath,
        Action<AppConfig> save)
    {
        var failure = RepairSlotsThatFailedToPromote(
            config, failedSlots, previousLogoPath, previousBackgroundPath);
        if (failure is null)
        {
            return null;
        }

        try
        {
            // Still inside the caller's config lock.
            save(config);
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't re-save the config after a failed splash image promotion", ex);
        }
        return failure;
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
