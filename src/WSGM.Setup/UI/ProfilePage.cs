using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json.Nodes;

namespace WSGM.Setup.UI;

/// <summary>
///     How WSGM runs: Full or Minimal, Steam or Desktop first, sign-in, and the Customize panel. It is built
///     from the answers WSGM exported, so setup knows the feature list and the presets only as data.
/// </summary>
internal sealed class ProfilePage : Page
{
    // Label, description, parent, group and whether WSGM 1.0 lacked it, for each feature WSGM exports. The
    // NEW flags compare with WSGM 1.0.0 (the tree before db0b0527), which had the Steam Input lease, the
    // shortcut, the gamepad chord, edge gestures and the boot splash. An unknown feature still appears,
    // under its own key, so a newer WSGM never has a switch setup cannot show.
    private static readonly Dictionary<string, (string Label, string Description, string? Parent, string Group, bool
        IsNew)> Known = new()
    {
        ["steamInputManagement"] = ("Manage Steam Input",
            "WSGM places its Steam Input helper in Steam's own folder, so Steam loads it and WSGM never writes into "
            + "Steam while it runs.", null, "Steam Input", true),
        ["steamInputLease"] = ("Pause Steam Input for WSGM's menus",
            "Keeps Steam from turning the controller into a mouse or keyboard while a WSGM menu is open.", null,
            "Steam Input", false),
        ["steamUi"] = ("Steam UI integration",
            "Adds WSGM's features inside Steam's Big Picture. Turning it off hides everything below.", null,
            "Steam UI integration", true),
        ["libraryTabs"] = ("Library tabs",
            "Your own filter tabs in the library, with the tab order you choose and Steam's tabs you don't need hidden.",
            "steamUi", "Steam UI integration", true),
        ["cardManager"] = ("SD card libraries",
            "A library tab for each SD card, a badge on each game showing which card it is on, and labels that "
            + "follow the card you insert.", "steamUi", "Steam UI integration", true),
        ["connectedLibraryCarousel"] = ("Home shows what's installed",
            "Home's carousel shows the games on the drives and cards attached right now, most recently played first.",
            "steamUi", "Steam UI integration", true),
        ["sdFormat"] = ("Format SD cards from Steam",
            "Formats a new card and adds its library to Steam without leaving Big Picture.", "steamUi",
            "Steam UI integration", true),
        ["wifiIndicator"] = ("Wi-Fi in the header", "Shows the Wi-Fi signal in Big Picture's header.", "steamUi",
            "Steam UI integration", true),
        ["nativeQuickAccess"] = ("WSGM in Quick Access", "Adds WSGM's controls to Steam's Quick Access menu.",
            "steamUi", "Steam UI integration", true),
        ["downloadKeepAwake"] = ("Stay awake while downloading",
            "Keeps the device out of standby while Steam is downloading, so downloads finish.", "steamUi",
            "Steam UI integration", true),
        ["downloadQueueSort"] = ("Sort the download queue",
            "Adds name, size and type sorting to Big Picture's download queue.", "steamUi", "Steam UI integration",
            true),
        ["edgeGestures"] = ("Edge swipes",
            "Swipe in from the top edge for WSGM, from the left for Steam's menu and from the right for Quick Access.",
            null, "Getting to WSGM", false),
        ["hotkey"] = ("Keyboard shortcut", "Opens WSGM with Ctrl+Alt+Home. You can change the keys in Settings.",
            null, "Getting to WSGM", false),
        ["gamepadChord"] = ("Controller button combination",
            "Opens WSGM with a button combination you set in Settings.", null, "Getting to WSGM", false),
        ["bootSplash"] = ("Boot splash", "Covers the desktop while Game Mode starts.", null, "Start", false)
    };

    private readonly JsonObject _presets;
    private bool _applying;
    private bool _edited;
    private bool _desktopFirst;
    private bool _signIn;
    private bool _takeover;
    private bool _managersTakeover;

    public ProfilePage(JsonObject answers)
    {
        _presets = answers["presets"] as JsonObject ?? new JsonObject();
        FromCurrent = answers["freshInstall"]?.GetValue<bool>() != true;
        _signIn = answers["startAtSignIn"]?.GetValue<bool>() ?? true;
        _desktopFirst = answers["startMode"]?.ToString() == "Desktop";
        _takeover = answers["steamAutostartTakeover"]?.GetValue<bool>() ?? false;
        _managersTakeover = answers["otherManagersTakeover"]?.GetValue<bool>() ?? false;
        OtherManagers = answers["otherManagers"] is JsonArray managers
            ? [.. managers.Select(manager => manager?.ToString() ?? "").Where(line => line.Length > 0)]
            : [];
        TakeoverEntries = answers["steamAutostartEntries"] is JsonArray entries
            ? string.Join("\n", entries.Select(entry => "• " + entry))
            : "";
        var features = answers["features"] as JsonObject ?? new JsonObject();
        if (!FromCurrent && _presets["full"] is JsonObject full)
        {
            // A fresh install starts from the Full preset, as the mockup shows.
            features = full["features"]?.DeepClone() as JsonObject ?? features;
            _takeover = full["steamAutostartTakeover"]?.GetValue<bool>() ?? _takeover;
            _managersTakeover = full["otherManagersTakeover"]?.GetValue<bool>() ?? _managersTakeover;
        }

        foreach (var (key, value) in features)
        {
            var (label, description, parent, group, isNew) = Known.TryGetValue(key, out var known)
                ? known
                : (key, "", null, "Other", false);
            FeatureOption option = new(key, label, description, value?.GetValue<bool>() == true, parent)
                { Group = group, IsNew = isNew };
            option.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(FeatureOption.On) && !_applying)
                {
                    _edited = true;
                }

                Changed();
            };
            Features.Add(option);
        }

        UpdateEnabled();
        foreach (var group in Features.GroupBy(feature => feature.Group))
        {
            Groups.Add(new FeatureGroup(group.Key.ToUpperInvariant(), [.. group]));
        }
    }

    /// <summary>The features by group, as the Customize page lists them.</summary>
    public ObservableCollection<FeatureGroup> Groups { get; } = [];

    public bool FromCurrent { get; }
    public ObservableCollection<FeatureOption> Features { get; } = [];
    public string TakeoverEntries { get; }
    public bool HasTakeoverEntries => TakeoverEntries.Length > 0;

    /// <summary>Handheld Companion and the maker's apps found here, one line each.</summary>
    public IReadOnlyList<string> OtherManagers { get; }

    /// <summary>Whether any other handheld manager was found.</summary>
    public bool HasOtherManagers => OtherManagers.Count > 0;

    /// <summary>The managers as a bulleted list, for the Customize page.</summary>
    public string OtherManagerLines => string.Join("\n", OtherManagers.Select(line => "• " + line));

    /// <summary>
    ///     The Profile page's note while other managers would be turned off: which ones, and that uninstalling
    ///     puts them back.
    /// </summary>
    public string OtherManagersNote => HasOtherManagers && _managersTakeover
        ? "This also turns off how these start, so WSGM is the one manager of your device: "
          + string.Join(", ", OtherManagers.Select(line => line.Split(':')[0]))
          + ". Their windows are asked to close. Uninstalling WSGM turns them back on."
        : "";

    /// <summary>Whether the note is shown.</summary>
    public bool HasOtherManagersNote => OtherManagersNote.Length > 0;

    /// <summary>Turn off other handheld managers' autostart and services.</summary>
    public bool OtherManagersTakeover
    {
        get => _managersTakeover;
        set
        {
            if (Set(ref _managersTakeover, value))
            {
                _edited = true;
                Changed();
            }
        }
    }
    public override string Eyebrow => "Profile";
    public override string Title => "How should WSGM run?";
    public override string Lead => FromCurrent ? "These are your current settings. Change anything you like." : "";

    /// <summary>"Current", "Customized" or empty, for the chip beside Integration.</summary>
    public string Chip => Level() is null ? "Customized" : FromCurrent ? "Current" : "";

    public bool IsFull
    {
        get => Level() == "full";
        set
        {
            if (value)
            {
                _edited = true;
                ApplyPreset("full");
            }
        }
    }

    public bool IsMinimal
    {
        get => Level() == "minimal";
        set
        {
            if (value)
            {
                _edited = true;
                ApplyPreset("minimal");
            }
        }
    }

    public bool SteamFirst
    {
        get => !_desktopFirst;
        set
        {
            if (value && Set(ref _desktopFirst, false))
            {
                Raise(nameof(DesktopFirst));
            }
        }
    }

    public bool DesktopFirst
    {
        get => _desktopFirst;
        set
        {
            if (value && Set(ref _desktopFirst, true))
            {
                Raise(nameof(SteamFirst));
            }
        }
    }

    public bool SignIn
    {
        get => _signIn;
        set => Set(ref _signIn, value);
    }

    public bool Takeover
    {
        get => _takeover;
        set
        {
            if (Set(ref _takeover, value))
            {
                _edited = true;
                Changed();
            }
        }
    }

    /// <summary>Writes the page's choices into the answers document.</summary>
    public void WriteTo(JsonObject answers)
    {
        answers["startAtSignIn"] = SignIn;
        answers["startMode"] = _desktopFirst ? "Desktop" : "Game";
        answers["steamAutostartTakeover"] = Takeover;
        answers["otherManagersTakeover"] = _managersTakeover;
        JsonObject features = new();
        foreach (var feature in Features)
        {
            features[feature.Key] = feature.On;
        }

        answers["features"] = features;
    }

    private string? Level()
    {
        foreach (var name in new[] { "full", "minimal" })
        {
            if (_presets[name] is JsonObject preset && preset["features"] is JsonObject features
                                                    && Features.All(feature =>
                                                        features[feature.Key]?.GetValue<bool>() == feature.On)
                                                    && (preset["steamAutostartTakeover"]?.GetValue<bool>() ?? false) ==
                                                    Takeover
                                                    && (!HasOtherManagers
                                                        || (preset["otherManagersTakeover"]?.GetValue<bool>() ?? false)
                                                        == _managersTakeover))
            {
                return name;
            }
        }

        return null;
    }

    /// <summary>
    ///     On a fresh install, starts the level from the hardware choice: Full with the device plugin, Minimal
    ///     without it. It follows that choice until the user picks a level or changes a switch; an update or
    ///     repair always keeps the current settings.
    /// </summary>
    public void UseDefaultLevel(bool withDevicePlugin)
    {
        if (!FromCurrent && !_edited)
        {
            ApplyPreset(withDevicePlugin ? "full" : "minimal");
        }
    }

    private void ApplyPreset(string name)
    {
        if (_presets[name] is not JsonObject preset || preset["features"] is not JsonObject features)
        {
            return;
        }

        _applying = true;
        foreach (var feature in Features)
        {
            feature.On = features[feature.Key]?.GetValue<bool>() == true;
        }

        _applying = false;

        _takeover = preset["steamAutostartTakeover"]?.GetValue<bool>() ?? false;
        _managersTakeover = preset["otherManagersTakeover"]?.GetValue<bool>() ?? false;
        Raise(nameof(Takeover));
        Raise(nameof(OtherManagersTakeover));
        Changed();
    }

    private void Changed()
    {
        UpdateEnabled();
        Raise(nameof(IsFull));
        Raise(nameof(IsMinimal));
        Raise(nameof(Chip));
        Raise(nameof(OtherManagersNote));
        Raise(nameof(HasOtherManagersNote));
    }

    private void UpdateEnabled()
    {
        foreach (var feature in Features.Where(feature => feature.Parent is not null))
        {
            feature.Enabled = Features.FirstOrDefault(parent => parent.Key == feature.Parent)?.On != false;
        }
    }
}

/// <summary>
///     Every integration switch on its own page, grouped, each with what it does and a NEW badge where WSGM 1.0
///     did not have it. It edits the profile page's state, so the level chip there follows.
/// </summary>
internal sealed class CustomizePage(ProfilePage profile) : Page
{
    public ProfilePage Profile { get; } = profile;
    public override string Eyebrow => "Profile";
    public override string Title => "Customize WSGM";

    public override string Lead =>
        "Turn each part on or off. NEW marks what WSGM 1.0 didn't have. You can change all of it later in WSGM Settings.";
}

/// <summary>One group of the Customize page.</summary>
internal sealed record FeatureGroup(string Name, IReadOnlyList<FeatureOption> Items);
