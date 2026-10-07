using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Overlay;

public sealed partial class GameLibraryView
{
    private RomSourceConfig? _romDraft;
    private bool _romSingleFile;

    private void OpenRomSource(string? id, bool singleFile = false)
    {
        var state = _service?.ReadState();
        var existing = state?.RomSources?.FirstOrDefault(source => source.Id == id);
        var manual = singleFile ? state?.ManualSources?.FirstOrDefault(source => source.Id == id) : null;
        var system = _service?.ReadRomSystems().FirstOrDefault();
        var preferred = _service?.ReadRomState().SystemPreferences.FirstOrDefault(item => item.SystemId == system?.Id);
        _romSingleFile = singleFile;
        if (manual?.RomPath is { } path)
        {
            _romDraft = new RomSourceConfig
            {
                Id = manual.Id,
                Name = manual.Name,
                Root = path.Copy(),
                SystemId = manual.SystemId,
                EmulatorInstallationId = manual.EmulatorInstallationId,
                CoreId = manual.CoreId,
                Arguments = [.. manual.RomArguments]
            };
        }
        else
        {
            _romDraft = existing?.Copy() ?? new RomSourceConfig
            {
                Name = singleFile ? "" : "ROM library",
                Root = new ManagedContentPath { Directory = !singleFile },
                SystemId = system?.Id ?? "",
                EmulatorInstallationId = preferred?.InstallationId ?? "",
                CoreId = preferred?.CoreId ?? "",
                Extensions = system?.Extensions.ToList() ?? []
            };
        }

        Navigate(RenderRomSource);
    }

    private void RenderRomSource()
    {
        var state = _service?.ReadState();
        if (_romDraft is not { } draft || state is null)
        {
            return;
        }

        var body = NewStack(draft.Id.Length == 0
            ? _romSingleFile ? "Add a ROM" : "Add a ROM library"
            : "Configure " + draft.Name);
        AddStatus(body, state);
        body.Children.Add(Caption(
            "Preview the titles before applying changes to Steam. ROM files stay in their current location."));
        body.Children.Add(Tagged(Row(_romSingleFile ? "Title" : "Library name", draft.Name, Icons.ListLines, () =>
            EditText(_romSingleFile ? "Title" : "Library name", draft.Name, 0, value =>
            {
                _romDraft!.Name = value;
                RenderRomSource();
            })), "rom.name"));
        body.Children.Add(Tagged(Row(_romSingleFile ? "Choose ROM file" : "Choose ROM folder", draft.Root.AbsolutePath,
            Icons.Grid4,
            () => _ = RunSafelyAsync(ChooseRomFolderAsync(), "ROM folder")), "rom.path"));
        var systems = _service!.ReadRomSystems();
        body.Children.Add(ChoiceRow("System", systems.Select(system => (system.Id, system.Name)).ToArray(),
            draft.SystemId,
            value =>
            {
                var system = systems.FirstOrDefault(item => item.Id == value);
                var preferred = _service.ReadRomState().SystemPreferences
                    .FirstOrDefault(item => item.SystemId == value);
                _romDraft!.SystemId = value;
                _romDraft.Extensions = system?.Extensions.ToList() ?? [];
                _romDraft.EmulatorInstallationId = preferred?.InstallationId ?? "";
                _romDraft.CoreId = preferred?.CoreId ?? "";
                RenderRomSource();
            }));
        AddRomLaunchChoices(body, draft.SystemId, draft.EmulatorInstallationId, draft.CoreId,
            (installationId, coreId) =>
            {
                _romDraft!.EmulatorInstallationId = installationId;
                _romDraft.CoreId = coreId;
                RenderRomSource();
            });
        body.Children.Add(Tagged(Row("Manage emulators", "Install one or register an existing executable", Icons.Grid4,
            () => EmulatorsRequested?.Invoke()), "rom.manage"));
        if (!_romSingleFile)
        {
            body.Children.Add(ToggleRow("Include subfolders", draft.IncludeSubfolders, value =>
            {
                _romDraft!.IncludeSubfolders = value;
                RenderRomSource();
            }));
            body.Children.Add(ToggleRow("Clean title names", draft.TitleCleanup, value =>
            {
                _romDraft!.TitleCleanup = value;
                RenderRomSource();
            }));
            body.Children.Add(Tagged(Row("File types", string.Join(" ", draft.Extensions), Icons.ListLines, () =>
                EditText("File extensions", string.Join(" ", draft.Extensions), 0, value =>
                {
                    _romDraft!.Extensions = ParseExtensions(value);
                    RenderRomSource();
                })), "rom.extensions"));
        }

        body.Children.Add(Tagged(Row("Launch arguments",
                draft.Arguments.Count == 0 ? "Emulator default" : string.Join(" · ", draft.Arguments),
                Icons.ListLines, () => OpenRomArguments(draft.Arguments, values =>
                {
                    _romDraft!.Arguments = values;
                    Back();
                    return Task.CompletedTask;
                })),
            "rom.arguments"));
        if (!_romSingleFile)
        {
            body.Children.Add(Tagged(Row("Excluded paths", string.Join("; ", draft.Exclusions), Icons.ListLines, () =>
                EditText("Excluded paths, separated by semicolons", string.Join("; ", draft.Exclusions), 0, value =>
                {
                    _romDraft!.Exclusions =
                        value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .ToList();
                    RenderRomSource();
                })), "rom.exclusions"));
        }

        body.Children.Add(Tagged(PrimaryRow(_romSingleFile ? "Save ROM and preview" : "Save library and preview",
            "Scan the configured libraries without writing to Steam",
            Icons.Play, state.Loading || draft.Root.AbsolutePath.Length == 0 || draft.SystemId.Length == 0
                        || draft.EmulatorInstallationId.Length == 0
                        || (_service.ReadRomState().Choices.FirstOrDefault(item => item.SystemId == draft.SystemId)
                                ?.Installations.FirstOrDefault(item => item.Id == draft.EmulatorInstallationId)
                            is { RequiresCore: true } && draft.CoreId.Length == 0)
                ? null
                : () => _ = RunSafelyAsync(SaveRomSourceAsync(), "save ROM source")), "rom.save"));
        if (draft.Id.Length > 0)
        {
            body.Children.Add(Tagged(DangerRow(_romSingleFile ? "Remove ROM entry" : "Remove library",
                "ROMs and imported Steam shortcuts stay",
                Icons.Close, state.Loading
                    ? null
                    : () => ConfirmCommand(_romSingleFile ? "Remove this ROM entry?" : "Remove this ROM library?",
                        _romSingleFile
                            ? "Stops tracking this ROM. Your file and imported shortcut stay."
                            : "Stops scanning this library. Your ROMs and imported shortcuts stay.",
                        token => _romSingleFile
                            ? _service!.RemoveManualSourceAsync(draft.Id, token)
                            : _service!.RemoveRomSourceAsync(draft.Id, token))), "rom.remove"));
        }

        SetContent(body);
    }

    private async Task ChooseRomFolderAsync()
    {
        var generation = NavigationGeneration;
        var path = await PickPathAsync(!_romSingleFile);
        if (path is not null && generation == NavigationGeneration && _romDraft is not null)
        {
            _romDraft.Root = new ManagedContentPath { AbsolutePath = path, Directory = !_romSingleFile };
            if (_romSingleFile && _romDraft.Name.Length == 0)
            {
                _romDraft.Name = Path.GetFileNameWithoutExtension(path);
            }

            RenderRomSource();
        }
    }

    private Task SaveRomSourceAsync()
    {
        if (_romDraft is not { } source)
        {
            return Task.CompletedTask;
        }

        var saved = source.Copy();
        return RunCommandAsync(token => _service!.AddRomSourceAsync(saved, token),
            _romSingleFile ? "save ROM" : "save ROM library",
            () => Replace(() => RenderReview(false)));
    }

    private void AddRomEntryControls(StackPanel body, GameLibraryState state, GameLibraryEntry entry)
    {
        if (entry.ManagedId.Length == 0)
        {
            return;
        }

        body.Children.Add(Caption("Location: " + (entry.Location.Length > 0 ? entry.Location : entry.ContentPath)));
        body.Children.Add(Caption("Availability: " + entry.AvailabilityLabel));
        body.Children.Add(Tagged(Row("Recheck availability", "Check the storage, content and emulator again",
            Icons.Restart, () => Run(token => _service!.RecheckAvailabilityAsync(entry.Id, token))), "rom.recheck"));
        if (entry.AppId > 0 && entry.Action != nameof(ImportAction.Conflict))
        {
            body.Children.Add(Tagged(entry.Action == nameof(ImportAction.Remove)
                ? Row("Keep this shortcut", "Cancel its staged removal", Icons.Restart,
                    () => Run(token => _service!.SetCleanupAsync(entry.Id, false, token)))
                : DangerRow("Stage shortcut removal", "Removes it from Steam when applied. ROMs and saves stay.",
                    Icons.Close,
                    () => ConfirmCommand("Stage shortcut removal?",
                        "The next apply will remove this shortcut from Steam. "
                        + "Your ROMs and saves are preserved.",
                        token => _service!.SetCleanupAsync(entry.Id, true, token))), "rom.cleanup"));
        }

        if (entry.SystemId.Length == 0)
        {
            return;
        }

        body.Children.Add(Tagged(Row("Title", entry.Name, Icons.ListLines, () =>
                EditText("Title", entry.Name, 0,
                    value => Run(token => _service!.SetRomTitleAsync(entry.Id, value, token)))),
            "rom.title"));
        AddRomLaunchChoices(body, entry.SystemId, entry.EmulatorInstallationId, entry.CoreId,
            (installationId, coreId) =>
                Run(token => _service!.SetRomEmulatorAsync(entry.Id, installationId, coreId, token)));
        body.Children.Add(Tagged(Row("Use library or system default", "Clear this title's emulator and core override",
            Icons.Restart, () => Run(token => _service!.SetRomEmulatorAsync(entry.Id, "", "", token))), "rom.default"));
        body.Children.Add(Tagged(Row("Launch arguments", entry.Arguments is { Count: > 0 }
                ? string.Join(" · ", entry.Arguments)
                : "Library or emulator default",
            Icons.ListLines, () => OpenRomArguments(entry.Arguments ?? [], values =>
                RunCommandAsync(token => _service!.SetRomArgumentsAsync(entry.Id, values, token), "save arguments",
                    () => Back()))), "rom.arguments"));
    }

    private void AddRomLaunchChoices(StackPanel body, string systemId, string installationId,
        string coreId, Action<string, string> choose)
    {
        var installations = _service?.ReadRomState().Choices.FirstOrDefault(item => item.SystemId == systemId)
                                ?.Installations ??
                            [];
        var choices = installations.Select(item => (item.Id, item.Label)).ToList();
        choices.Insert(0, ("", "Choose an installed emulator"));
        if (installationId.Length > 0 && installations.All(item => item.Id != installationId))
        {
            choices.Add((installationId, "Selected emulator is unavailable"));
        }

        body.Children.Add(ChoiceRow("Emulator", choices, installationId, value =>
        {
            var installation = installations.FirstOrDefault(item => item.Id == value);
            choose(value, installation?.DefaultCoreId ?? "");
        }));
        if (installations.FirstOrDefault(item => item.Id == installationId) is not
            { RequiresCore: true } selected)
        {
            return;
        }

        var coreChoices = selected.Cores.Select(core => (core.Id, core.Label)).ToList();
        coreChoices.Insert(0, ("", "Choose a core"));
        if (coreId.Length > 0 && coreChoices.All(item => item.Item1 != coreId))
        {
            coreChoices.Add((coreId, "Selected core is unavailable"));
        }

        body.Children.Add(ChoiceRow("Core", coreChoices, coreId, value => choose(installationId, value)));
    }
}
