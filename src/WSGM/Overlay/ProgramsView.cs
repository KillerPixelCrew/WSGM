using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>Controller/touch Programs editor; the session service owns every durable operation.</summary>
public sealed class ProgramsView : ServiceSubView
{
    private IProgramsSource? _service;
    internal event Action<string>? ActivateRequested;
    internal event Action<string>? PinRequested;
    internal event Action<string>? UnpinRequested;
    /// <inheritdoc />
    protected override string LogScope => "Programs";

    internal void Attach(IProgramsSource? service)
    {
        _service = service;
        AttachSource(IsEffectivelyVisible ? service : null);
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

        body.Children.Add(Caption("Launch a desktop tool or focus its existing window. X, touch hold or right-click pins a program to Quick access."));
        if (service.LastResult.Length > 0)
        {
            body.Children.Add(Tagged(Caption(service.LastResult), "program.result"));
        }

        body.Children.Add(Tagged(PrimaryRow("Add program", "Choose a Windows executable", Icons.FolderPlus,
            () => _ = RunSafelyAsync(AddAsync(), "add")), "program.add"));
        foreach (var state in service.ReadState())
        {
            var id = state.Definition.Id;
            var group = new StackPanel { Spacing = 6, Tag = "program.group:" + id };
            var action = new ProgramActionRow(state, () => ActivateRequested?.Invoke(id))
            {
                Tag = ProgramsService.PinPrefix + id,
                IsPinned = service.IsPinned(id)
            };
            group.Children.Add(action);
            group.Children.Add(Tagged(Row("Edit " + state.Definition.Name,
                "Name, executable, arguments, identity and pin", Icons.Gear,
                () => Navigate(() => RenderEditor(id))), "program.edit:" + id));
            body.Children.Add(group);
        }

        if (service.ReadState().Length == 0)
        {
            body.Children.Add(Caption("No programs added yet."));
        }

        SetContent(body);
    }

    private async Task AddAsync()
    {
        var generation = NavigationGeneration;
        var path = await PickPathAsync(folder: false, ".exe");
        if (path is null || generation != NavigationGeneration || _service is not { } service)
        {
            return;
        }

        var definition = new ProgramDefinition { Name = Path.GetFileNameWithoutExtension(path), Executable = path };
        if (await ApplyAsync(() => service.SaveAsync(definition, approveExecutable: true)))
        {
            Navigate(() => RenderEditor(definition.Id));
        }
    }

    private void RenderEditor(string id)
    {
        var state = _service?.ReadState().FirstOrDefault(item => item.Definition.Id == id);
        if (state is null || _service is not { } service)
        {
            RenderMessage("Program removed", "This entry no longer exists. Removing an entry never closes the application.");
            return;
        }

        var entry = state.Definition;
        var body = NewStack(entry.Name);
        body.Children.Add(Tagged(Caption(state.Status), "program.status"));
        body.Children.Add(new ProgramActionRow(state, () => ActivateRequested?.Invoke(id))
            { Tag = ProgramsService.PinPrefix + id, IsPinned = service.IsPinned(id) });
        body.Children.Add(Tagged(Row("Display name", entry.Name, Icons.ListLines,
            () => EditText("Display name", entry.Name, 160, value => Save(entry with { Name = value }))), "program.name"));
        body.Children.Add(Tagged(Row("Executable", entry.Executable, Icons.FolderPlus,
            () => _ = RunSafelyAsync(ChooseExecutableAsync(entry), "choose executable")), "program.executable"));
        body.Children.Add(Tagged(Row("Arguments", entry.Arguments.Length == 0 ? "None" : entry.Arguments,
            Icons.ListLines, () => EditText("Arguments", entry.Arguments, 4096,
                value => Save(entry with { Arguments = value }))), "program.arguments"));
        body.Children.Add(Tagged(Row("Working directory", entry.WorkingDirectory.Length == 0
                ? "Executable folder" : entry.WorkingDirectory, Icons.FolderPlus,
            () => _ = RunSafelyAsync(ChooseDirectoryAsync(entry), "working directory")), "program.directory"));
        if (entry.WorkingDirectory.Length > 0)
        {
            body.Children.Add(Tagged(Row("Use executable folder", "Clear the custom working directory", Icons.ArrowLeft,
                () => Save(entry with { WorkingDirectory = "" })), "program.directory.clear"));
        }

        body.Children.Add(ToggleRow("Request administrator elevation", entry.Elevated,
            value => Save(entry with { Elevated = value })));
        body.Children.Add(Caption("Off launches as the normal interactive user. On explicitly requests UAC; Windows may refuse focus across integrity levels."));
        body.Children.Add(ToggleRow("Reinvoke when running without a window", entry.ReactivateWithoutWindow,
            value => Save(entry with { ReactivateWithoutWindow = value })));
        body.Children.Add(Caption("Leave off unless this application's single-instance IPC activation is known to be safe. Otherwise WSGM reports running without a window."));
        body.Children.Add(Tagged(Row(service.IsPinned(id) ? "Unpin from Quick access" : "Pin to Quick access",
            "Uses the same launch-or-focus action", Icons.Pin,
            () => PinRequested?.Invoke(id)), "program.pin"));
        body.Children.Add(Tagged(Row("Select running UI application",
            entry.UiExecutable.Length > 0 ? entry.UiExecutable : "For launchers and bootstrap apps: choose the real application's window",
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
                    () => service.SaveAsync(entry, approveExecutable: true))), "program.approve"));
        }

        body.Children.Add(Tagged(Row("Clear pending launch", "Only after checking that a previous launch did not start the tool",
            Icons.Restart, () => ConfirmAsync("Clear pending launch",
                "A previous launch may still be starting. Clearing its pending state allows another launch and may create a duplicate.",
                () => service.ClearPendingAsync(id))), "program.pending.clear"));
        body.Children.Add(Tagged(DangerRow("Remove program", "Remove WSGM configuration and pin; keep the application running",
            Icons.Close, () => ConfirmAsync("Remove program", "The application and its files will remain untouched.",
                () =>
                {
                    UnpinRequested?.Invoke(id);
                    return service.RemoveAsync(id);
                }, returnHome: true)), "program.remove"));
        SetContent(body);
    }

    private void Save(ProgramDefinition entry) => _ = ApplyAsync(() => _service!.SaveAsync(entry, approveExecutable: false));

    private async Task ChooseExecutableAsync(ProgramDefinition entry)
    {
        var generation = NavigationGeneration;
        var path = await PickPathAsync(folder: false, ".exe");
        if (path is not null && generation == NavigationGeneration)
        {
            await ApplyAsync(() => _service!.SaveAsync(entry with { Executable = path }, approveExecutable: true));
        }
    }

    private async Task ChooseDirectoryAsync(ProgramDefinition entry)
    {
        var generation = NavigationGeneration;
        var path = await PickPathAsync(folder: true);
        if (path is not null && generation == NavigationGeneration)
        {
            await ApplyAsync(() => _service!.SaveAsync(entry with { WorkingDirectory = path }, approveExecutable: false));
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
            body.Children.Add(Caption("Choose only the window belonging to this program. WSGM remembers its exact executable path and fingerprint for future focus actions."));
            foreach (var window in windows)
            {
                var selected = window;
                body.Children.Add(Tagged(Row(window.Window.Title, window.Executable, Icons.Monitor,
                    () => ConfirmAsync("Remember this application?", selected.Window.Title + "\n" + selected.Executable,
                        () => _service!.RememberWindowAsync(id, selected), returnHome: true)),
                    "program.ui.window:" + window.Window.Hwnd));
            }

            if (windows.Length == 0)
            {
                body.Children.Add(Caption("No eligible current-session application windows found. Open the tool, then try again."));
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
