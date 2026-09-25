using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using WSGM.DeviceLab.Application;
using WSGM.DeviceLab.Knowledge;
using WSGM.DeviceLab.Preflight;
using WSGM.DeviceLab.Wizard;

namespace WSGM.DeviceLab.Gui;

/// <summary>How the wizard was started.</summary>
/// <param name="Elevated">Whether the process has an administrator token.</param>
/// <param name="ElevationNote">Why it does not, when it does not.</param>
/// <param name="ProjectPath">A project to open straight away.</param>
internal sealed record WizardOptions(bool Elevated, string? ElevationNote, string? ProjectPath);

/// <summary>
///     The attended wizard a tester runs: one stage at a time, every stage saved into a project that can
///     be reopened to redo any part of it.
/// </summary>
/// <remarks>
///     Stage logic lives in <c>WSGM.DeviceLab.Wizard</c>; this window only sequences it and asks the
///     tester. Selecting a stage shows its result; only an explicit start or "Run again" begins a new
///     attempt. Every file, registry and driver call runs off the UI thread, one operation at a time, and
///     closing the window waits for that operation before it undoes the session's machine changes.
/// </remarks>
internal sealed partial class WizardWindow : Window
{
    private const string FinishId = "finish";

    private readonly CancellationTokenSource _lifetime = new();

    private readonly TextBox _note = new()
    {
        PlaceholderText = "Anything to add? For example: the left stick feels loose.",
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        MaxLength = 2000,
        Height = 80
    };

    private readonly LabMachineState _machine = LabMachineState.ForCurrentUser;
    private readonly WizardOptions _options;
    private readonly ContentControl _page = new();
    private readonly LabPawnIo _pawnIo;
    private readonly TextBlock _projectLine = Muted(string.Empty);
    private readonly ListBox _stages = new() { MinWidth = 240 };
    private bool _closeReady;
    private bool _closing;
    private bool _confirmingClose;
    private Task _operation = Task.CompletedTask;
    private DeviceLabOwnerReservation? _owner;
    private LabProject? _project;
    private string? _running;
    private CancellationTokenSource _stage = new();
    private bool _refreshing;

    public WizardWindow(WizardOptions options)
    {
        _options = options;
        _pawnIo = LabPawnIo.ForMachine(_machine);
        Title = "WSGM Device Lab";
        Width = 1100;
        Height = 760;
        MinWidth = 820;
        MinHeight = 560;

        Grid root = new()
        {
            Margin = new Thickness(18),
            ColumnDefinitions = new ColumnDefinitions("260,*"),
            ColumnSpacing = 18
        };
        StackPanel side = new() { Spacing = 10 };
        side.Children.Add(new TextBlock { Text = "Device Lab", FontSize = 24, FontWeight = FontWeight.SemiBold });
        side.Children.Add(_projectLine);
        side.Children.Add(_stages);
        Button stop = new() { Content = "Stop and save (Esc)" };
        stop.Click += (_, _) => StopStage();
        side.Children.Add(stop);
        side.Children.Add(Muted("Notes for the developer"));
        side.Children.Add(_note);
        side.Children.Add(Action("Save note", SaveNote));
        side.Children.Add(Buttons(Action("Open test folder", OpenProjectFolder), Action("Help and licences", ShowHelp)));
        root.Children.Add(side);
        ScrollViewer scroller = new() { Content = _page };
        Grid.SetColumn(scroller, 1);
        root.Children.Add(scroller);
        Content = root;

        _stages.SelectionChanged += (_, _) =>
        {
            if (!_refreshing && _stages.SelectedItem is ListBoxItem { Tag: string id })
            {
                ShowStage(id);
            }
        };
        Closing += (_, args) =>
        {
            if (_closeReady)
            {
                return;
            }

            args.Cancel = true;
            if (!_closing && !_confirmingClose)
            {
                _ = ConfirmCloseAsync();
            }
        };
        Opened += (_, _) => Start();
        KeyDown += (_, args) =>
        {
            if (args.Key == Avalonia.Input.Key.Escape)
            {
                StopStage();
            }
        };
    }

    private void Start()
    {
        _stages.IsEnabled = false;
        _page.Content = Page("Test your handheld", "Checking what an earlier session left behind...");
        Run(null, async () =>
        {
            if (_options.Elevated)
            {
                await Task.Run(_pawnIo.Reconcile);
            }

            // Undo what a killed session left applied: power settings and a switched controller mode.
            List<string> recovered = [];
            if (_options.Elevated && await Task.Run(() => LabPowerRecovery.RestoreRecorded(_machine)) is { } power)
            {
                recovered.Add(power.Message);
            }

            if (LabControllerInit.HasPending)
            {
                var problem = await Task.Run(() => LabControllerInit.RecoverPending(_lifetime.Token));
                recovered.Add(problem is null
                    ? "The controller was switched back to the mode it had before an earlier test."
                    : $"The controller could not be switched back after an earlier test: {problem}");
            }

            if (recovered.Count > 0)
            {
                var notice = Page("Test your handheld", "An earlier test did not finish.");
                foreach (var line in recovered)
                {
                    notice.Children.Add(Status(line));
                }

                _page.Content = notice;
                await AskAsync(notice, "Continue");
            }

            if (_options.ProjectPath is { } path)
            {
                await OpenAsync(path);
            }
            else
            {
                ShowWelcome();
            }
        });
    }

    private void ShowWelcome()
    {
        var page = Page("Test your handheld",
            "This walks you through a set of checks and saves everything into a report you can send back. "
            + "It takes about twenty minutes. You can stop at any time and continue later from the saved test.");
        if (!_options.Elevated)
        {
            page.Children.Add(Warning(_options.ElevationNote
                                      ??
                                      "Device Lab is not running as administrator, so several checks will be skipped."));
        }

        page.Children.Add(Buttons(
            Action("Start a new test", () => Run(page, CreateProjectAsync)),
            Action("Continue a saved test", () => Run(page, PickProjectAsync))));
        _page.Content = page;
    }

    private async Task CreateProjectAsync()
    {
        var parent = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "WSGM Device Lab");
        var directory = Path.Combine(parent, $"Device test {DateTime.Now:yyyy-MM-dd HHmmss}");
        var decision = DeviceLabOutputPathPolicy.Evaluate(directory, DeviceLabOutputTargetKind.Directory, Boundaries());
        if (!decision.IsAllowed)
        {
            throw new IOException($"The test folder could not be created: {decision.Reason ?? directory}");
        }

        var project = await Task.Run(() =>
        {
            Directory.CreateDirectory(parent);
            return LabProject.Create(decision.FullPath!, LabStages.Ids, ToolVersion(), DateTimeOffset.UtcNow,
                ToolSha256(), SourceRevision());
        });
        Load(project);
    }

    private async Task PickProjectAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select a saved Device Lab test",
            AllowMultiple = false
        });
        if (folders.Count > 0)
        {
            await OpenAsync(folders[0].Path.LocalPath);
        }
    }

    private async Task OpenAsync(string path)
    {
        Load(await Task.Run(() => LabProject.Open(path)));
    }

    private void Load(LabProject project)
    {
        _project = project;
        _projectLine.Text = project.Directory;
        var next = LabStages.All.FirstOrDefault(stage =>
                project.Segment(stage.Id).Status is LabSegmentStatus.NotStarted)
            ?.Id ?? FinishId;
        ShowStage(next, true);
    }

    private void RefreshStages(string? selected)
    {
        if (_project is null)
        {
            return;
        }

        _refreshing = true;
        try
        {
            List<ListBoxItem> items = [];
            foreach (var stage in LabStages.All)
            {
                var state = _project.Segment(stage.Id);
                var mark = state.Status switch
                {
                    LabSegmentStatus.Completed => "done",
                    LabSegmentStatus.Skipped => "skipped",
                    LabSegmentStatus.Failed => "failed",
                    _ => string.Empty
                };
                items.Add(new ListBoxItem
                {
                    Tag = stage.Id,
                    Content = mark.Length == 0 ? stage.Title : $"{stage.Title}  ({mark})"
                });
            }

            items.Add(new ListBoxItem { Tag = FinishId, Content = "Finish and share" });
            _stages.ItemsSource = items;
            _stages.SelectedItem = items.FirstOrDefault(item => (string?)item.Tag == selected);
        }
        finally
        {
            _refreshing = false;
        }
    }

    // Shows a stage without changing anything: a finished stage shows its result and a "Run again"
    // button, an unstarted one a "Start" button. Only the finish page does work on selection, and that
    // work (restoring HidHide, building the preview) changes no evidence.
    private void ShowStage(string id, bool fromCompletedOperation = false)
    {
        if (_project is not { } project || (!fromCompletedOperation && !_operation.IsCompleted))
        {
            return;
        }

        RefreshStages(id);
        _stages.IsEnabled = true;
        if (id == FinishId)
        {
            var finish = Page("Finish and share", "Preparing the report...");
            _page.Content = finish;
            Run(finish, () => ShowFinishAsync(project, finish), fromCompletedOperation);
            return;
        }

        var stage = LabStages.All.Single(item => item.Id == id);
        var page = Page(stage.Title, stage.Description);
        _page.Content = page;
        var state = project.Segment(id);
        if (state.Status is LabSegmentStatus.NotStarted)
        {
            if (state.Attempts > 0)
            {
                page.Children.Add(Muted("An earlier run of this step did not finish."));
            }

            page.Children.Add(Buttons(
                Action("Start", () => StartStage(id)),
                Action("Skip this step", () => Run(page, async () =>
                {
                    await Task.Run(() => SkipStage(project, id));
                    Next(id);
                }))));
            return;
        }

        page.Children.Add(Status(state.Status switch
        {
            LabSegmentStatus.Completed => "Done.",
            LabSegmentStatus.Skipped => "Skipped.",
            _ => "This step failed."
        }));
        if (state.Summary is { } summary)
        {
            page.Children.Add(Status(summary));
        }

        page.Children.Add(Muted($"Run {state.Attempts} time(s), last on {state.UpdatedAt?.ToLocalTime():g}."));
        page.Children.Add(Buttons(Action("Run again", () => StartStage(id))));
    }

    private void StartStage(string id, bool fromCompletedOperation = false)
    {
        if (_project is not { } project || (!fromCompletedOperation && !_operation.IsCompleted))
        {
            return;
        }

        _running = id;
        RefreshStages(id);
        var page = Page(LabStages.All.Single(stage => stage.Id == id).Title, string.Empty);
        _page.Content = page;
        switch (id)
        {
            case LabStages.Preflight:
                Run(page, () => RunPreflightAsync(project, page), fromCompletedOperation);
                break;
            case LabStages.Identity:
                Run(page, () => RunIdentityAsync(project, page), fromCompletedOperation);
                break;
            case LabStages.SystemDump:
                Run(page, () => RunSystemDumpAsync(project, page), fromCompletedOperation);
                break;
            case LabStages.Buttons:
                RunHardware(page, () => RunButtonsAsync(project, page), fromCompletedOperation);
                break;
            case LabStages.Motion:
                RunHardware(page, () => RunMotionAsync(project, page), fromCompletedOperation);
                break;
            case LabStages.Rumble:
                RunHardware(page, () => RunRumbleAsync(project, page), fromCompletedOperation);
                break;
            case LabStages.Power:
                RunHardware(page, () => RunPowerAsync(project, page), fromCompletedOperation);
                break;
            case LabStages.Sleep:
                RunHardware(page, () => RunSleepAsync(project, page), fromCompletedOperation);
                break;
        }
    }

    // Called at the end of an operation, which still counts as running until it returns.
    private void Next(string after)
    {
        var next = LabStages.All.SkipWhile(stage => stage.Id != after).Skip(1).FirstOrDefault()?.Id;
        if (next is null)
        {
            ShowStage(FinishId, true);
        }
        else
        {
            StartStage(next, true);
        }
    }

    private async Task RunPreflightAsync(LabProject project, StackPanel page)
    {
        page.Children.Add(
            Status("These checks make sure nothing else hides or changes your controller while the test runs."));
        var attempt = await Task.Run(() => project.BeginAttempt(LabStages.Preflight, DateTimeOffset.UtcNow));
        var allowance = HidHideAllowance.ForMachine(_machine, DeviceLabExecutable.CurrentPath);
        Dictionary<string, object?> evidence = new() { ["elevated"] = _options.Elevated };

        // 1. An entry an earlier session left behind: the tester chooses to remove or keep it.
        if (await Task.Run(() => _machine.Read().HidHideEntry) is not null)
        {
            page.Children.Add(Heading("Left over from an earlier test"));
            page.Children.Add(Status(
                "An earlier test added this tool to HidHide's allowed programs and did not remove it. Remove it now?"));
            if (await AskAsync(page, "Remove it", "Keep it") == 0)
            {
                var problem = await Task.Run(allowance.RestoreRecorded);
                evidence["recoveredHidHideEntry"] = problem ?? "removed";
                page.Children.Add(Status(problem is null
                    ? "Removed the HidHide entry an earlier session left behind."
                    : $"The entry could not be removed: {problem}"));
            }
            else
            {
                evidence["recoveredHidHideEntry"] = $"kept by the tester: {await Task.Run(allowance.KeepRecorded)}";
                page.Children.Add(Status("Kept. It will not be removed automatically."));
            }
        }

        // 2. WSGM's device integration must not run beside the test. The reservation is held until the
        //    window closes.
        page.Children.Add(Heading("WSGM"));
        if (_owner is null)
        {
            var reserved = await Task.Run(DeviceLabOwnerInspector.Reserve);
            evidence["deviceOwner"] = reserved.Inspection.State.ToString();
            _owner = reserved.Reservation;
        }

        page.Children.Add(_owner is not null
            ? Status("WSGM's device integration is not running; the test holds its place until you close this window.")
            : Warning(
                "WSGM's device integration is running or could not be checked. Close WSGM before the hardware steps, then run this step again."));

        // 3. Other managers.
        var managers = await Task.Run(ManagerConflicts.Running);
        evidence["managers"] = managers;
        evidence["drivers"] = await Task.Run(ManagerConflicts.Drivers);
        page.Children.Add(Heading("Other controller software"));
        List<ManagerCloseResult> closed = [];
        evidence["closeRequests"] = closed;
        page.Children.Add(ManagersPanel(managers, closed));

        // 4. HidHide.
        page.Children.Add(Heading("Hidden devices (HidHide)"));
        if (!_options.Elevated)
        {
            page.Children.Add(Status("Skipped: changing HidHide needs administrator rights."));
        }
        else
        {
            var state = await Task.Run(allowance.Read);
            evidence["hidHide"] = new
                { state.Available, state.Active, state.Inverse, Entries = state.Applications.Count };
            if (!state.Available)
            {
                page.Children.Add(Status("HidHide is not installed, so no device is hidden from this tool."));
            }
            else
            {
                if (allowance.DeniedByInverseList(state))
                {
                    page.Children.Add(Warning(
                        "HidHide is set to hide devices from the programs on its list, and this tool is on that list. It will not see the controller. Remove it from HidHide's list, or continue and the tests will miss the controller."));
                    evidence["hidHideAllow"] = "denied by inverse list";
                    if (await AskAsync(page, "Continue anyway", "Stop here") == 1)
                    {
                        await Task.Run(() =>
                        {
                            project.WriteEvidence(attempt, "preflight", evidence);
                            project.Finish(LabStages.Preflight, LabSegmentStatus.Failed,
                                "Stopped: HidHide blocks this tool.", DateTimeOffset.UtcNow);
                        });
                        page.Children.Add(Buttons(Action("Check again", () => StartStage(LabStages.Preflight))));
                        return;
                    }
                }
                else if (state.Inverse)
                {
                    page.Children.Add(Status(
                        "HidHide is in inverse mode and this tool is not on its list, so it sees every device. Nothing to change."));
                    evidence["hidHideAllow"] = "inverse mode, not listed";
                }
                else if (allowance.AlreadyAllowed(state))
                {
                    page.Children.Add(Status("This tool is already allowed by HidHide."));
                    evidence["hidHideAllow"] = "already allowed";
                }
                else
                {
                    page.Children.Add(Status(
                        "HidHide can hide the controller from this tool. Add this tool to HidHide's allowed programs for the test? It is removed again when you finish or close the window."));
                    if (await AskAsync(page, "Add it, then undo it later", "Leave HidHide alone") == 0)
                    {
                        var allowed = await Task.Run(allowance.TryAllow);
                        evidence["hidHideAllow"] = allowed;
                        page.Children.Add(Status(allowed.Added is not null
                            ? "Added. It is removed again when you finish or close the window."
                            : allowed.Reason ?? "Nothing to change."));
                    }
                    else
                    {
                        evidence["hidHideAllow"] = "declined by the tester";
                        page.Children.Add(Status("Left alone. Hidden controllers will not show up in the tests."));
                    }
                }
            }
        }

        // 5. PawnIO.
        page.Children.Add(Heading("PawnIO driver"));
        await PawnIoAsync(page, evidence);

        page.Children.Add(Buttons(
            Action("Check again", () => StartStage(LabStages.Preflight)),
            Action("Continue", () => Run(page, async () =>
            {
                await Task.Run(() =>
                {
                    project.WriteEvidence(attempt, "preflight", evidence);
                    project.Finish(LabStages.Preflight, LabSegmentStatus.Completed,
                        _owner is null ? "WSGM was running; the hardware steps will refuse to start." : null,
                        DateTimeOffset.UtcNow);
                });
                Next(LabStages.Preflight);
            }))));
    }

    private StackPanel ManagersPanel(IReadOnlyList<RunningManager> managers, List<ManagerCloseResult> closed)
    {
        StackPanel panel = new() { Spacing = 6 };
        if (managers.Count == 0)
        {
            panel.Children.Add(Status("None running."));
            return panel;
        }

        foreach (var running in managers)
        {
            var manager = running.Manager;
            StackPanel row = new() { Orientation = Orientation.Horizontal, Spacing = 10 };
            var line = Status($"{manager.Label}: {manager.Why}.");
            row.Children.Add(line);
            if (manager.Closable)
            {
                Button close = new() { Content = "Close it" };
                close.Click += (_, _) => Run(panel, async () =>
                {
                    close.IsEnabled = false;
                    var result = await Task.Run(() => ManagerConflicts.Close(running));
                    closed.Add(result);
                    line.Text = result.Exited ? $"{manager.Label}: closed." : $"{manager.Label}: {result.Detail}";
                });
                row.Children.Add(close);
            }
            else
            {
                row.Children.Add(Muted("(a service; stop it yourself if the test misbehaves)"));
            }

            panel.Children.Add(row);
        }

        return panel;
    }

    private async Task PawnIoAsync(StackPanel page, Dictionary<string, object?> evidence)
    {
        if (!_options.Elevated)
        {
            page.Children.Add(Status("Skipped: installing PawnIO needs administrator rights."));
            return;
        }

        var status = await Task.Run(_pawnIo.Detect);
        var action = PawnIoSetup.Decide(status, PawnIoSetup.Pin, PawnIoSetup.InstallerBundled);
        evidence["pawnIo"] = new
        {
            status.InstalledVersion,
            status.DeviceOpened,
            status.DeviceError,
            Action = action.ToString()
        };
        switch (action)
        {
            case PawnIoAction.None:
                page.Children.Add(Status($"PawnIO {status.InstalledVersion} is installed and running."));
                break;
            case PawnIoAction.Install:
                page.Children.Add(
                    Status($"Installing PawnIO {PawnIoSetup.Pin.Version}, which the power and fan checks need..."));
                var installed = await _pawnIo.InstallAsync(_lifetime.Token);
                evidence["pawnIoInstall"] = installed;
                page.Children.Add(Outcome(installed));
                break;
            case PawnIoAction.AskToReplace:
                StackPanel choice = new() { Spacing = 6 };
                choice.Children.Add(Status(
                    $"PawnIO {status.InstalledVersion} is installed but too old. Replace it with {PawnIoSetup.Pin.Version}? Other programs that use PawnIO keep working with the new version, and Device Lab will not remove it afterwards."));
                choice.Children.Add(Buttons(
                    Action("Replace it", () => Run(choice, async () =>
                    {
                        var replaced = await _pawnIo.ReplaceAsync(status, _lifetime.Token);
                        evidence["pawnIoReplace"] = replaced;
                        choice.Children.Add(Outcome(replaced));
                    })),
                    Action("Leave it",
                        () => choice.Children.Add(
                            Status("Left as it is; the power and fan checks will be skipped.")))));
                page.Children.Add(choice);
                break;
            case PawnIoAction.ReportNotRunning:
                page.Children.Add(Warning(
                    $"PawnIO {status.InstalledVersion} is installed but its driver does not answer (error {status.DeviceError}). Windows may be blocking it; the power and fan checks will be skipped."));
                break;
            case PawnIoAction.Unavailable:
                page.Children.Add(Status(
                    "This build does not include the PawnIO installer; the power and fan checks will be skipped."));
                break;
        }
    }

    private static TextBlock Outcome(LabPawnIoOutcome outcome)
    {
        if (outcome.Running)
        {
            return Status($"PawnIO {outcome.After.InstalledVersion} is installed and running.");
        }

        var problem = outcome.Problem ?? $"its driver does not answer (error {outcome.After.DeviceError})";
        return Warning(outcome.LostPrevious
            ? $"Your earlier PawnIO was removed, but the new one could not be installed: {problem}. Reinstall PawnIO from the program that brought it, or from github.com/namazso/PawnIO.Setup."
            : $"PawnIO is not available: {problem}. The power and fan checks will be skipped.");
    }

    private async Task RunIdentityAsync(LabProject project, StackPanel page)
    {
        page.Children.Add(Status("Reading the board, BIOS and firmware versions..."));
        var attempt = await Task.Run(() => project.BeginAttempt(LabStages.Identity, DateTimeOffset.UtcNow));
        var observed = await Task.Run(() => LabIdentity.Observe(DeviceKnowledgeBase.Default, _lifetime.Token));
        await Task.Run(() => DurableFile.WriteNewText(
            Path.Combine(attempt, "inventory.json"),
            DeviceLabJson.Serialize(observed.Inventory) + "\n"));

        page.Children.Clear();
        page.Children.Add(PageTitle("Your device"));
        page.Children.Add(Status("This is what the device reports about itself. No serial numbers are collected."));
        page.Children.Add(Facts(observed.Facts));

        List<RadioButton> choices = [];
        page.Children.Add(Heading(observed.Matches.Count > 0
            ? "Is this your device?"
            : "This device is not in the list yet"));
        foreach (var match in observed.Matches)
        {
            choices.Add(new RadioButton
            {
                GroupName = "device",
                Tag = match,
                Content = match.Fallback ? $"{match.DisplayName} (close match)" : match.DisplayName,
                IsChecked = choices.Count == 0
            });
        }

        RadioButton other = new()
        {
            GroupName = "device",
            Content = "None of these",
            IsChecked = observed.Matches.Count == 0
        };
        choices.Add(other);
        foreach (var choice in choices)
        {
            page.Children.Add(choice);
        }

        TextBox product = new()
        {
            PlaceholderText = "Product name, for example ROG Ally X",
            Width = 420,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        TextBox model = new()
        {
            PlaceholderText = "Exact model from the label or box, for example RC72LA",
            Width = 420,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        StackPanel manual = new() { Spacing = 6, IsVisible = other.IsChecked == true };
        manual.Children.Add(product);
        manual.Children.Add(model);
        page.Children.Add(manual);
        other.IsCheckedChanged += (_, _) => manual.IsVisible = other.IsChecked == true;
        var problem = Muted(string.Empty);
        page.Children.Add(problem);

        page.Children.Add(Buttons(Action("Continue", () =>
        {
            var selected = choices.FirstOrDefault(choice => choice.IsChecked == true)?.Tag as DeviceKnowledgeMatch;
            if (selected is null && (string.IsNullOrWhiteSpace(product.Text) || string.IsNullOrWhiteSpace(model.Text)))
            {
                problem.Text = "Enter the product name and the exact model.";
                return;
            }

            var device = selected is not null
                ? new LabDeviceIdentity { RecordId = selected.RecordId, DisplayName = selected.DisplayName }
                : new LabDeviceIdentity { ProductName = product.Text!.Trim(), Model = model.Text!.Trim() };
            Run(page, async () =>
            {
                await Task.Run(() =>
                {
                    project.SetDevice(device);
                    project.WriteEvidence(attempt, "identity",
                        new { observed.Facts, observed.Matches, Confirmed = device });
                    project.Finish(LabStages.Identity, LabSegmentStatus.Completed,
                        device.DisplayName ?? $"{device.ProductName} ({device.Model})", DateTimeOffset.UtcNow);
                });
                Next(LabStages.Identity);
            });
        })));
    }

    private async Task ShowFinishAsync(LabProject project, StackPanel page)
    {
        page.Children.Clear();
        page.Children.Add(PageTitle("Finish and share"));
        page.Children.Add(Status(
            "Check what will be sent. Serial numbers, account names, user folders and network addresses are replaced with placeholders."));
        if (await Task.Run(RestoreHidHide) is { } restore)
        {
            page.Children.Add(Warning(
                $"The HidHide entry could not be removed: {restore}. Remove Device Lab from HidHide's allowed programs yourself."));
        }

        var export = await Task.Run(() => LabExport.Prepare(project));
        var preview = export.Preview;
        var steps = LabStages.All.Select(stage => project.Segment(stage.Id)).ToList();
        page.Children.Add(Heading("Your test"));
        page.Children.Add(Status(
            $"{steps.Count(step => step.Status is LabSegmentStatus.Completed)} steps done, " +
            $"{steps.Count(step => step.Status is LabSegmentStatus.Skipped)} skipped, " +
            $"{steps.Count(step => step.Status is LabSegmentStatus.Failed)} failed, " +
            $"{steps.Count(step => step.Status is LabSegmentStatus.NotStarted)} not run."));
        if (await Task.Run(() => _machine.Read().Power) is not null)
        {
            page.Children.Add(Warning(
                "A power or fan setting from this test is still recorded as not put back. Restart Device Lab to restore it, or restart the device."));
        }
        page.Children.Add(Heading($"{preview.Files.Count} files, {preview.Files.Sum(file => file.Bytes) / 1024} KiB"));
        page.Children.Add(Muted(string.Join(Environment.NewLine,
            preview.Files.Select(file => $"{file.Path}  ({file.Bytes} bytes)"))));
        if (preview.Excluded.Count > 0)
        {
            page.Children.Add(Heading("Kept on this computer"));
            page.Children.Add(Muted(string.Join(Environment.NewLine, preview.Excluded)));
        }

        page.Children.Add(Heading("Replaced before sharing"));
        page.Children.Add(Muted(preview.Redactions.Count == 0
            ? "Nothing identifying was found."
            : string.Join(Environment.NewLine,
                preview.Redactions.Select(item => $"{item.Category}: {item.Occurrences}"))));
        page.Children.Add(Heading("Test summary"));
        page.Children.Add(new TextBox
        {
            Text = preview.ProjectManifest,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Consolas, monospace"),
            MaxHeight = 260
        });
        var saved = Muted(string.Empty);
        page.Children.Add(Buttons(Action("Save the report", () => Run(page, () => SaveReportAsync(export, saved)))));
        page.Children.Add(saved);

        if (_options.Elevated && await Task.Run(_pawnIo.CanOfferRemoval))
        {
            page.Children.Add(Heading("PawnIO"));
            page.Children.Add(Status(
                "Device Lab installed the PawnIO driver for this test. You can keep it (other tools use it too) or remove it."));
            var removed = Muted(string.Empty);
            page.Children.Add(Buttons(Action("Remove PawnIO", () => Run(page, async () =>
            {
                var problem = await _pawnIo.RemoveAsync(_lifetime.Token);
                removed.Text = problem is null ? "PawnIO was removed." : $"PawnIO was not removed: {problem}";
            }))));
            page.Children.Add(removed);
        }
    }

    private async Task SaveReportAsync(LabExport export, TextBlock saved)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save the Device Lab report",
            SuggestedFileName = $"Device Lab report {DateTime.Now:yyyy-MM-dd HHmm}.wsgmlab",
            DefaultExtension = "wsgmlab",
            FileTypeChoices = [new FilePickerFileType("Device Lab report") { Patterns = ["*.wsgmlab"] }],
            SuggestedStartLocation = await StorageProvider.TryGetWellKnownFolderAsync(WellKnownFolder.Desktop)
        });
        if (file?.Path.LocalPath is not { } path)
        {
            return;
        }

        await Task.Run(() => export.Write(path, Boundaries()));
        saved.Text = $"Saved to {path}. Send this file back.";
        if (saved.Parent is Panel panel)
        {
            panel.Children.Add(Buttons(Action("Show the report in its folder", () => OpenInExplorer("/select," + Quote(path)))));
        }
    }

    private string? RestoreHidHide()
    {
        return _machine.Read().HidHideEntry is null
            ? null
            : HidHideAllowance.ForMachine(_machine, DeviceLabExecutable.CurrentPath).RestoreRecorded();
    }

    // Runs one operation at a time. The stage list is locked while it runs, and any failure that is not
    // the window closing is shown on the page instead of ending the process or leaving it half drawn.
    // An operation that hands over to the next stage passes chained, because it is itself still running.
    private void Run(Panel? errors, Func<Task> work, bool chained = false)
    {
        if (_closing || (!chained && !_operation.IsCompleted))
        {
            return;
        }

        _stages.IsEnabled = false;
        if (!chained)
        {
            _stage.Dispose();
            _stage = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        }

        var operation = RunCore(errors, work);
        if (!chained)
        {
            _operation = operation;
        }
        else
        {
            var outer = _operation;
            _operation = Task.WhenAll(outer, operation);
        }
    }

    private async Task RunCore(Panel? errors, Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // The window is closing.
        }
        catch (OperationCanceledException) when (_stage.IsCancellationRequested)
        {
            // "Stop and save": everything recorded so far stays; the interrupted step is marked not done.
            var page = Page("Stopped",
                "Everything recorded so far is saved. Pick any step in the list to continue or redo it.");
            page.Children.Add(Buttons(Action("Go to Finish and share", () => ShowStage(FinishId))));
            _page.Content = page;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (errors is not null)
            {
                errors.Children.Add(Warning(
                    $"Something went wrong: {ex.Message} Nothing is retried automatically; what was recorded is saved."));
                if (_running is { } stage && _project is not null)
                {
                    errors.Children.Add(Buttons(
                        Action("Continue with the next step", () =>
                        {
                            var next = LabStages.All.SkipWhile(item => item.Id != stage).Skip(1).FirstOrDefault()?.Id;
                            ShowStage(next ?? FinishId);
                        }),
                        Action("Go to Finish and share", () => ShowStage(FinishId))));
                }
            }
            else
            {
                var page = Page("Something went wrong", ex.Message);
                page.Children.Add(Buttons(Action("Back", ShowWelcome)));
                _page.Content = page;
            }
        }
        finally
        {
            _stages.IsEnabled = _project is not null && !_closing;
        }
    }

    // A power, fan or charge setting that could not be confirmed as put back makes closing a choice.
    private async Task ConfirmCloseAsync()
    {
        _confirmingClose = true;
        try
        {
            if (PowerRestorationPending())
            {
                TaskCompletionSource<bool> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
                Window dialog = new()
                {
                    Title = "Settings not confirmed as restored",
                    Width = 520,
                    SizeToContent = SizeToContent.Height,
                    CanResize = false
                };
                StackPanel body = new() { Margin = new Thickness(18), Spacing = 12 };
                body.Children.Add(Status(
                    "A power or fan setting from this test is not confirmed as put back. Close without confirming restoration? Device Lab tries again the next time it starts."));
                body.Children.Add(Buttons(
                    Action("Close anyway", () =>
                    {
                        answer.TrySetResult(true);
                        dialog.Close();
                    }),
                    Action("Stay open", () =>
                    {
                        answer.TrySetResult(false);
                        dialog.Close();
                    })));
                dialog.Content = body;
                dialog.Closed += (_, _) => answer.TrySetResult(false);
                await dialog.ShowDialog(this);
                if (!await answer.Task)
                {
                    return;
                }
            }

            _closing = true;
            await CloseAfterCleanupAsync();
        }
        finally
        {
            _confirmingClose = false;
        }
    }

    private async Task CloseAfterCleanupAsync()
    {
        await _lifetime.CancelAsync();
        try
        {
            await _operation;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // RunCore reports failures itself; closing only needs the operation to have stopped.
        }

        try
        {
            await Task.Run(RestoreHidHide);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // The record stays, so the next start removes the entry.
        }

        _capture?.Dispose();
        _capture = null;
        _owner?.Dispose();
        _closeReady = true;
        Close();
    }

    private void StopStage()
    {
        if (!_operation.IsCompleted)
        {
            _stage.Cancel();
        }
    }

    // A note is its own attempt of the "notes" segment, so notes never overwrite each other.
    private void SaveNote()
    {
        if (_project is not { } project || string.IsNullOrWhiteSpace(_note.Text))
        {
            return;
        }

        var text = _note.Text.Trim();
        var stage = _running;
        _ = Task.Run(() =>
        {
            var directory = project.BeginAttempt("notes", DateTimeOffset.UtcNow);
            project.WriteEvidence(directory, "note", new { Stage = stage, Text = text, At = DateTimeOffset.UtcNow });
            project.Finish("notes", LabSegmentStatus.Completed, "Tester notes", DateTimeOffset.UtcNow);
        }).ContinueWith(task => OnUi(() => _note.Text = task.IsFaulted
            ? $"The note could not be saved: {task.Exception?.GetBaseException().Message}"
            : string.Empty), TaskScheduler.Default);
    }

    private void OpenProjectFolder()
    {
        if (_project is { } project)
        {
            OpenInExplorer(Quote(project.Directory));
        }
    }

    private static void OpenInExplorer(string arguments)
    {
        System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = false });
    }

    private static string Quote(string path)
    {
        return '"' + path + '"';
    }

    private void ShowHelp()
    {
        Window help = new()
        {
            Title = "Device Lab: help and licences",
            Width = 820,
            Height = 640,
            Content = new TextBox
            {
                Text = WizardHelp.Text(),
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Consolas, monospace")
            }
        };
        help.Show(this);
    }

    private static DeviceLabPathBoundaries Boundaries()
    {
        return DeviceLabPathBoundaries.ForCurrentUser(DeviceLabRepositoryLocator.Find(Environment.CurrentDirectory));
    }

    private static string? ToolSha256()
    {
        try
        {
            using var stream = File.OpenRead(DeviceLabExecutable.CurrentPath);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? SourceRevision()
    {
        var informational = typeof(WizardWindow).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion;
        var plus = informational?.IndexOf('+') ?? -1;
        return plus >= 0 ? informational![(plus + 1)..] : null;
    }

    private static string ToolVersion()
    {
        return typeof(WizardWindow).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    private static Grid Facts(LabIdentityFacts facts)
    {
        Grid grid = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 16, RowSpacing = 4 };
        (string Label, string? Value)[] rows =
        [
            ("Board maker", facts.BoardManufacturer),
            ("Board", facts.BoardName),
            ("Board version", facts.BoardVersion),
            ("Model", facts.SystemModel),
            ("BIOS", facts.BiosVersion),
            ("Embedded controller", facts.EmbeddedControllerVersion),
            ("Processor", facts.Processor),
            ("Processor family", facts.ProcessorIdentity),
            ("Controller firmware",
                facts.ControllerFirmware.Count == 0 ? null : string.Join(", ", facts.ControllerFirmware))
        ];
        for (var i = 0; i < rows.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var label = Muted(rows[i].Label);
            var value = new TextBlock { Text = rows[i].Value ?? "(not reported)", TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(label, i);
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            grid.Children.Add(label);
            grid.Children.Add(value);
        }

        return grid;
    }

    private static StackPanel Page(string title, string description)
    {
        StackPanel page = new() { Spacing = 10, MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };
        page.Children.Add(PageTitle(title));
        if (description.Length > 0)
        {
            page.Children.Add(Status(description));
        }

        return page;
    }

    private static TextBlock PageTitle(string text)
    {
        return new TextBlock { Text = text, FontSize = 22, FontWeight = FontWeight.SemiBold };
    }

    private static TextBlock Heading(string text)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 8, 0, 0)
        };
    }

    private static TextBlock Status(string text)
    {
        return new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
    }

    private static TextBlock Warning(string text)
    {
        return new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Orange };
    }

    private static TextBlock Muted(string text)
    {
        return new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Silver };
    }

    private static Button Action(string text, Action onClick)
    {
        Button button = new() { Content = text };
        button.Click += (_, _) => onClick();
        return button;
    }

    private static StackPanel Buttons(params Button[] buttons)
    {
        StackPanel row = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Margin = new Thickness(0, 10, 0, 0)
        };
        foreach (var button in buttons)
        {
            row.Children.Add(button);
        }

        return row;
    }
}
