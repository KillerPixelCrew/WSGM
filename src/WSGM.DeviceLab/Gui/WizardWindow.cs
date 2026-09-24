using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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
///     tester. Every blocking call runs off the UI thread, and only one runs at a time.
/// </remarks>
internal sealed class WizardWindow : Window
{
    private const string FinishId = "finish";
    private readonly CancellationTokenSource _lifetime = new();

    private readonly LabMachineState _machine = LabMachineState.ForCurrentUser;
    private readonly WizardOptions _options;
    private readonly ContentControl _page = new();
    private readonly TextBlock _projectLine = Muted(string.Empty);
    private readonly ListBox _stages = new() { MinWidth = 240 };
    private bool _busy;
    private LabProject? _project;
    private bool _refreshing;

    public WizardWindow(WizardOptions options)
    {
        _options = options;
        Title = "WSGM Device Lab";
        Width = 1100;
        Height = 760;
        MinWidth = 820;
        MinHeight = 560;

        Grid root = new()
            { Margin = new Thickness(18), ColumnDefinitions = new ColumnDefinitions("260,*"), ColumnSpacing = 18 };
        StackPanel side = new() { Spacing = 10 };
        side.Children.Add(new TextBlock { Text = "Device Lab", FontSize = 24, FontWeight = FontWeight.SemiBold });
        side.Children.Add(_projectLine);
        side.Children.Add(_stages);
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
        Closing += (_, _) =>
        {
            _lifetime.Cancel();
            RestoreHidHide();
        };
        Opened += (_, _) => Start();
    }

    private void Start()
    {
        _stages.IsEnabled = false;
        if (_options.ProjectPath is { } path)
        {
            TryOpen(path);
            return;
        }

        ShowWelcome();
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
            Action("Start a new test", CreateProject),
            Action("Continue a saved test", () => _ = OpenProjectAsync())));
        _page.Content = page;
    }

    private void CreateProject()
    {
        var parent = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "WSGM Device Lab");
        var directory = Path.Combine(parent, $"Device test {DateTime.Now:yyyy-MM-dd HHmmss}");
        var decision = DeviceLabOutputPathPolicy.Evaluate(directory, DeviceLabOutputTargetKind.Directory, Boundaries());
        if (!decision.IsAllowed)
        {
            ShowError("The test folder could not be created", decision.Reason ?? directory);
            return;
        }

        try
        {
            Directory.CreateDirectory(parent);
            Load(LabProject.Create(decision.FullPath!, LabStages.Ids, ToolVersion(), DateTimeOffset.UtcNow));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError("The test folder could not be created", ex.Message);
        }
    }

    private async Task OpenProjectAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select a saved Device Lab test", AllowMultiple = false
        });
        if (folders.Count > 0)
        {
            TryOpen(folders[0].Path.LocalPath);
        }
    }

    private void TryOpen(string path)
    {
        try
        {
            Load(LabProject.Open(path));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            ShowError("That folder is not a Device Lab test", ex.Message);
        }
    }

    private void Load(LabProject project)
    {
        _project = project;
        _projectLine.Text = project.Directory;
        RefreshStages();
        _stages.IsEnabled = true;
        var next = LabStages.All.FirstOrDefault(stage =>
                stage.Available && project.Segment(stage.Id).Status is LabSegmentStatus.NotStarted)
            ?.Id ?? FinishId;
        ShowStage(next);
    }

    private void RefreshStages(string? selected = null)
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
                var mark = !stage.Available
                    ? "later"
                    : state.Status switch
                    {
                        LabSegmentStatus.Completed => "done",
                        LabSegmentStatus.Skipped => "skipped",
                        LabSegmentStatus.Failed => "failed",
                        _ => string.Empty
                    };
                items.Add(new ListBoxItem
                {
                    Tag = stage.Id,
                    Content = mark.Length == 0 ? stage.Title : $"{stage.Title}  ({mark})",
                    Foreground = stage.Available ? null : Brushes.Gray
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

    private void ShowStage(string id)
    {
        if (_project is null || _busy)
        {
            return;
        }

        RefreshStages(id);
        switch (id)
        {
            case LabStages.Preflight:
                _ = RunPreflightAsync(_project);
                break;
            case LabStages.Identity:
                _ = RunIdentityAsync(_project);
                break;
            case FinishId:
                _ = ShowFinishAsync(_project);
                break;
            default:
                var stage = LabStages.All.Single(item => item.Id == id);
                _page.Content = Page(stage.Title,
                    stage.Description +
                    " This part is not in this build of Device Lab yet; skip ahead to Finish and share.");
                break;
        }
    }

    private async Task RunPreflightAsync(LabProject project)
    {
        var page = Page("Get ready",
            "These checks make sure nothing else hides or changes your controller while the test runs.");
        _page.Content = page;
        var attempt = project.BeginAttempt(LabStages.Preflight, DateTimeOffset.UtcNow);
        var allowance = HidHideAllowance.ForMachine(_machine, DeviceLabExecutable.CurrentPath);
        Dictionary<string, object?> evidence = new() { ["elevated"] = _options.Elevated };

        await Busy(async () =>
        {
            // 1. Undo what an earlier session left behind.
            var leftover = _machine.Read().HidHideEntry;
            if (leftover is not null)
            {
                var problem = await Task.Run(allowance.RestoreRecorded);
                evidence["recoveredHidHideEntry"] = problem ?? "removed";
                page.Children.Add(Status(problem is null
                    ? "Removed the HidHide entry an earlier session left behind."
                    : $"An earlier session left a HidHide entry that could not be removed: {problem}"));
            }

            // 2. Other managers.
            var managers = await Task.Run(ManagerConflicts.Running);
            evidence["managers"] = managers;
            evidence["drivers"] = ManagerConflicts.Drivers();
            page.Children.Add(Heading("Other controller software"));
            page.Children.Add(ManagersPanel(managers, evidence));

            // 3. HidHide.
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
                    var allowed = await Task.Run(allowance.TryAllow);
                    evidence["hidHideAllow"] = allowed;
                    page.Children.Add(Status(allowed.Added is not null
                        ? "This tool was added to HidHide's allowed programs for the test. It is removed again when you finish or close the window."
                        : allowed.Reason ?? "Nothing to change."));
                }
            }

            // 4. PawnIO.
            page.Children.Add(Heading("PawnIO driver"));
            await PawnIoAsync(project, page, evidence);
        });

        page.Children.Add(Buttons(
            Action("Check again", () => ShowStage(LabStages.Preflight)),
            Action("Continue", () =>
            {
                project.WriteEvidence(attempt, "preflight", evidence);
                project.Finish(LabStages.Preflight, LabSegmentStatus.Completed, null, DateTimeOffset.UtcNow);
                ShowStage(LabStages.Identity);
            })));
    }

    private Control ManagersPanel(IReadOnlyList<RunningManager> managers, Dictionary<string, object?> evidence)
    {
        StackPanel panel = new() { Spacing = 6 };
        if (managers.Count == 0)
        {
            panel.Children.Add(Status("None running."));
            return panel;
        }

        List<ManagerCloseResult> closed = [];
        evidence["closeRequests"] = closed;
        foreach (var running in managers)
        {
            var manager = running.Manager;
            StackPanel row = new() { Orientation = Orientation.Horizontal, Spacing = 10 };
            var line = Status($"{manager.Label}: {manager.Why}.");
            row.Children.Add(line);
            if (manager.Closable)
            {
                Button close = new() { Content = "Close it" };
                close.Click += async (_, _) =>
                {
                    close.IsEnabled = false;
                    var result = await Task.Run(() => ManagerConflicts.Close(running));
                    closed.Add(result);
                    line.Text = result.Exited ? $"{manager.Label}: closed." : $"{manager.Label}: {result.Detail}";
                };
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

    private async Task PawnIoAsync(LabProject project, StackPanel page, Dictionary<string, object?> evidence)
    {
        if (!_options.Elevated)
        {
            page.Children.Add(Status("Skipped: installing PawnIO needs administrator rights."));
            return;
        }

        var status = await Task.Run(PawnIoSetup.Detect);
        var action = PawnIoSetup.Decide(status, PawnIoSetup.Pin, PawnIoSetup.InstallerBundled);
        evidence["pawnIo"] = new
            { status.InstalledVersion, status.DeviceOpened, status.DeviceError, Action = action.ToString() };
        switch (action)
        {
            case PawnIoAction.None:
                page.Children.Add(Status($"PawnIO {status.InstalledVersion} is installed and running."));
                break;
            case PawnIoAction.Install:
                page.Children.Add(
                    Status($"Installing PawnIO {PawnIoSetup.Pin.Version}, which the power and fan checks need..."));
                await InstallPawnIoAsync(project, page, evidence);
                break;
            case PawnIoAction.AskToReplace:
                StackPanel choice = new() { Spacing = 6 };
                choice.Children.Add(Status(
                    $"PawnIO {status.InstalledVersion} is installed but too old. Replace it with {PawnIoSetup.Pin.Version}? Other programs that use PawnIO keep working with the new version."));
                choice.Children.Add(Buttons(
                    Action("Replace it", () => _ = ReplacePawnIoAsync(project, status, choice, evidence)),
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

    private async Task ReplacePawnIoAsync(LabProject project, PawnIoStatus status, StackPanel choice,
        Dictionary<string, object?> evidence)
    {
        await Busy(async () =>
        {
            var problem = await PawnIoSetup.UninstallAsync(status, _lifetime.Token);
            evidence["pawnIoUninstall"] = problem ?? "ok";
            if (problem is not null)
            {
                choice.Children.Add(Warning($"The old PawnIO could not be removed: {problem}"));
                return;
            }

            await InstallPawnIoAsync(project, choice, evidence);
        });
    }

    private async Task InstallPawnIoAsync(LabProject project, StackPanel page, Dictionary<string, object?> evidence)
    {
        _machine.Update(changes => changes with { PawnIoInstalledByLab = true });
        var problem = await PawnIoSetup.InstallAsync(_lifetime.Token);
        var after = await Task.Run(PawnIoSetup.Detect);
        var installed = problem is null && after.InstalledVersion is not null;
        if (installed)
        {
            project.SetPawnIoInstalledByLab(true);
        }
        else
        {
            _machine.Update(changes => changes with { PawnIoInstalledByLab = false });
        }

        evidence["pawnIoInstall"] = new { Problem = problem, after.InstalledVersion, after.DeviceOpened };
        page.Children.Add(installed && after.DeviceOpened
            ? Status($"PawnIO {after.InstalledVersion} installed and running.")
            : Warning(
                $"PawnIO could not be installed: {problem ?? $"its driver does not answer (error {after.DeviceError})"}. The power and fan checks will be skipped."));
    }

    private async Task RunIdentityAsync(LabProject project)
    {
        var page = Page("Your device", "Reading the board, BIOS and firmware versions...");
        _page.Content = page;
        var attempt = project.BeginAttempt(LabStages.Identity, DateTimeOffset.UtcNow);
        LabIdentityObservation? observed = null;
        await Busy(async () =>
        {
            observed = await Task.Run(() => LabIdentity.Observe(DeviceKnowledgeBase.Default, _lifetime.Token));
        });
        if (observed is null)
        {
            project.Finish(LabStages.Identity, LabSegmentStatus.Failed, "The device could not be read.",
                DateTimeOffset.UtcNow);
            RefreshStages(LabStages.Identity);
            return;
        }

        DurableFile.WriteNewText(Path.Combine(attempt, "inventory.json"),
            DeviceLabJson.Serialize(observed.Inventory) + "\n");
        page = Page("Your device", "This is what the device reports about itself. No serial numbers are collected.");
        _page.Content = page;
        page.Children.Add(Facts(observed.Facts));

        List<RadioButton> choices = [];
        if (observed.Matches.Count > 0)
        {
            page.Children.Add(Heading("Is this your device?"));
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
        }
        else
        {
            page.Children.Add(Heading("This device is not in the list yet"));
        }

        RadioButton other = new()
            { GroupName = "device", Content = "None of these", IsChecked = observed.Matches.Count == 0 };
        choices.Add(other);
        foreach (var choice in choices)
        {
            page.Children.Add(choice);
        }

        TextBox product = new()
        {
            PlaceholderText = "Product name, for example ROG Ally X", Width = 420,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        TextBox model = new()
        {
            PlaceholderText = "Exact model from the label or box, for example RC72LA", Width = 420,
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
            project.SetDevice(device);
            project.WriteEvidence(attempt, "identity", new { observed.Facts, observed.Matches, Confirmed = device });
            project.Finish(LabStages.Identity, LabSegmentStatus.Completed,
                device.DisplayName ?? $"{device.ProductName} ({device.Model})", DateTimeOffset.UtcNow);
            var next = LabStages.All.SkipWhile(stage => stage.Id != LabStages.Identity).Skip(1)
                .FirstOrDefault(stage => stage.Available)?.Id ?? FinishId;
            ShowStage(next);
        })));
    }

    private async Task ShowFinishAsync(LabProject project)
    {
        var page = Page("Finish and share",
            "Check what will be sent. Serial numbers, account names, user folders and network addresses are replaced with placeholders.");
        _page.Content = page;
        var restore = RestoreHidHide();
        if (restore is not null)
        {
            page.Children.Add(Warning(
                $"The HidHide entry could not be removed: {restore}. Remove Device Lab from HidHide's allowed programs yourself."));
        }

        LabExport? export = null;
        string? failure = null;
        await Busy(async () =>
        {
            try
            {
                export = await Task.Run(() => LabExport.Prepare(project));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException
                                           or UnauthorizedAccessException)
            {
                failure = ex.Message;
            }
        });
        if (export is null)
        {
            page.Children.Add(Warning($"The report could not be prepared: {failure}"));
            return;
        }

        var preview = export.Preview;
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
            Text = preview.ProjectManifest, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Consolas, monospace"), MaxHeight = 260
        });
        var saved = Muted(string.Empty);
        page.Children.Add(Buttons(Action("Save the report", () => _ = SaveReportAsync(export, saved))));
        page.Children.Add(saved);

        if (project.Manifest.PawnIoInstalledByLab)
        {
            page.Children.Add(Heading("PawnIO"));
            page.Children.Add(Status(
                "Device Lab installed the PawnIO driver for this test. You can keep it (other tools use it too) or remove it."));
            var removed = Muted(string.Empty);
            page.Children.Add(Buttons(Action("Remove PawnIO", () => _ = RemovePawnIoAsync(project, removed))));
            page.Children.Add(removed);
        }
    }

    private async Task SaveReportAsync(LabExport export, TextBlock saved)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save the Device Lab report",
            SuggestedFileName = $"Device Lab report {DateTime.Now:yyyy-MM-dd HHmm}.zip",
            DefaultExtension = "zip",
            SuggestedStartLocation = await StorageProvider.TryGetWellKnownFolderAsync(WellKnownFolder.Desktop)
        });
        if (file?.Path.LocalPath is not { } path)
        {
            return;
        }

        try
        {
            await Task.Run(() => export.Write(path, Boundaries()));
            saved.Text = $"Saved to {path}. Send this file back.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            saved.Text = $"The report was not saved: {ex.Message}";
        }
    }

    private async Task RemovePawnIoAsync(LabProject project, TextBlock removed)
    {
        await Busy(async () =>
        {
            var status = await Task.Run(PawnIoSetup.Detect);
            var problem = await PawnIoSetup.UninstallAsync(status, _lifetime.Token);
            if (problem is null)
            {
                project.SetPawnIoInstalledByLab(false);
                _machine.Update(changes => changes with { PawnIoInstalledByLab = false });
            }

            removed.Text = problem is null ? "PawnIO was removed." : $"PawnIO was not removed: {problem}";
        });
    }

    private string? RestoreHidHide()
    {
        if (_machine.Read().HidHideEntry is null)
        {
            return null;
        }

        try
        {
            return HidHideAllowance.ForMachine(_machine, DeviceLabExecutable.CurrentPath).RestoreRecorded();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return ex.Message;
        }
    }

    private async Task Busy(Func<Task> work)
    {
        _busy = true;
        _stages.IsEnabled = false;
        try
        {
            await work();
        }
        catch (OperationCanceledException)
        {
            // The window is closing.
        }
        finally
        {
            _busy = false;
            _stages.IsEnabled = _project is not null;
        }
    }

    private void ShowError(string title, string detail)
    {
        var page = Page(title, detail);
        page.Children.Add(Buttons(Action("Back", ShowWelcome)));
        _page.Content = page;
    }

    private static DeviceLabPathBoundaries Boundaries()
    {
        return DeviceLabPathBoundaries.ForCurrentUser(DeviceLabRepositoryLocator.Find(Environment.CurrentDirectory));
    }

    private static string ToolVersion()
    {
        return typeof(WizardWindow).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    private static Control Facts(LabIdentityFacts facts)
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
            ("Processor", facts.Processor)
        ];
        for (var i = 0; i < rows.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var label = Muted(rows[i].Label);
            var value = new TextBlock { Text = rows[i].Value ?? "(not reported)" };
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
        page.Children.Add(new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeight.SemiBold });
        page.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap });
        return page;
    }

    private static TextBlock Heading(string text)
    {
        return new TextBlock
            { Text = text, FontSize = 16, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) };
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
            { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 10, 0, 0) };
        foreach (var button in buttons)
        {
            row.Children.Add(button);
        }

        return row;
    }
}
