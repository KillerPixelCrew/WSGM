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
    // Label, description and parent for each feature WSGM exports. An unknown feature still appears,
    // under its own key, so a newer WSGM never has a switch setup cannot show.
    private static readonly Dictionary<string, (string Label, string Description, string? Parent, string Group)> Known = new()
    {
        ["steamInputManagement"] = ("Manage Steam Input",
            "WSGM hands controllers to Steam in games and takes them back for its own menus.", null, "Steam Input"),
        ["steamInputLease"] = ("Steam Input lease for WSGM's menus",
            "Keeps Steam from turning the controller into a mouse while WSGM is in front.", null, "Steam Input"),
        ["steamUi"] = ("Steam UI integration", "WSGM adds its own rows and tabs inside Steam's Big Picture.", null,
            "Steam UI integration"),
        ["libraryTabs"] = ("Library tabs per SD card", "", "steamUi", "Steam UI integration"),
        ["cardManager"] = ("Card manager", "", "steamUi", "Steam UI integration"),
        ["connectedLibraryCarousel"] = ("Connected-library carousel on Home", "", "steamUi", "Steam UI integration"),
        ["sdFormat"] = ("Format SD cards from Steam", "", "steamUi", "Steam UI integration"),
        ["wifiIndicator"] = ("Wi-Fi indicator in the header", "", "steamUi", "Steam UI integration"),
        ["nativeQuickAccess"] = ("WSGM rows in Quick Access", "", "steamUi", "Steam UI integration"),
        ["downloadKeepAwake"] = ("Stay awake while downloading", "", "steamUi", "Steam UI integration"),
        ["downloadQueueSort"] = ("Download queue sorting", "", "steamUi", "Steam UI integration"),
        ["edgeGestures"] = ("Edge swipes", "Swipe in from the top edge to open the overlay.", null, "Getting to WSGM"),
        ["hotkey"] = ("Keyboard shortcut", "", null, "Getting to WSGM"),
        ["gamepadChord"] = ("Gamepad chord", "", null, "Getting to WSGM"),
        ["bootSplash"] = ("Boot splash", "Covers the desktop while Game Mode starts.", null, "Start")
    };

    private readonly JsonObject _presets;
    private bool _customize;
    private bool _desktopFirst;
    private bool _signIn;
    private bool _takeover;

    public ProfilePage(JsonObject answers)
    {
        _presets = answers["presets"] as JsonObject ?? new JsonObject();
        FromCurrent = answers["freshInstall"]?.GetValue<bool>() != true;
        _signIn = answers["startAtSignIn"]?.GetValue<bool>() ?? true;
        _desktopFirst = answers["startMode"]?.ToString() == "Desktop";
        _takeover = answers["steamAutostartTakeover"]?.GetValue<bool>() ?? false;
        TakeoverEntries = answers["steamAutostartEntries"] is JsonArray entries
            ? string.Join("\n", entries.Select(entry => "• " + entry))
            : "";
        var features = answers["features"] as JsonObject ?? new JsonObject();
        if (!FromCurrent && _presets["full"] is JsonObject full)
        {
            // A fresh install starts from the Full preset, as the mockup shows.
            features = full["features"]?.DeepClone() as JsonObject ?? features;
            _takeover = full["steamAutostartTakeover"]?.GetValue<bool>() ?? _takeover;
        }

        foreach (var (key, value) in features)
        {
            var (label, description, parent, group) = Known.TryGetValue(key, out var known)
                ? known
                : (key, "", null, "Other");
            FeatureOption option = new(key, label, description, value?.GetValue<bool>() == true, parent) { Group = group };
            option.PropertyChanged += (_, _) => Changed();
            Features.Add(option);
        }

        UpdateEnabled();
    }

    public bool FromCurrent { get; }
    public ObservableCollection<FeatureOption> Features { get; } = [];
    public string TakeoverEntries { get; }
    public bool HasTakeoverEntries => TakeoverEntries.Length > 0;
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
                Changed();
            }
        }
    }

    public bool Customize
    {
        get => _customize;
        set => Set(ref _customize, value);
    }

    /// <summary>Writes the page's choices into the answers document.</summary>
    public void WriteTo(JsonObject answers)
    {
        answers["startAtSignIn"] = SignIn;
        answers["startMode"] = _desktopFirst ? "Desktop" : "Game";
        answers["steamAutostartTakeover"] = Takeover;
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
                                                    && (preset["steamAutostartTakeover"]?.GetValue<bool>() ?? false) == Takeover)
            {
                return name;
            }
        }

        return null;
    }

    private void ApplyPreset(string name)
    {
        if (_presets[name] is not JsonObject preset || preset["features"] is not JsonObject features)
        {
            return;
        }

        foreach (var feature in Features)
        {
            feature.On = features[feature.Key]?.GetValue<bool>() == true;
        }

        _takeover = preset["steamAutostartTakeover"]?.GetValue<bool>() ?? false;
        Raise(nameof(Takeover));
        Changed();
    }

    private void Changed()
    {
        UpdateEnabled();
        Raise(nameof(IsFull));
        Raise(nameof(IsMinimal));
        Raise(nameof(Chip));
    }

    private void UpdateEnabled()
    {
        foreach (var feature in Features.Where(feature => feature.Parent is not null))
        {
            feature.Enabled = Features.FirstOrDefault(parent => parent.Key == feature.Parent)?.On != false;
        }
    }
}
