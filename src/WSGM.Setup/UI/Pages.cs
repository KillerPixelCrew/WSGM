using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using WSGM.Install;
using WSGM.Setup.Engine;

namespace WSGM.Setup.UI;

/// <summary>Property-change plumbing for the setup view models.</summary>
internal abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        Raise(name);
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>A command that runs an action.</summary>
internal sealed class Command(Action execute) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter)
    {
        return true;
    }

    public void Execute(object? parameter)
    {
        execute();
    }
}

/// <summary>What every page shows above its body and in the action row.</summary>
internal abstract class Page : Observable
{
    public abstract string Eyebrow { get; }
    public abstract string Title { get; }
    public virtual string Lead => "";
    public bool HasLead => Lead.Length > 0;

    /// <summary>The primary button's label, or empty to hide it.</summary>
    public virtual string Primary => "Continue";

    /// <summary>The back button's label, or empty to hide it.</summary>
    public virtual string Back => "Back";

    public virtual bool PrimaryIsDanger => false;
}

/// <summary>A page that only explains something: a refusal or a check in progress.</summary>
internal sealed class MessagePage(string eyebrow, string title, string lead, string primary = "Close") : Page
{
    public override string Eyebrow { get; } = eyebrow;
    public override string Title { get; } = title;
    public override string Lead { get; } = lead;
    public override string Primary { get; } = primary;
    public override string Back => "";
}

internal sealed class WelcomePage(Version version, bool steamFound) : Page
{
    public override string Eyebrow => $"WSGM {version.ToString(3)}";
    public override string Title => "Set up WSGM";

    public override string Lead =>
        "Game Mode for Windows handhelds. Setup checks your hardware, asks how you want WSGM to start, and installs everything in one go.";

    public string InstallPath => InstallLayout.Root;
    public string SteamStatus => steamFound ? "Found" : "Not found. Install Steam first.";
    public override string Primary => "Get started";
    public override string Back => "";
}

internal sealed class LegacyPage(string legacyVersion, Version version) : Page
{
    public override string Eyebrow => $"WSGM {legacyVersion} → {version.ToString(3)}";
    public override string Title => $"WSGM {legacyVersion} is removed first";

    public override string Lead =>
        $"WSGM {legacyVersion} was installed with the old installer. Setup uninstalls it before installing this version. Nothing is carried over: this version asks everything again.";

    public override string Primary => "Continue";
    public override string Back => "";
}

internal sealed class UpdatePage(IReadOnlyList<string> changes, IReadOnlyList<string> outdated, string from, string to) : Page
{
    public override string Eyebrow => $"WSGM {from} → {to}";
    public override string Title => "Update WSGM";
    public override string Lead => "Everything is updated together: WSGM itself and the plugins it ships with.";
    public IReadOnlyList<string> Changes { get; } = changes;
    public IReadOnlyList<string> Outdated { get; } = outdated;
    public bool HasOutdated => Outdated.Count > 0;
    public override string Primary => HasOutdated ? "Update anyway" : "Update";
    public override string Back => HasOutdated ? $"Stay on {from}" : "";
}

internal sealed class MaintainPage(Version version, Action repair, Action uninstall, Action close) : Page
{
    public override string Eyebrow => $"WSGM {version.ToString(3)} is installed";
    public override string Title => "What do you want to do?";
    public ICommand Repair { get; } = new Command(repair);
    public ICommand Uninstall { get; } = new Command(uninstall);
    public ICommand Close { get; } = new Command(close);
    public override string Primary => "";
    public override string Back => "";
}

/// <summary>One device plugin that matches this machine.</summary>
internal sealed class CandidateOption(PluginOffer offer) : Observable
{
    private bool _selected;
    public PluginOffer Offer { get; } = offer;
    public string Name => $"{Offer.Plugin.Name} {Offer.Plugin.Version}";

    public string Badges =>
        (Offer.Plugin.Community ? "Community" : "First-party") + " · "
                                                               + (Offer.Plugin.HardwareTested ? "Hardware-tested" : "Blind")
                                                               + " · " + (Offer.Match?.Fallback == true ? "Family match" : "Exact match");

    public bool Selected
    {
        get => _selected;
        set => Set(ref _selected, value);
    }
}

/// <summary>One common plugin setup can add.</summary>
internal sealed class CommonOption(BundledPlugin plugin, bool installed) : Observable
{
    private bool _checked = installed;
    public BundledPlugin Plugin { get; } = plugin;
    public string Name => $"{Plugin.Name} {Plugin.Version}";

    public bool Checked
    {
        get => _checked;
        set => Set(ref _checked, value);
    }
}

internal sealed class HardwarePage : Page
{
    private bool _installPlugin = true;

    public HardwarePage(string device, string identity, PluginOffers offers, Action changed)
    {
        Device = device;
        Identity = identity;
        foreach (var offer in offers.DeviceCandidates)
        {
            CandidateOption option = new(offer) { Selected = offer == (offers.RecommendedDevice ?? offers.DeviceCandidates[0]) };
            option.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(CandidateOption.Selected) && option.Selected)
                {
                    foreach (var other in Candidates.Where(candidate => candidate != option))
                    {
                        other.Selected = false;
                    }

                    Refresh();
                    changed();
                }
            };
            Candidates.Add(option);
        }

        foreach (var common in offers.Common)
        {
            Commons.Add(new CommonOption(common.Plugin, common.Installed));
        }
    }

    public string Device { get; }
    public string Identity { get; }
    public ObservableCollection<CandidateOption> Candidates { get; } = [];
    public ObservableCollection<CommonOption> Commons { get; } = [];
    public bool HasMatch => Candidates.Count > 0;
    public bool NoMatch => !HasMatch;
    public bool NeedsChoice => Candidates.Count > 1;
    public bool HasCommons => Commons.Count > 0;
    public CandidateOption? Chosen => InstallPlugin ? Candidates.FirstOrDefault(candidate => candidate.Selected) : null;

    public bool InstallPlugin
    {
        get => _installPlugin;
        set
        {
            if (Set(ref _installPlugin, value))
            {
                Refresh();
            }
        }
    }

    public override string Eyebrow => "Hardware";

    public override string Title =>
        !HasMatch ? "No hardware support for this device yet"
        : NeedsChoice ? "More than one plugin supports this device"
        : "Good news! Your hardware is supported.";

    public override string Lead =>
        !HasMatch ? "WSGM installs and works without it. Power limits, fans and the virtual controller stay off."
        : NeedsChoice ? "Only one can be installed. The exact match is usually the better choice."
        : "";

    /// <summary>The blind or community note for the chosen plugin, or empty.</summary>
    public string Caution => Chosen?.Offer.Plugin is { } plugin && (!plugin.HardwareTested || plugin.Community)
        ? (plugin.HardwareTested ? "" : "Not tested on this hardware by the WSGM team. ")
          + (plugin.Contact is { } contact ? "Report problems to its developer: " + contact : "")
        : "";

    public bool HasCaution => Caution.Length > 0;

    public IReadOnlyList<string> WillInstall =>
        Chosen is { } chosen
            ? [.. new[] { chosen.Name }.Concat(chosen.Offer.Components.Select(SetupComponents.DisplayName))]
            : [];

    private void Refresh()
    {
        Raise(nameof(Chosen));
        Raise(nameof(Caution));
        Raise(nameof(HasCaution));
        Raise(nameof(WillInstall));
    }
}

/// <summary>One line of the Customize panel.</summary>
internal sealed class FeatureOption(string key, string label, string description, bool on, string? parent) : Observable
{
    private bool _enabled = true;
    private bool _on = on;
    public string Key { get; } = key;
    public string Label { get; } = label;
    public string Description { get; } = description;
    public string? Parent { get; } = parent;
    public string Group { get; init; } = "";
    public bool HasDescription => Description.Length > 0;

    public bool On
    {
        get => _on;
        set => Set(ref _on, value);
    }

    public bool Enabled
    {
        get => _enabled;
        set => Set(ref _enabled, value);
    }
}

internal sealed class DriversPage : Page
{
    public override string Eyebrow => "Drivers";
    public override string Title => "Your controls will drop out for a moment";
    public override string Lead => "The virtual controller needs two drivers. Installing one of them restarts every USB hub.";
    public override string Primary => "Install";
}

/// <summary>One line of the progress and summary pages.</summary>
internal sealed class StepRow(SetupStep step) : Observable
{
    public SetupStep Step { get; } = step;
    public string Label => Step.State is StepState.Done or StepState.Skipped ? Step.DoneLabel : Step.Label;
    public string Note => Step.Note;
    public bool HasNote => Note.Length > 0;

    public string Glyph => Step.State switch
    {
        StepState.Done => "✓",
        StepState.Failed => "!",
        StepState.Skipped => "–",
        StepState.Running => "…",
        _ => ""
    };

    public void Update()
    {
        Raise(nameof(Label));
        Raise(nameof(Note));
        Raise(nameof(HasNote));
        Raise(nameof(Glyph));
    }
}

internal sealed class ProgressPage(bool uninstall) : Page
{
    private string _running = "";
    public ObservableCollection<StepRow> Steps { get; } = [];
    public override string Eyebrow => uninstall ? "Uninstalling" : "Installing";
    public override string Title => _running.Length > 0 ? _running + "…" : uninstall ? "Removing WSGM" : "Installing WSGM";

    public override string Lead =>
        Steps.FirstOrDefault(row => row.Step.State is StepState.Running)?.Step.Hint ?? "Keep the device on and plugged in.";

    public override string Primary => "";
    public override string Back => "";
    public double Percent => Steps.Count == 0 ? 0 : 100.0 * Steps.Count(row => row.Step.State is not (StepState.Waiting or StepState.Running)) / Steps.Count;

    public void Update()
    {
        foreach (var row in Steps)
        {
            row.Update();
        }

        _running = Steps.FirstOrDefault(row => row.Step.State is StepState.Running)?.Step.Label ?? "";
        Raise(nameof(Title));
        Raise(nameof(Lead));
        Raise(nameof(Percent));
    }
}

internal sealed class SummaryPage(string eyebrow, string title, string lead, IReadOnlyList<StepRow> steps, string problem,
    string primary, string back) : Page
{
    public override string Eyebrow { get; } = eyebrow;
    public override string Title { get; } = title;
    public override string Lead { get; } = lead;
    public IReadOnlyList<StepRow> Steps { get; } = steps;
    public string Problem { get; } = problem;
    public bool HasProblem => Problem.Length > 0;
    public override string Primary { get; } = primary;
    public override string Back { get; } = back;
}

internal sealed class UninstallPage(string version, bool usbipOwned, bool hidHideOwned) : Page
{
    private bool _confirming;
    private bool _custom;
    private bool _keepData = true;
    private bool _removeHidHide = hidHideOwned;
    private bool _removeUsbip = usbipOwned;

    public override string Eyebrow => $"WSGM {version}";
    public override string Title => _confirming ? "Uninstall WSGM now?" : "Uninstall WSGM";

    public override string Lead => _confirming
        ? "WSGM and Steam close. The program files are deleted."
        : "WSGM, its plugins and its sign-in service are removed. Windows settings WSGM changed are put back.";

    public bool CanRemoveUsbip { get; } = usbipOwned;
    public bool CanRemoveHidHide { get; } = hidHideOwned;
    public bool HasOwnedComponents => CanRemoveUsbip || CanRemoveHidHide;

    public bool KeepData
    {
        get => _keepData;
        set => Set(ref _keepData, value);
    }

    public bool RemoveUsbip
    {
        get => _removeUsbip;
        set => Set(ref _removeUsbip, value);
    }

    public bool RemoveHidHide
    {
        get => _removeHidHide;
        set => Set(ref _removeHidHide, value);
    }

    public bool Custom
    {
        get => _custom;
        set => Set(ref _custom, value);
    }

    public bool Confirming
    {
        get => _confirming;
        set
        {
            if (Set(ref _confirming, value))
            {
                Raise(nameof(Title));
                Raise(nameof(Lead));
                Raise(nameof(Primary));
                Raise(nameof(PrimaryIsDanger));
                Raise(nameof(Back));
                Raise(nameof(Editing));
                Raise(nameof(Consequences));
            }
        }
    }

    public bool Editing => !_confirming;

    public IReadOnlyList<string> Consequences =>
    [
        "WSGM and its plugins are removed",
        KeepData ? "Your settings and data are kept" : "Your settings and data are deleted",
        CanRemoveUsbip ? RemoveUsbip ? "The USB/IP driver is removed" : "The USB/IP driver stays" : "",
        CanRemoveHidHide ? RemoveHidHide ? "HidHide is removed" : "HidHide stays" : ""
    ];

    public override string Primary => _confirming ? "Uninstall" : "Continue";
    public override string Back => _confirming ? "Back" : "Cancel";
    public override bool PrimaryIsDanger => _confirming;
}
