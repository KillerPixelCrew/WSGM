using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using WSGM.Install;
using WSGM.Setup.Engine;

namespace WSGM.Setup.UI;

/// <summary>One entry of the rail's step list.</summary>
/// <param name="number">Displayed one-based step number, replaced by a check mark when done.</param>
/// <param name="label">Step name in the navigation rail.</param>
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
    private IReadOnlyList<CommonOption>? _newGraphics;
    private Page _page;
    private ProfilePage? _profile;
    private int _step;
    private UninstallPage? _uninstall;

    public SetupViewModel(SetupOptions options)
        : this(options, null, new MessagePage("WSGM Setup", "Checking this PC…",
            "Looking at what is installed and which hardware this is.", ""), [], 0)
    {
        _ = DetectAsync();
    }

    /// <summary>Composes page commands with an already prepared engine and flow.</summary>
    /// <param name="options">Launch options used by later setup actions.</param>
    /// <param name="engine">Prepared engine, or null before detection; required before running a plan.</param>
    /// <param name="page">Initial page instance whose close policy and actions are used.</param>
    /// <param name="flow">Ordered page identifiers, copied into the view model.</param>
    /// <param name="step">Zero-based current position in the flow.</param>
    internal SetupViewModel(SetupOptions options, SetupEngine? engine, Page page,
        IReadOnlyList<string> flow, int step)
    {
        _options = options;
        _engine = engine;
        _page = page;
        _uninstall = page as UninstallPage;
        _flow.AddRange(flow);
        _step = step;
        PrimaryCommand = new Command(OnPrimary);
        BackCommand = new Command(OnBack);
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
    public string Version => SetupEngine.Display(_engine?.ThisVersion);
    public string InstallPath => InstallLayout.Root;
    public string HintLeft => _flow.Count == 0 ? "" : $"Step {_step + 1} of {_flow.Count}";

    private bool FinishingDrivers => _flow.Contains("finishdrivers");

    /// <summary>Whether the window may close now; each page says (<see cref="Page.OnClose" />).</summary>
    /// <returns>True to close immediately; false when closure is refused or a confirmation page was opened.</returns>
    public bool RequestClose()
    {
        switch (Page.OnClose)
        {
            case CloseBehaviour.Close:
                return true;
            case CloseBehaviour.Ask:
                Page = new ConfirmClosePage(Page);
                return false;
            default:
                return false;
        }
    }

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
            Page = new MessagePage("WSGM Setup",
                ex is WrongSetupAccountException ? "Setup runs for another account" : "Setup could not read this PC",
                ex.Message);
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
                $"This setup installs WSGM {SetupEngine.Display(engine.ThisVersion)}, which is older. Nothing was changed.");
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
        else if (_options.Mode is SetupMode.FinishDrivers)
        {
            // The run after the driver-update restart: the driver step and autostart, nothing else.
            _flow.Clear();
            _flow.AddRange(["finishdrivers", "summary"]);
            GoTo(0);
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
            "update" => ["update", "profile", "customize", "progress", "summary"],
            "maintain-repair" => ["profile", "customize", "progress", "summary"],
            _ =>
            [
                engine.Legacy is null ? "welcome" : "legacy", "hardware", "profile", "customize", "drivers", "progress",
                "summary"
            ]
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
        _uninstall = new UninstallPage(SetupEngine.Display(engine.InstalledVersion ?? engine.ThisVersion),
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
            case "customize":
                Page = new CustomizePage(_profile!);
                break;
            case "drivers":
                Page = new DriversPage();
                break;
            case "uninstall":
                Page = _uninstall!;
                break;
            case "progress":
            case "finishdrivers":
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
                "customize" => "Customize",
                "drivers" => "Drivers",
                "progress" => _flow.Contains("uninstall") ? "Remove" : "Install",
                "finishdrivers" => "Driver",
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
            if (_profile is null)
            {
                _profile = new ProfilePage(_answers);
            }

            // Full with native device support, Minimal without it (declined, or nothing matches).
            _profile.UseDefaultLevel(_hardware?.Chosen is not null);
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
        List<string> changes =
            [$"WSGM {SetupEngine.Display(engine.InstalledVersion)} → {SetupEngine.Display(engine.ThisVersion)}"];
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

        return new UpdatePage(changes, outdated, SetupEngine.Display(engine.InstalledVersion),
            SetupEngine.Display(engine.ThisVersion), NewGraphics());
    }

    // New graphics plugins for an update or repair, checked by default; the update page shows them.
    private IReadOnlyList<CommonOption> NewGraphics()
    {
        return _newGraphics ??= [.. _engine!.NewGpuOffers().Select(offer => new CommonOption(offer.Plugin, true))];
    }

    private InstallChoices Choices()
    {
        var engine = _engine!;
        var answers = (JsonObject)_answers!.DeepClone();
        _profile?.WriteTo(answers);
        if (_hardware is null)
        {
            // Update and repair keep what is installed and add the graphics plugins checked on the
            // update page, or every new one for a repair, which has no page for them.
            return engine.KeptChoices(answers,
                NewGraphics().Where(option => option.Checked).Select(option => option.Plugin.Id));
        }

        var device = _hardware.Chosen?.Offer.Definition.Id;
        answers["deviceIntegration"] = device is not null;
        return new InstallChoices(device,
        [
            .. _hardware.Graphics.Concat(_hardware.Commons).Where(option => option.Checked)
                .Select(option => option.Plugin.Id)
        ], answers);
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
                : FinishingDrivers
                    ? engine.PlanFinishDrivers()
                    : engine.PlanInstall(Choices());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            SetupLog.Error("Planning failed", ex);
            Page = new MessagePage("Setup", "Setup could not start", ex.Message);
            return;
        }

        await RunPlanAsync(steps, uninstall);
    }

    /// <summary>Runs the engine's prepared steps and publishes their summary when they finish.</summary>
    /// <param name="steps">Prepared engine steps, executed in their supplied order on a worker.</param>
    /// <param name="uninstall">Whether progress and completion use the uninstall flow.</param>
    /// <param name="dispatch">Schedules progress notifications on the UI thread; null uses the Avalonia dispatcher.</param>
    /// <returns>Completion after the engine finishes and the summary page is selected; unexpected exceptions propagate.</returns>
    internal async Task RunPlanAsync(IReadOnlyList<SetupStep> steps, bool uninstall,
        Action<Action>? dispatch = null)
    {
        var engine = _engine!;
        dispatch ??= action => Dispatcher.UIThread.Post(action);
        var progress = uninstall ? new ProgressPage("Uninstalling", "Removing WSGM")
            : FinishingDrivers ? new ProgressPage("Finishing", "Installing the controller driver")
            : new ProgressPage("Installing", "Installing WSGM");
        foreach (var step in steps)
        {
            progress.Steps.Add(new StepRow(step));
        }

        Page = progress;
        var ok = await Task.Run(() => engine.Run(steps, () => dispatch(progress.Update)));
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
                    "Setup couldn't confirm that HidHide shows these devices again. Setup didn't retry.",
                    rows,
                    "Open HidHide Configuration Client, go to Devices, and untick:\n"
                    + string.Join("\n", engine.StillHiddenDevices)
                    + "\nOr reinstall WSGM and uninstall again, which tries once more."
                    + (failed.Length > 0
                        ? "\n\n" + string.Join("\n", failed.Select(row => $"{row.Step.Label}: {row.Note}"))
                        : ""),
                    "Close", "");
                return;
            }

            var dataFailed = failed.Any(row => row.Step.Label == "Deleting settings and data");
            var filesFailed = failed.Any(row => row.Step.Label == "Deleting program files");
            Page = new SummaryPage(failed.Length > 0 ? "Uninstall finished with a problem" : "Done",
                filesFailed ? "Some WSGM program files remain" : "WSGM is uninstalled",
                _uninstall!.KeepData
                    ? "Your settings and data are still there if you install WSGM again."
                    : dataFailed
                        ? "Some settings and data could not be deleted. See the details below."
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
            var profileStarted = progress.Steps.Any(row => row.Step.Label == "Applying your profile"
                                                           && row.Step.State is not StepState.Waiting);
            Page = legacyRemoved
                ? new SummaryPage("Setup stopped",
                    $"WSGM {engine.Legacy!.Value.Version} was removed, but the new version is not installed",
                    "Run setup again to install WSGM. Your settings are still there.", rows,
                    string.Join("\n", failed.Select(row => row.Note)), "Close", "")
                : profileStarted || engine.RollbackIncomplete
                    ? new SummaryPage("Setup stopped",
                        engine.RollbackIncomplete ? "The installation needs repair" : "Some settings may have changed",
                        engine.RollbackIncomplete
                            ? "Setup could not fully restore the previous installation. Run setup again to repair it."
                            : "The profile step had started. Run setup again to finish or repair the installation.",
                        rows,
                        string.Join("\n", failed.Select(row => $"{row.Step.Label}: {row.Note}")), "Close", "")
                    : new SummaryPage("Setup stopped", "Nothing was changed",
                        "The installed WSGM, if there was one, is back as it was.", rows,
                        string.Join("\n", failed.Select(row => row.Note)), "Close", "");
            return;
        }

        if (engine.DriverUpdatePending)
        {
            // WSGM's autostart is off now and the only way forward is the restart. WSGM must not
            // start before it: it attaches the virtual pad, and the driver cannot be replaced once
            // anything has.
            Page = new RestartPage(rows, engine.DriverUpdateResumes);
            return;
        }

        var problem = string.Join("\n", failed.Select(row => $"{row.Step.Label}: {row.Note}"));
        var title = FinishingDrivers ? "The controller driver is installed"
            : _flow.Contains("update") ? $"WSGM {SetupEngine.Display(engine.ThisVersion)} is installed"
            : "WSGM is ready";
        var lead = engine.RestartRequired
            ? "Restart Windows to turn on the virtual controller. Everything else works now."
            : "";
        Page = new SummaryPage(problem.Length > 0 ? "Done, with a problem" : "Done", title, lead, rows, problem,
            engine.RestartRequired ? "Start WSGM, restart later" : "Start WSGM", "", true);
    }

    // A page hides an action with an empty label; the hidden action then does nothing from any input.
    private void OnPrimary()
    {
        if (Page.Primary.Length == 0)
        {
            return;
        }

        switch (Page)
        {
            case ConfirmClosePage confirm:
                Page = confirm.Resume;
                return;
            case RestartPage:
                SetupEngine.RestartWindows();
                CloseRequested?.Invoke();
                return;
            case MessagePage:
                CloseRequested?.Invoke();
                return;
            case SummaryPage summary:
                summary.Complete(() => _engine?.StartWsgm(), () => CloseRequested?.Invoke());
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
        if (Page.Back.Length == 0)
        {
            return;
        }

        switch (Page)
        {
            case ConfirmClosePage:
                CloseRequested?.Invoke();
                return;
            case UninstallPage { Confirming: true } page:
                page.Confirming = false;
                return;
            case UninstallPage:
            case UpdatePage:
                CloseRequested?.Invoke();
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
}
