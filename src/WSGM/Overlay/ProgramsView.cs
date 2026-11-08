using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Input;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>Controller/touch Programs editor; the session service owns every durable operation.</summary>
public sealed class ProgramsView : ServiceSubView
{
    /// <summary>How many application icons are read at a time, so rows fill in while the rest load.</summary>
    private const int IconBatchSize = 40;

    private readonly Dictionary<string, Bitmap?> _pickerIcons = new(StringComparer.Ordinal);
    private readonly Action _pickerLevel;
    private bool _adding;
    private HashSet<string>? _pins;
    private InstalledProgramList? _installed;
    private CancellationTokenSource? _pickerWork;
    private string _pickerQuery = "";
    private IProgramsSource? _service;

    /// <summary>Creates the view.</summary>
    public ProgramsView()
    {
        _pickerLevel = RenderPicker;
    }

    /// <inheritdoc />
    protected override string LogScope => "Programs";

    /// <summary>
    ///     Raised with a chosen program. The controller launches or focuses it, or opens its editor here when it
    ///     can do neither.
    /// </summary>
    internal event Action<string>? ActivateRequested;

    /// <summary>Raised with a program to pin to Quick access.</summary>
    internal event Action<string>? PinRequested;

    /// <summary>Raised with a program to unpin from Quick access.</summary>
    internal event Action<string>? UnpinRequested;

    /// <summary>Raised on the service's thread when the programs change, so their pinned rows follow.</summary>
    internal event Action? ProgramsChanged;

    /// <summary>Follows the session's Programs owner, or none with null.</summary>
    /// <param name="service">The owner, or null when the overlay closes or the session has none.</param>
    internal void Attach(IProgramsSource? service)
    {
        if (_service is not null)
        {
            _service.Changed -= OnProgramsChanged;
        }

        _service = service;
        if (service is not null)
        {
            service.Changed += OnProgramsChanged;
        }

        AttachSource(IsEffectivelyVisible ? service : null);
    }

    /// <summary>Takes the Quick access pin list, so each program's pin button follows it at once.</summary>
    /// <param name="ids">The pinned ids.</param>
    internal void SetPins(IReadOnlyList<string> ids)
    {
        _pins = ids.ToHashSet(StringComparer.Ordinal);
        if (IsEffectivelyVisible && !ReferenceEquals(CurrentLevel, _pickerLevel))
        {
            CurrentLevel?.Invoke();
        }
    }

    /// <summary>Reports a chosen program to the controller, which launches, focuses or explains it.</summary>
    /// <param name="id">The program.</param>
    internal void Activate(string id)
    {
        ActivateRequested?.Invoke(id);
    }

    /// <summary>Resolves a pin id that names a program.</summary>
    /// <param name="pin">The pin id.</param>
    /// <returns>The program's Quick access row, or null for an id this source does not own.</returns>
    internal QuickAccessPin? ResolvePin(string pin)
    {
        if (!pin.StartsWith(ProgramsService.PinPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var id = pin[ProgramsService.PinPrefix.Length..];
        var tag = QuickAccessPins.TagPrefix + pin;
        var state = _service?.ReadState().FirstOrDefault(item => item.Definition.Id == id);
        if (state is null)
        {
            return new QuickAccessPin("Program unavailable", false, false, _ => new ActionButton
            {
                Tag = tag,
                Title = "Program unavailable",
                Description = "X, touch hold or right-click to unpin",
                IconGeometry = Icons.Rocket
            });
        }

        return new QuickAccessPin(state.Definition.Name, true, false, drawn =>
        {
            if (drawn is ProgramActionRow existing)
            {
                existing.Apply(state);
                return existing;
            }

            return new ProgramActionRow(state, () => Activate(id)) { Tag = tag };
        });
    }

    private void OnProgramsChanged()
    {
        ProgramsChanged?.Invoke();
    }

    internal void OpenEditor(string id)
    {
        Open();
        Navigate(() => RenderEditor(id));
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty)
        {
            AttachSource(IsVisible ? _service : null);
        }
    }

    /// <inheritdoc />
    private protected override void RenderHome()
    {
        var body = NewStack("Programs");
        if (_service is not { } service)
        {
            body.Children.Add(Caption("Programs are unavailable in this session."));
            SetContent(body);
            return;
        }

        body.Children.Add(Caption(
            "Launch a desktop tool or focus its existing window. X, touch hold or right-click pins a program to Quick access."));
        if (service.LastResult.Length > 0)
        {
            body.Children.Add(Tagged(Caption(service.LastResult), "program.result"));
        }

        body.Children.Add(Tagged(PrimaryRow("Add program", "Choose from the apps installed on this PC",
            Icons.FolderPlus, OpenPicker), "program.add"));
        foreach (var state in service.ReadState())
        {
            var id = state.Definition.Id;
            var name = state.Definition.Name;
            var pinned = _pins?.Contains(ProgramsService.PinPrefix + id) ?? service.IsPinned(id);
            var row = new ProgramActionRow(state, () => Activate(id))
            {
                Tag = ProgramsService.PinPrefix + id,
                IsPinned = pinned
            };
            body.Children.Add(ListItem("program.item:" + id, row,
                new RowCommandButton("program.edit:" + id, "Edit " + name, Icons.Wrench,
                    () => Navigate(() => RenderEditor(id)), false, false),
                new RowCommandButton("program.pin:" + id, (pinned ? "Unpin " : "Pin ") + name, Icons.Pin,
                    () => PinRequested?.Invoke(id), pinned, false),
                new RowCommandButton("program.remove:" + id, "Remove " + name, Icons.Trash,
                    () => ConfirmRemove(id, name), false, true)));
        }

        if (service.ReadState().Length == 0)
        {
            body.Children.Add(Caption("No programs added yet."));
        }

        SetContent(body);
        ReleasePicker();
    }

    /// <inheritdoc />
    internal override void Leave()
    {
        base.Leave();
        ReleasePicker();
    }

    private void OpenPicker()
    {
        ReleasePicker();
        _pickerQuery = "";
        Navigate(_pickerLevel);
        _ = RunSafelyAsync(LoadInstalledAsync(), "read installed applications");
    }

    /// <summary>Reads the installed applications off the UI thread and draws them when they arrive.</summary>
    private async Task LoadInstalledAsync()
    {
        if (_service is not { } service)
        {
            return;
        }

        CancelPickerWork();
        var work = _pickerWork = new CancellationTokenSource();
        _installed = null;
        RedrawPicker();
        InstalledProgramList result;
        try
        {
            result = await service.ReadInstalledAsync(work.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!ReferenceEquals(_pickerWork, work))
        {
            return;
        }

        _installed = result;
        RedrawPicker();
        if (result.Error is null)
        {
            await LoadPickerIconsAsync(service, result, work);
        }
    }

    private void RedrawPicker()
    {
        if (ReferenceEquals(CurrentLevel, _pickerLevel))
        {
            RenderPicker();
        }
    }

    /// <summary>The Add program list: every installed app beside its alphabet bar, the manual file choice above them.</summary>
    private void RenderPicker()
    {
        var root = new StackPanel();
        var body = NewStack("Add program");
        if (_service is not { } service)
        {
            body.Children.Add(Caption("Programs are unavailable in this session."));
            SetContent(body);
            return;
        }

        body.Children.Add(Caption("Choose an app installed on this PC, the same list Windows shows in Start."));
        body.Children.Add(Tagged(Row("Search apps",
            _pickerQuery.Length == 0 ? "Type part of a name" : _pickerQuery, Icons.ListLines,
            () => EditText("Search apps", _pickerQuery, 0, value => _pickerQuery = value.Trim())), "program.search"));
        body.Children.Add(Tagged(Row("Browse for a file…", "Choose a Windows executable yourself", Icons.FolderPlus,
            () => _ = RunSafelyAsync(BrowseAsync(), "browse")), "program.browse"));
        var grid = new Grid { ColumnSpacing = 8, Tag = "program.picker" };
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        grid.Children.Add(body);
        if (_installed is not { } installed)
        {
            body.Children.Add(Tagged(Caption("Reading installed apps…"), "program.picker.loading"));
        }
        else if (installed.Error is { } error)
        {
            body.Children.Add(Banner("program.picker.error", FAInfoBarSeverity.Error, "Installed apps unavailable",
                error + " You can still browse for a file."));
            body.Children.Add(Tagged(Row("Try again", "Read the installed apps again", Icons.Restart,
                () => _ = RunSafelyAsync(LoadInstalledAsync(), "read installed applications")), "program.retry"));
        }
        else if (AddPickerRows(body, service, installed) is { Count: > 0 } targets)
        {
            var bar = new AlphabetJumpBar(targets);
            Grid.SetColumn(bar, 1);
            grid.Children.Add(bar);
        }

        root.Children.Add(grid);
        // Adding leaves this level, and a publication would only redraw every row for nothing.
        SetStaticContent(root);
    }

    /// <summary>Adds the rows and reports the first row of each letter.</summary>
    private Dictionary<char, string> AddPickerRows(StackPanel body, IProgramsSource service,
        InstalledProgramList installed)
    {
        var targets = new Dictionary<char, string>();
        var matches = installed.Programs
            .Where(program => _pickerQuery.Length == 0
                              || program.Name.Contains(_pickerQuery, StringComparison.CurrentCultureIgnoreCase))
            .ToArray();
        if (matches.Length == 0)
        {
            body.Children.Add(Tagged(Caption(_pickerQuery.Length == 0
                ? "Windows lists no apps to add."
                : $"No apps match “{_pickerQuery}”."), "program.picker.empty"));
            if (_pickerQuery.Length > 0)
            {
                body.Children.Add(Tagged(Row("Clear search", "Show every installed app", Icons.Close, () =>
                {
                    _pickerQuery = "";
                    RenderPicker();
                }), "program.search.clear"));
            }

            return targets;
        }

        var states = service.ReadState();
        foreach (var program in matches)
        {
            var existing = states.FirstOrDefault(state => Same(state.Definition, program));
            var tag = "program.pick:" + program.Key;
            var row = new IconActionRow(() => PickInstalled(program, existing?.Definition.Id))
            {
                Title = program.Name,
                Description = program.AppUserModelId is not null
                    ? "Microsoft Store app"
                    : Path.GetFileName(program.Executable),
                TrailingText = existing is null ? null : "Added",
                Tag = tag
            };
            row.ShowIcon(_pickerIcons.GetValueOrDefault(program.Key));
            body.Children.Add(row);
            targets.TryAdd(AlphabetJumpBar.Bucket(program.Name), tag);
        }

        return targets;
    }

    private static bool Same(ProgramDefinition definition, InstalledProgram program)
    {
        return program.AppUserModelId is { } aumid
            ? string.Equals(definition.AppUserModelId, aumid, StringComparison.OrdinalIgnoreCase)
            : string.Equals(definition.Executable, program.Executable, StringComparison.OrdinalIgnoreCase)
              && string.Equals(definition.Arguments, program.Arguments, StringComparison.Ordinal);
    }

    /// <summary>Reads every app's icon a batch at a time and fills the rows in without redrawing the list.</summary>
    private async Task LoadPickerIconsAsync(IProgramsSource service, InstalledProgramList installed,
        CancellationTokenSource work)
    {
        foreach (var batch in installed.Programs.Select(program => program.Key).Chunk(IconBatchSize))
        {
            IReadOnlyDictionary<string, Bitmap?> loaded;
            try
            {
                loaded = await service.ReadInstalledIconsAsync(batch, work.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // These rows keep their glyph and the remaining batches still load.
                continue;
            }

            if (!ReferenceEquals(_pickerWork, work))
            {
                foreach (var bitmap in loaded.Values)
                {
                    bitmap?.Dispose();
                }

                return;
            }

            foreach (var (key, bitmap) in loaded)
            {
                _pickerIcons[key] = bitmap;
            }

            ShowPickerIcons(loaded);
        }
    }

    private void ShowPickerIcons(IReadOnlyDictionary<string, Bitmap?> loaded)
    {
        if (!ReferenceEquals(CurrentLevel, _pickerLevel) || Content is not Control root)
        {
            return;
        }

        foreach (var row in root.GetLogicalDescendants().OfType<IconActionRow>())
        {
            if (row.Tag is string tag && tag.StartsWith("program.pick:", StringComparison.Ordinal)
                                      && loaded.TryGetValue(tag["program.pick:".Length..], out var icon))
            {
                row.ShowIcon(icon);
            }
        }
    }

    private void CancelPickerWork()
    {
        _pickerWork?.Cancel();
        _pickerWork?.Dispose();
        _pickerWork = null;
    }

    /// <summary>Ends the Add program flow's work and disposes the icons it loaded.</summary>
    private void ReleasePicker()
    {
        CancelPickerWork();
        _installed = null;
        foreach (var bitmap in _pickerIcons.Values)
        {
            bitmap?.Dispose();
        }

        _pickerIcons.Clear();
    }

    /// <summary>Puts a picked app straight into the list; one that is already there is just shown.</summary>
    private void PickInstalled(InstalledProgram program, string? existingId)
    {
        if (existingId is not null)
        {
            ReturnToList(existingId);
            return;
        }

        _ = RunSafelyAsync(AddAsync(new ProgramDefinition
        {
            Name = program.Name,
            AppUserModelId = program.AppUserModelId ?? "",
            Executable = program.Executable ?? "",
            Arguments = program.Arguments
        }), "add");
    }

    private void ReturnToList(string id)
    {
        Back();
        Dispatcher.UIThread.Post(() =>
        {
            if (Content is Control root && FocusSearch.Keyed(root, ProgramsService.PinPrefix + id) is { } row)
            {
                row.Focus(NavigationMethod.Directional);
            }
        });
    }

    private async Task AddAsync(ProgramDefinition program)
    {
        if (_adding || _service is not { } service)
        {
            return;
        }

        _adding = true;
        var generation = NavigationGeneration;
        try
        {
            await service.SaveAsync(program, true);
            if (generation == NavigationGeneration)
            {
                ReturnToList(program.Id);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (generation == NavigationGeneration)
            {
                Toast(ex.Message);
            }
        }
        finally
        {
            _adding = false;
        }
    }

    private void ConfirmRemove(string id, string name)
    {
        ShowConfirm("Remove “" + name + "”?",
            "WSGM forgets this program and removes its Quick access pin. The application and its files stay "
            + "untouched, and it keeps running if it is open.", "Remove",
            () => _ = RunSafelyAsync(RemoveAsync(id), "remove"));
    }

    private async Task RemoveAsync(string id)
    {
        if (_service is { } service)
        {
            UnpinRequested?.Invoke(id);
            await ApplyAsync(() => service.RemoveAsync(id));
        }
    }

    private async Task BrowseAsync()
    {
        var generation = NavigationGeneration;
        var path = await PickPathAsync(false, ".exe");
        if (path is null || generation != NavigationGeneration)
        {
            return;
        }

        await AddAsync(new ProgramDefinition { Name = Path.GetFileNameWithoutExtension(path), Executable = path });
    }

    private void RenderEditor(string id)
    {
        var state = _service?.ReadState().FirstOrDefault(item => item.Definition.Id == id);
        if (state is null || _service is not { } service)
        {
            RenderMessage("Program removed",
                "This entry no longer exists. Removing an entry never closes the application.");
            return;
        }

        var entry = state.Definition;
        var packaged = entry.AppUserModelId.Length > 0;
        var body = NewStack(entry.Name);
        body.Children.Add(Tagged(Caption(packaged ? state.Status + " · Microsoft Store app" : state.Status),
            "program.status"));
        body.Children.Add(new ProgramActionRow(state, () => Activate(id))
            { Tag = ProgramsService.PinPrefix + id, IsPinned = service.IsPinned(id) });
        body.Children.Add(Tagged(Row("Display name", entry.Name, Icons.ListLines,
                () => EditText("Display name", entry.Name, 160, value => Save(entry with { Name = value }))),
            "program.name"));
        if (!packaged)
        {
            body.Children.Add(Tagged(Row("Executable", entry.Executable, Icons.FolderPlus,
                () => _ = RunSafelyAsync(ChooseExecutableAsync(entry), "choose executable")), "program.executable"));
            body.Children.Add(Tagged(Row("Arguments", entry.Arguments.Length == 0 ? "None" : entry.Arguments,
                Icons.ListLines, () => EditText("Arguments", entry.Arguments, 4096,
                    value => Save(entry with { Arguments = value }))), "program.arguments"));
            body.Children.Add(Tagged(Row("Working directory", entry.WorkingDirectory.Length == 0
                    ? "Executable folder"
                    : entry.WorkingDirectory, Icons.FolderPlus,
                () => _ = RunSafelyAsync(ChooseDirectoryAsync(entry), "working directory")), "program.directory"));
            if (entry.WorkingDirectory.Length > 0)
            {
                body.Children.Add(Tagged(Row("Use executable folder", "Clear the custom working directory", Icons.ArrowLeft,
                    () => Save(entry with { WorkingDirectory = "" })), "program.directory.clear"));
            }

            body.Children.Add(ToggleRow("Request administrator elevation", entry.Elevated,
                value => Save(entry with { Elevated = value })));
            body.Children.Add(Caption(
                "Off launches as the normal interactive user. On explicitly requests UAC; Windows may refuse focus across integrity levels."));
            body.Children.Add(ToggleRow("Reinvoke when running without a window", entry.ReactivateWithoutWindow,
                value => Save(entry with { ReactivateWithoutWindow = value })));
            body.Children.Add(Caption(
                "Leave off unless this application's single-instance IPC activation is known to be safe. Otherwise WSGM reports running without a window."));
        }

        if (!packaged)
        {
            body.Children.Add(Tagged(Row("Select running UI application",
                entry.UiExecutable.Length > 0
                    ? entry.UiExecutable
                    : "For launchers and bootstrap apps: choose the real application's window",
                Icons.Monitor, () => _ = RunSafelyAsync(SelectUiAsync(id), "select UI")), "program.ui"));
            if (entry.UiExecutable.Length > 0)
            {
                body.Children.Add(Tagged(Row("Forget resolved UI", "Match the configured executable again", Icons.ArrowLeft,
                    () => _ = ApplyAsync(() => service.ForgetUiAsync(id))), "program.ui.clear"));
            }

            if (!state.Available)
            {
                body.Children.Add(Tagged(Row("Approve replacement executable", entry.Executable,
                    Icons.Info, () => ConfirmAsync("Approve replacement executable",
                        "The executable changed since it was selected. Approve only if you trust the new file. This also clears the resolved UI identity.",
                        () => service.SaveAsync(entry, true))), "program.approve"));
            }

            body.Children.Add(Tagged(Row("Clear pending launch",
                "Only after checking that a previous launch did not start the tool",
                Icons.Restart, () => ConfirmAsync("Clear pending launch",
                    "A previous launch may still be starting. Clearing its pending state allows another launch and may create a duplicate.",
                    () => service.ClearPendingAsync(id))), "program.pending.clear"));
        }

        SetContent(body);
        ReleasePicker();
    }

    private void Save(ProgramDefinition entry)
    {
        _ = ApplyAsync(() => _service!.SaveAsync(entry, false));
    }

    private async Task ChooseExecutableAsync(ProgramDefinition entry)
    {
        var generation = NavigationGeneration;
        var path = await PickPathAsync(false, ".exe");
        if (path is not null && generation == NavigationGeneration)
        {
            await ApplyAsync(() => _service!.SaveAsync(entry with { Executable = path }, true));
        }
    }

    private async Task ChooseDirectoryAsync(ProgramDefinition entry)
    {
        var generation = NavigationGeneration;
        var path = await PickPathAsync(true);
        if (path is not null && generation == NavigationGeneration)
        {
            await ApplyAsync(() => _service!.SaveAsync(entry with { WorkingDirectory = path }, false));
        }
    }

    private async Task SelectUiAsync(string id)
    {
        var generation = NavigationGeneration;
        var windows = await Task.Run(ProgramsService.SelectableWindows);
        if (generation != NavigationGeneration)
        {
            return;
        }

        Navigate(() =>
        {
            var body = NewStack("Select running UI application");
            body.Children.Add(Caption(
                "Choose only the window belonging to this program. WSGM remembers its exact executable path and fingerprint for future focus actions."));
            foreach (var window in windows)
            {
                var selected = window;
                body.Children.Add(Tagged(Row(window.Window.Title, window.Executable, Icons.Monitor,
                        () => ConfirmAsync("Remember this application?",
                            selected.Window.Title + "\n" + selected.Executable,
                            () => _service!.RememberWindowAsync(id, selected), true)),
                    "program.ui.window:" + window.Window.Hwnd));
            }

            if (windows.Length == 0)
            {
                body.Children.Add(
                    Caption("No eligible current-session application windows found. Open the tool, then try again."));
            }

            SetContent(body);
        });
    }

    private void ConfirmAsync(string title, string message, Func<Task> command, bool returnHome = false)
    {
        Navigate(() =>
        {
            var body = NewStack(title);
            body.Children.Add(Caption(message));
            body.Children.Add(Tagged(Row("Cancel", "", Icons.ArrowLeft, () => Back()), "program.confirm.cancel"));
            body.Children.Add(Tagged(PrimaryRow("Confirm", "", Icons.Info,
                () => _ = CommitAsync()), "program.confirm.accept"));
            SetContent(body);
        });
        return;

        async Task CommitAsync()
        {
            if (await ApplyAsync(command))
            {
                if (returnHome)
                {
                    Open();
                }
                else
                {
                    Back();
                }
            }
        }
    }

    private async Task<bool> ApplyAsync(Func<Task> command)
    {
        var generation = NavigationGeneration;
        try
        {
            await command();
            if (generation == NavigationGeneration)
            {
                CurrentLevel?.Invoke();
            }

            return true;
        }
        catch (Exception ex)
        {
            if (generation == NavigationGeneration)
            {
                Toast(ex.Message);
            }

            return false;
        }
    }
}
