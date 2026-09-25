using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using WSGM.Install;
using WSGM.Setup.Engine;

namespace WSGM.Setup.UI;

/// <summary>One entry of the rail's step list.</summary>
internal sealed class RailStep(int number, string label) : Observable
{
    private string _state = "";
    public int Number { get; } = number;
    public string Label { get; } = label;

    /// <summary><c>done</c>, <c>current</c> or empty.</summary>
    public string State
    {
        get => _state;
        set
        {
            if (Set(ref _state, value))
            {
                Raise(nameof(Marker));
                Raise(nameof(IsCurrent));
            }
        }
    }

    public string Marker => State == "done" ? "✓" : Number.ToString();
    public bool IsCurrent => State == "current";
}

/// <summary>
///     Drives setup's pages, following the approved mockup: install, update from an older WSGM 2, the repair
///     and uninstall chooser, and uninstall. Every change to the machine happens in <see cref="SetupEngine" />.
/// </summary>
internal sealed class SetupViewModel : Observable
{
    private readonly List<string> _flow = [];
    private readonly SetupOptions _options;
    private JsonObject? _answers;
    private Task<JsonObject>? _answersTask;
    private SetupEngine? _engine;
    private HardwarePage? _hardware;
    private Page _page;
    private ProfilePage? _profile;
    private int _step;
    private UninstallPage? _uninstall;

    public SetupViewModel(SetupOptions options)
    {
        _options = options;
        _page = new MessagePage("WSGM Setup", "Checking this PC…",
            "Looking at what is installed and which hardware this is.", "");
        PrimaryCommand = new Command(OnPrimary);
        BackCommand = new Command(OnBack);
        _ = DetectAsync();
    }

    public ObservableCollection<RailStep> Rail { get; } = [];

    public Page Page
    {
        get => _page;
        private set
        {
            if (Set(ref _page, value))
            {
                Raise(nameof(HintLeft));
            }
        }
    }

    public ICommand PrimaryCommand { get; }
    public ICommand BackCommand { get; }
    public string Version => _engine?.ThisVersion.ToString(3) ?? "";
    public string InstallPath => InstallLayout.Root;
    public string HintLeft => _flow.Count == 0 ? "" : $"Step {_step + 1} of {_flow.Count}";

    /// <summary>Raised when setup should close.</summary>
    public event Action? CloseRequested;

    private async Task DetectAsync()
    {
        try
        {
            _engine = await Task.Run(() => SetupEngine.Detect(_options.PayloadDirectory));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            SetupLog.Error("Detection failed", ex);
            Page = new MessagePage("WSGM Setup", "Setup could not read this PC", ex.Message);
            return;
        }

        Raise(nameof(Version));
        var engine = _engine;
        if (_options.Mode is SetupMode.Uninstall)
        {
            StartUninstall();
        }
        else if (engine.Kind is SetupKind.NewerInstalled)
        {
            Page = new MessagePage($"WSGM {engine.InstalledVersion}", "A newer WSGM is installed",
                $"This setup installs WSGM {engine.ThisVersion.ToString(3)}, which is older. Nothing was changed.");
        }
        else if (engine.Payload is null || engine.Offers is null)
        {
            Page = new MessagePage("WSGM Setup", "This setup carries no WSGM",
                "It was built without its payload. Run it with /payload=<publish directory>, or use a release setup.");
        }
        else if (!engine.SteamInstalled)
        {
            Page = new MessagePage("WSGM Setup", "Install Steam first",
                "WSGM runs on top of Steam. Install Steam, sign in once, then run setup again.");
        }
        else if (engine.Kind is SetupKind.Maintain && _options.Mode is not SetupMode.Repair)
        {
            ShowMaintain();
        }
        else if (engine.Kind is SetupKind.Maintain)
        {
            StartFlow("maintain-repair");
        }
        else if (engine.Kind is SetupKind.Update)
        {
            StartFlow("update");
        }
        else
        {
            StartFlow("install");
        }
    }

    private void ShowMaintain()
    {
        _flow.Clear();
        Rail.Clear();
        Rail.Add(new RailStep(1, "Choose") { State = "current" });
        Page = new MaintainPage(_engine!.ThisVersion, () => StartFlow("maintain-repair"), StartUninstall,
            () => CloseRequested?.Invoke());
    }

    private void StartFlow(string kind)
    {
        var engine = _engine!;
        _flow.Clear();
        _flow.AddRange(kind switch
        {
            "update" => ["update", "profile", "progress", "summary"],
            "maintain-repair" => ["profile", "progress", "summary"],
            _ => [engine.Legacy is null ? "welcome" : "legacy", "hardware", "profile", "drivers", "progress", "summary"]
        });
        // Unpacking the new WSGM and asking it for the current answers takes a moment; start now.
        _answersTask = Task.Run(engine.PrepareAnswers);
        GoTo(0);
    }

    private void StartUninstall()
    {
        var engine = _engine!;
        _flow.Clear();
        _flow.AddRange(["uninstall", "progress", "summary"]);
        _uninstall = new UninstallPage(engine.InstalledVersion?.ToString(3) ?? engine.ThisVersion.ToString(3),
            engine.Components.Usbip && InstalledComponents.UsbipPresent(),
            engine.Components.HidHide && InstalledComponents.HidHidePresent());
        GoTo(0);
    }

    private void GoTo(int index)
    {
        _step = index;
        var id = _flow[index];
        if (id == "drivers" && !NeedsDrivers())
        {
            // The drivers page only appears when the chosen plugin needs drivers that are missing.
            _flow.RemoveAt(index);
            GoTo(index);
            return;
        }

        UpdateRail();
        switch (id)
        {
            case "welcome":
                Page = new WelcomePage(_engine!.ThisVersion, _engine.SteamInstalled);
                break;
            case "legacy":
                Page = new LegacyPage(_engine!.Legacy!.Value.Version, _engine.ThisVersion);
                break;
            case "update":
                Page = BuildUpdatePage();
                break;
            case "hardware":
                _hardware ??= BuildHardwarePage();
                Page = _hardware;
                break;
            case "profile":
                _ = ShowProfileAsync();
                break;
            case "drivers":
                Page = new DriversPage();
                break;
            case "uninstall":
                Page = _uninstall!;
                break;
            case "progress":
                _ = RunAsync();
                break;
        }
    }

    private void UpdateRail()
    {
        Rail.Clear();
        for (var i = 0; i < _flow.Count; i++)
        {
            Rail.Add(new RailStep(i + 1, _flow[i] switch
            {
                "welcome" => "Welcome",
                "legacy" => "WSGM 1.0",
                "update" => "What's new",
                "hardware" => "Hardware",
                "profile" => "Profile",
                "drivers" => "Drivers",
                "progress" => _flow.Contains("uninstall") ? "Remove" : "Install",
                "summary" => "Done",
                "uninstall" => "Uninstall",
                _ => _flow[i]
            })
            {
                State = i < _step ? "done" : i == _step ? "current" : ""
            });
        }

        Raise(nameof(HintLeft));
    }

    private async Task ShowProfileAsync()
    {
        Page = new MessagePage("Profile", "Reading your settings…", "", "");
        try
        {
            _answers ??= await _answersTask!;
            _profile ??= new ProfilePage(_answers);
            Page = _profile;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            SetupLog.Error("Preparing the answers failed", ex);
            Page = new MessagePage("Profile", "Setup could not prepare WSGM", ex.Message);
        }
    }

    private HardwarePage BuildHardwarePage()
    {
        var identity = DeviceMachineIdentity.Collect();
        var device = identity.SystemProduct ?? identity.BaseboardProduct ?? "This PC";
        var detail = string.Join(" · ", new[] { identity.BaseboardProduct, identity.SystemSku, identity.ProcessorName }
            .Where(part => !string.IsNullOrWhiteSpace(part)));
        return new HardwarePage(device, detail, _engine!.Offers!, () => { });
    }

    private UpdatePage BuildUpdatePage()
    {
        var engine = _engine!;
        var bundle = engine.Payload!.Bundle;
        var installedBundle = BundleManifest.TryRead(InstallLayout.InstalledBundle);
        List<string> changes = [$"WSGM {engine.InstalledVersion?.ToString(3)} → {engine.ThisVersion.ToString(3)}"];
        foreach (var id in engine.InstalledPluginIds)
        {
            var next = bundle.Plugins.First(plugin => plugin.Id == id);
            var previous = installedBundle?.Plugins.FirstOrDefault(plugin => plugin.Id == id)?.Version;
            changes.Add(previous is null || previous == next.Version
                ? $"{next.Name} {next.Version}"
                : $"{next.Name} {previous} → {next.Version}");
        }

        // A community plugin the user has that this release could not build.
        List<string> outdated = [];
        foreach (var old in installedBundle?.Plugins.Where(plugin => plugin.Community) ?? [])
        {
            if (bundle.Plugins.All(plugin => plugin.Id != old.Id))
            {
                var contact = bundle.Outdated.FirstOrDefault(entry => entry.Id == old.Id)?.Contact ?? old.Contact;
                outdated.Add(
                    $"{old.Name} has no build for WSGM {engine.ThisVersion.ToString(3)} and stops loading after this update."
                    + (contact is null ? "" : $" Its developer: {contact}."));
            }
        }

        return new UpdatePage(changes, outdated, engine.InstalledVersion?.ToString(3) ?? "",
            engine.ThisVersion.ToString(3));
    }

    private InstallChoices Choices()
    {
        var engine = _engine!;
        var answers = (JsonObject)_answers!.DeepClone();
        _profile?.WriteTo(answers);
        string? device;
        IReadOnlyList<string> common;
        if (_hardware is not null)
        {
            device = _hardware.Chosen?.Offer.Plugin.Id;
            common = [.. _hardware.Commons.Where(option => option.Checked).Select(option => option.Plugin.Id)];
        }
        else
        {
            // Update and repair keep what is installed.
            device = engine.Offers!.DeviceCandidates.FirstOrDefault(offer => offer.Installed)?.Plugin.Id;
            common = [.. engine.Offers.Common.Where(offer => offer.Installed).Select(offer => offer.Plugin.Id)];
        }

        answers["deviceIntegration"] = device is not null;
        return new InstallChoices(device, common, answers);
    }

    private bool NeedsDrivers()
    {
        return (_answers is not null && _engine!.NeedsDrivers(Choices()))
               || (_hardware?.Chosen?.Offer.Components.Contains(SetupComponent.ControllerStack) == true
                   && (!InstalledComponents.UsbipPresent() || !InstalledComponents.HidHidePresent()));
    }

    private async Task RunAsync()
    {
        var engine = _engine!;
        var uninstall = _flow.Contains("uninstall");
        IReadOnlyList<SetupStep> steps;
        try
        {
            steps = uninstall
                ? engine.PlanUninstall(new UninstallChoices(_uninstall!.KeepData, _uninstall.RemoveUsbip,
                    _uninstall.RemoveHidHide))
                : engine.PlanInstall(Choices());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            SetupLog.Error("Planning failed", ex);
            Page = new MessagePage("Setup", "Setup could not start", ex.Message);
            return;
        }

        ProgressPage progress = new(uninstall);
        foreach (var step in steps)
        {
            progress.Steps.Add(new StepRow(step));
        }

        Page = progress;
        var ok = await Task.Run(() => engine.Run(steps, () => Dispatcher.UIThread.Post(progress.Update)));
        progress.Update();
        GoTo(_step + 1);
        ShowSummary(uninstall, ok, progress);
    }

    private void ShowSummary(bool uninstall, bool ok, ProgressPage progress)
    {
        var engine = _engine!;
        var failed = progress.Steps.Where(row => row.Step.State is StepState.Failed).ToArray();
        IReadOnlyList<StepRow> rows = [.. progress.Steps];
        if (uninstall)
        {
            if (engine.StillHiddenDevices.Count > 0)
            {
                Page = new SummaryPage("Uninstall finished with a problem", "Your controller may still be hidden",
                    "WSGM is removed, but setup couldn't confirm that HidHide shows these devices again. Setup didn't retry.",
                    rows,
                    "Open HidHide Configuration Client, go to Devices, and untick:\n"
                    + string.Join("\n", engine.StillHiddenDevices)
                    + "\nOr reinstall WSGM and uninstall again, which tries once more.",
                    "Close", "");
                return;
            }

            Page = new SummaryPage("Done", "WSGM is uninstalled",
                _uninstall!.KeepData
                    ? "Your settings and data are still there if you install WSGM again."
                    : "Your settings and data were deleted.",
                rows, string.Join("\n", failed.Select(row => $"{row.Step.Label}: {row.Note}")), "Close", "");
            return;
        }

        if (!ok)
        {
            // The old version's own uninstaller cannot be undone, so a failure after it is not "nothing changed".
            var legacyRemoved = engine.Legacy is { } legacy
                                && progress.Steps.Any(row =>
                                    row.Step.Label.EndsWith(legacy.Version, StringComparison.Ordinal)
                                    && row.Step.State is StepState.Done);
            Page = legacyRemoved
                ? new SummaryPage("Setup stopped",
                    $"WSGM {engine.Legacy!.Value.Version} was removed, but the new version is not installed",
                    "Run setup again to install WSGM. Your settings are still there.", rows,
                    string.Join("\n", failed.Select(row => row.Note)), "Close", "")
                : new SummaryPage("Setup stopped", "Nothing was changed",
                    "The installed WSGM, if there was one, is back as it was.", rows,
                    string.Join("\n", failed.Select(row => row.Note)), "Close", "");
            return;
        }

        var problem = string.Join("\n", failed.Select(row => $"{row.Step.Label}: {row.Note}"));
        var title = _flow.Contains("update") ? $"WSGM {engine.ThisVersion.ToString(3)} is installed" : "WSGM is ready";
        var lead = engine.RestartRequired
            ? "Restart Windows to turn on the virtual controller. Everything else works now."
            : "";
        Page = new SummaryPage(problem.Length > 0 ? "Done, with a problem" : "Done", title, lead, rows, problem,
            engine.RestartRequired ? "Start WSGM, restart later" : "Start WSGM", "");
    }

    private void OnPrimary()
    {
        switch (Page)
        {
            case MessagePage:
            case SummaryPage:
                if (Page is SummaryPage && !_flow.Contains("uninstall"))
                {
                    _engine?.StartWsgm();
                }

                CloseRequested?.Invoke();
                return;
            case UninstallPage { Confirming: false } page:
                page.Confirming = true;
                return;
            case HardwarePage hardware when hardware.NeedsChoice && hardware.InstallPlugin && hardware.Chosen is null:
                return;
        }

        if (_flow.Count > 0 && _step + 1 < _flow.Count)
        {
            GoTo(_step + 1);
        }
    }

    private void OnBack()
    {
        switch (Page)
        {
            case UninstallPage { Confirming: true } page:
                page.Confirming = false;
                return;
            case UninstallPage:
            case UpdatePage:
            case MaintainPage:
                CloseRequested?.Invoke();
                return;
            case ProgressPage:
            case SummaryPage:
                return;
        }

        if (_step > 0)
        {
            GoTo(_step - 1);
        }
        else if (_engine?.Kind is SetupKind.Maintain)
        {
            ShowMaintain();
        }
    }

    /// <summary>Closes the window through the desktop lifetime.</summary>
    internal static void Shutdown()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }
}
