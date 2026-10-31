using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>The independent emulator tool over its one session-owned backend.</summary>
public sealed partial class EmulatorManagerView : ServiceSubView
{
    private string _coreFilter = "all";
    private string _coreSearch = "";

    private string _emulatorChannel = "";
    private string _search = "";
    private IEmulatorBackend? _service;
    private string _tab = "installed";

    /// <inheritdoc />
    protected override string LogScope => "Emulator Manager";

    internal void Attach(IEmulatorBackend? service)
    {
        _service = service;
        AttachSource(IsEffectivelyVisible ? service : null);
    }

    private void AddReleaseNotes(StackPanel body, EmulatorOffer? offer)
    {
        if (offer?.NotesUrl is not { Length: > 0 })
        {
            return;
        }

        body.Children.Add(Tagged(Row("Release notes", "Open the source's notes for this release", Icons.ListLines,
            () => Run(token => _service!.OpenEmulatorReleaseNotesAsync(offer.DefinitionId, offer.Channel,
                offer.Architecture, token))), "emu.notes"));
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

    private protected override void RenderHome()
    {
        if (_tab == "bios")
        {
            RenderBios();
        }
        else if (_tab == "defaults")
        {
            RenderEmulatorDefaults();
        }
        else
        {
            RenderEmulators();
        }
    }

    private void RenderEmulators()
    {
        var body = NewStack("Emulators");
        var shown = ReadShown();
        if (shown is null)
        {
            body.Children.Add(Caption("Emulator information is not available in this session."));
            SetContent(body);
            return;
        }

        var snapshot = shown.Snapshot;
        var listedById = shown.Page.Installed.ToDictionary(item => item.Id);
        AddEmulatorStatus(body, shown);
        body.Children.Add(Tagged(Row("Check for updates", "Refresh available releases from their sources",
            Icons.Restart, snapshot.Busy ? null : () => Run(_service!.RefreshEmulatorsAsync)), "emu.refresh"));
        body.Children.Insert(1, ManagerTabs(snapshot));
        var updates = shown.Page.Installed.Count(item => item.UpdateAvailable);
        var missing = snapshot.Installations.Count(item => item.MissingRequirements.Length > 0);
        body.Children.Add(
            Caption($"{snapshot.Installations.Length} installed · {updates} updates · {missing} need setup"));
        body.Children.Add(Tagged(Row("Search emulators or systems", _search, Icons.ListLines,
            () => EditText("Search emulators or systems", _search, 120, value =>
            {
                _search = value;
                RenderEmulators();
            })), "emu.search"));
        if (_tab == "installed")
        {
            foreach (var installation in snapshot.Installations.Where(item => Matches(item.Name, item.Systems)))
            {
                var id = installation.Id;
                var listed = listedById[id];
                var row = Tagged(Row(installation.Name, listed.Detail, Icons.Grid4,
                    () => Navigate(() => RenderEmulatorInstallation(id))), "emu.installation:" + id);
                row.TrailingText = listed.Badge;
                body.Children.Add(row);
            }

            if (snapshot.Installations.Length == 0)
            {
                body.Children.Add(
                    Caption("No emulators installed. Choose Available to install or register an emulator."));
            }
        }
        else
        {
            foreach (var definition in snapshot.Definitions.Where(item => !snapshot.Installations.Any(installed =>
                         installed.DefinitionId == item.Id) && Matches(item.Name, item.Systems)))
            {
                var id = definition.Id;
                body.Children.Add(Tagged(Row(definition.Name,
                    string.Join(" · ", definition.Systems.Select(EmulatorPresentation.SystemName)), Icons.Grid4, () =>
                    {
                        _emulatorChannel = definition.Channels.FirstOrDefault() ?? "";
                        Navigate(() => RenderEmulator(id));
                    }), "emu:" + id));
            }
        }

        SetEmulatorContent(body, shown);
    }

    internal bool CheckForUpdates()
    {
        if (!IsEffectivelyVisible || _service is null)
        {
            return false;
        }

        if (!_service.ReadProgressState().Busy)
        {
            Run(_service.RefreshEmulatorsAsync);
        }

        return true;
    }

    private bool Matches(string name, string[] systems)
    {
        return EmulatorPresentation.Matches(_search, name, string.Join(" ", systems),
            string.Join(" ", systems.Select(EmulatorPresentation.SystemName)));
    }

    private Control ManagerTabs(EmulatorSnapshot snapshot, string? selected = null)
    {
        var available = snapshot.Definitions.Count(item =>
            snapshot.Installations.All(installed => installed.DefinitionId != item.Id));
        return ToolTabs(selected ?? _tab,
            ("installed", $"Installed {snapshot.Installations.Length}", () => SelectTab("installed")),
            ("available", $"Available {available}", () => SelectTab("available")),
            ("bios", "BIOS & firmware", () => SelectTab("bios")),
            ("defaults", "System defaults", () => SelectTab("defaults")));
    }

    private void SelectTab(string tab)
    {
        _tab = tab;
        NavigationStack.Clear();
        NavigationGeneration++;
        Replace(tab == "bios" ? RenderBios : tab == "defaults" ? RenderEmulatorDefaults : RenderEmulators);
    }

    private static StackPanel Group(StackPanel parent, string title, string key)
    {
        var content = new StackPanel { Spacing = 8 };
        var section = new CollapsibleSection(title, content) { IsExpanded = true };
        section.Heading.Tag = "emu.group:" + key;
        parent.Children.Add(section);
        return content;
    }

    private static void Commands(StackPanel parent, params ActionButton[] buttons)
    {
        var bar = new WrapPanel { Orientation = Orientation.Horizontal, Tag = "emu.actions:" + buttons[0].Tag };
        foreach (var button in buttons)
        {
            button.Description = "";
            button.IconGeometry = null;
            button.MinHeight = 36;
            button.Margin = new Thickness(0, 0, 8, 4);
            button.Padding = new Thickness(12, 6);
            button.HorizontalAlignment = HorizontalAlignment.Left;
            bar.Children.Add(button);
        }

        parent.Children.Add(bar);
    }

    private void RenderEmulator(string definitionId)
    {
        var shown = ReadShown();
        var definition = shown?.Snapshot.Definitions.FirstOrDefault(item => item.Id == definitionId);
        if (shown is null || definition is null)
        {
            RenderMessage("Emulator", "That emulator is no longer listed.");
            return;
        }

        var state = shown.Page;
        var snapshot = shown.Snapshot;

        var body = NewStack(definition.Name);
        AddEmulatorStatus(body, shown);
        body.Children.Add(Caption("Emulators › Available"));
        body.Children.Add(Caption(definition.Source));
        body.Children.Add(Caption("Systems: " +
                                  string.Join(", ", definition.Systems.Select(EmulatorPresentation.SystemName))));
        var install = Group(body, "Install", "install");
        install.Children.Add(ChoiceRow("Release channel",
            definition.Channels.Select(channel => (channel, channel)).ToArray(),
            _emulatorChannel, value =>
            {
                _emulatorChannel = value;
                RenderEmulator(definitionId);
            }));
        var offer = snapshot.Offers.FirstOrDefault(item => item.DefinitionId == definitionId
                                                           && item.Channel == _emulatorChannel);
        install.Children.Add(Caption(offer is null ? "Check for updates to read the latest release."
            : offer.Error.Length > 0 ? offer.Error : "Latest: " + offer.Version));
        AddReleaseNotes(install, offer);

        install.Children.Add(Tagged(PrimaryRow("Install " + _emulatorChannel,
            definition.DataPolicy.HasCores
                ? "Installs RetroArch and the complete published Windows core catalogue"
                : "Download and manage this emulator",
            Icons.Grid4, snapshot.Busy
                ? null
                : () => Run(token =>
                    _service!.InstallEmulatorAsync(definitionId, _emulatorChannel, token))), "emu.install"));
        install.Children.Add(Tagged(Row("Use an existing installation", "Choose this emulator's executable",
                Icons.Grid4, snapshot.Busy ? null : () => _ = RunSafelyAsync(ChooseExternalAsync(), "choose emulator")),
            "emu.external"));
        if (definition.DataPolicy.RequiresVisualCpp || definition.Id == "model2")
        {
            install.Children.Add(Tagged(Row("Install required runtimes",
                "Install verified Microsoft components; Windows may request permission",
                Icons.Grid4, snapshot.Busy ? null : () => Run(token =>
                    _service!.InstallEmulatorRuntimeDependenciesAsync(definitionId, token))), "emu.runtimes"));
        }
        var setup = Group(body, "Set up from your BIOS folder", "setup");
        setup.Children.Add(Caption(state.Bios.Folder ?? "Choose your BIOS folder on the BIOS & firmware tab."));
        foreach (var prerequisite in definition.Prerequisites)
        {
            setup.Children.Add(Caption(prerequisite.Name + ": " + prerequisite.Description));
        }

        setup.Children.Add(Tagged(Row("Open BIOS & firmware",
            "Add local BIOS, keys and firmware before or after installation",
            Icons.Grid4, () => Navigate(RenderBios)), "emu.bios"));
        foreach (var installation in snapshot.Installations.Where(item => item.DefinitionId == definitionId))
        {
            var id = installation.Id;
            body.Children.Add(Tagged(Row(installation.Name + " " + installation.Version,
                $"{installation.Channel} · {(installation.Managed ? "Managed" : "External")} · {installation.Architecture}",
                Icons.ListLines, () => Navigate(() => RenderEmulatorInstallation(id))), "emu.installation:" + id));
        }

        SetEmulatorContent(body, shown);
        return;

        async Task ChooseExternalAsync()
        {
            var generation = NavigationGeneration;
            var executable = await PickPathAsync(false, ".exe");
            if (executable is not null && generation == NavigationGeneration)
            {
                Run(token => _service!.UseExternalEmulatorAsync(definitionId, executable, token));
            }
        }
    }

    private void RenderEmulatorInstallation(string installationId)
    {
        var shown = ReadShown();
        var installation = shown?.Snapshot.Installations.FirstOrDefault(item => item.Id == installationId);
        if (shown is null || installation is null)
        {
            RenderMessage("Installation", "That installation is no longer registered.");
            return;
        }

        var state = shown.Page;
        var snapshot = shown.Snapshot;

        var body = NewStack(installation.Name);
        AddEmulatorStatus(body, shown);
        body.Children.Add(Caption("Emulators › Installed"));
        var details = Group(body, "Installation", "installation");
        details.Children.Add(Caption(
            $"{installation.Version} · {installation.Channel} · {installation.Architecture} · {installation.Source}\n"
            + installation.Integrity + "\n" + installation.ExecutablePath + "\nData: " + installation.DataPath));
        var shortcut = Group(body, "Steam shortcut", "steam-shortcut");
        shortcut.IsEnabled = !snapshot.Busy;
        shortcut.Children.Add(ToggleRow("Show emulator in Steam", installation.SteamShortcutEnabled,
            enabled => Run(token => _service!.SetEmulatorShortcutAsync(installationId, enabled,
                installation.SteamShortcutLaunchMode, token))));
        shortcut.Children.Add(ChoiceRow("Launch mode", new[]
            {
                (2, "Input lease"), (0, "No lease"), (1, "De-elevate"), (3, "De-elevate + input lease")
            }, installation.SteamShortcutLaunchMode,
            mode => Run(token => _service!.SetEmulatorShortcutAsync(installationId,
                installation.SteamShortcutEnabled, mode, token))));
        shortcut.Children.Add(Caption(state.ShortcutStatus is { Length: > 0 } status
            ? status
            : "Opens the emulator interface using its current installation. Input lease lets it read the controller directly."));
        var setup = Group(body, "BIOS & firmware", "setup");
        if (installation.DataPolicy.RequiresVisualCpp || installation.DefinitionId == "model2")
        {
            setup.Children.Add(Tagged(Row("Install required runtimes",
                "Install verified Microsoft components, then refresh or repair this emulator",
                Icons.Grid4, snapshot.Busy ? null : () => Run(token =>
                    _service!.InstallEmulatorRuntimeDependenciesAsync(installation.DefinitionId, token))), "emu.runtimes"));
        }
        foreach (var missing in installation.MissingRequirements)
        {
            setup.Children.Add(Caption("Required: " + missing));
        }

        if (snapshot.Definitions.FirstOrDefault(item => item.Id == installation.DefinitionId) is { } definition
            && definition.Prerequisites.Length > 0)
        {
            setup.Children.Add(Tagged(Row("Open BIOS & firmware", state.Bios.Folder ?? "Choose a shared BIOS folder",
                Icons.Grid4, () => Navigate(RenderBios)), "emu.bios"));
            setup.Children.Add(Tagged(Row("Emulator-specific setup",
                "Supply local files or use the native firmware installer",
                Icons.Grid4, () => Navigate(() => RenderEmulatorSetup(installationId))), "emu.setup"));
        }

        var offer = snapshot.Offers.FirstOrDefault(item => item.DefinitionId == installation.DefinitionId
                                                           && item.Channel == installation.Channel
                                                           && item.Architecture == installation.Architecture);
        if (offer is not null)
        {
            var update = Group(body, offer.ReleaseId != installation.ReleaseId ? "Update available" : "Release",
                "release");
            var releaseSection = body.Children[^1];
            body.Children.Remove(releaseSection);
            body.Children.Insert(2, releaseSection);
            update.Children.Add(Caption(offer.Error.Length > 0 ? offer.Error : "Latest: " + offer.Version));
            AddReleaseNotes(update, offer);

            if (offer.ReleaseId.Length > 0 && offer.ReleaseId != installation.ReleaseId)
            {
                update.Children.Add(Tagged(PrimaryRow("Update to " + offer.Version,
                    offer.ReleaseId == installation.IgnoredReleaseId
                        ? "You skipped this version; install it now"
                        : "Preserve settings, saves and supplied firmware",
                    Icons.Restart, snapshot.Busy || !installation.Managed
                        ? null
                        : () => Run(token =>
                            _service!.UpdateEmulatorAsync(installationId, token))), "emu.update"));
                update.Children.Add(Tagged(Row("Skip this version", "Remember this release until another is published",
                        Icons.BlockedCircle, snapshot.Busy
                            ? null
                            : () => Run(token =>
                                _service!.IgnoreEmulatorVersionAsync(installationId, offer.ReleaseId, token))),
                    "emu.skip"));
                var commands = update.Children.OfType<ActionButton>().ToArray();
                foreach (var command in commands)
                {
                    update.Children.Remove(command);
                }

                Commands(update, commands);
                update.Children.Add(Caption("Settings, saves and supplied firmware are preserved."));
            }
        }

        if (installation.Managed)
        {
            if (installation.DataPolicy.HasCores)
            {
                body.Children.Add(Tagged(Row("Update installed cores",
                        "Check the complete core catalogue independently of the frontend release",
                        Icons.Restart,
                        snapshot.Busy
                            ? null
                            : () => Run(token => _service!.UpdateEmulatorAsync(installationId, token))),
                    "emu.cores.update"));
            }

            details.Children.Add(Tagged(Row("Repair installation",
                "Restore the selected package and required core files",
                Icons.Restart, snapshot.Busy
                    ? null
                    : () => Run(token =>
                        _service!.RepairEmulatorAsync(installationId, token))), "emu.repair"));
        }

        var dependency = state.Dependencies.FirstOrDefault(item => item.InstallationId == installationId);
        var warning = dependency?.Summary ?? "Imported ROMs that use this installation will need another emulator.";
        details.Children.Add(Tagged(DangerRow(installation.Managed ? "Remove emulator" : "Forget external installation",
            warning + " Saves and ROMs stay.", Icons.Close, snapshot.Busy
                ? null
                : () => ConfirmCommand("Remove " + installation.Name + "?",
                    warning + " Your ROMs, saves, settings and firmware are preserved.",
                    token => _service!.RemoveEmulatorAsync(installationId, token))), "emu.remove"));
        var maintenance = details.Children.OfType<ActionButton>().ToArray();
        foreach (var command in maintenance)
        {
            details.Children.Remove(command);
        }

        Commands(details, maintenance);
        var installationSection = body.Children.OfType<CollapsibleSection>()
            .First(section => ReferenceEquals(section.Body, details));
        body.Children.Remove(installationSection);
        body.Children.Add(installationSection);
        if (installation.Cores.Length > 0)
        {
            body.Children.Add(Tagged(Row($"Installed cores ({installation.Cores.Length})",
                "Inspect systems, package metadata and required files", Icons.ListLines,
                () => Navigate(() => RenderEmulatorCores(installationId))), "emu.cores"));
        }

        SetEmulatorContent(body, shown);
    }

    private void RenderEmulatorCores(string installationId)
    {
        var shown = ReadShown();
        var installation = shown?.Snapshot.Installations.FirstOrDefault(item => item.Id == installationId);
        if (shown is null || installation is null)
        {
            Back();
            return;
        }

        var page = shown.Page;
        var snapshot = shown.Snapshot;

        var coreStatus = page.CoreStatus;
        var body = NewStack("Installed cores");
        body.Children.Add(Caption(installation.Name + $" · {installation.Cores.Length} cores"));
        body.Children.Add(Tagged(Row("Search cores", _coreSearch, Icons.ListLines, () =>
            EditText("Search cores", _coreSearch, int.MaxValue, value =>
            {
                _coreSearch = value;
                _coreFilter = "matches";
                RenderEmulatorCores(installationId);
            })), "cores.search"));
        var cores = installation.Cores.Where(core => EmulatorPresentation.Matches(_coreSearch,
            core.Name, core.Id, string.Join(" ", core.Systems),
            string.Join(" ", core.Systems.Select(EmulatorPresentation.SystemName)))).ToArray();
        body.Children.Add(ToolTabs(_coreFilter,
            ("all", $"All {installation.Cores.Length}", () => Filter("all")),
            ("matches", $"Matches {cores.Length}", () => Filter("matches")),
            ("files", $"Need files {installation.Cores.Count(NeedsFiles)}", () => Filter("files")),
            ("metadata", $"No metadata {installation.Cores.Count(core => core.MetadataMissing)}",
                () => Filter("metadata"))));
        cores = (_coreFilter == "all" ? installation.Cores : cores)
            .Where(core => _coreFilter != "files" || NeedsFiles(core))
            .Where(core => _coreFilter != "metadata" || core.MetadataMissing).ToArray();
        foreach (var core in cores)
        {
            var row = Tagged(Row(core.Name, string.Join(", ", core.Systems.Select(EmulatorPresentation.SystemName))
                                            + (core.MetadataMissing ? " · Metadata unavailable" : ""),
                Icons.ListLines, () => Navigate(() =>
                {
                    var details = NewStack(core.Name);
                    details.Children.Add(Caption(core.Path));
                    details.Children.Add(Caption("File types: " + string.Join(", ", core.Extensions)));
                    details.Children.Add(Caption(core.MetadataMissing
                        ? "Upstream core metadata is missing. Choose its system manually."
                        : "System mapping supplied by the core metadata."));
                    foreach (var required in core.RequiredFiles)
                    {
                        details.Children.Add(Caption("Required: " + required));
                    }

                    // Fixed facts about the core: the manager's progress must not redraw them.
                    SetStaticContent(details);
                })), "core:" + core.Id);
            row.TrailingText = core.MetadataMissing ? "No metadata" : NeedsFiles(core) ? "Need files" : "Ready";
            body.Children.Add(row);
        }

        body.Children.Add(Tagged(Row("Open BIOS & firmware", "Configure required system files", Icons.Grid4,
            () => Navigate(RenderBios)), "emu.bios"));

        SetEmulatorContent(body, shown);
        return;

        void Filter(string value)
        {
            _coreFilter = value;
            RenderEmulatorCores(installationId);
        }

        bool NeedsFiles(EmulatorCore core)
        {
            return coreStatus.FirstOrDefault(item => item.InstallationId == installationId
                                                    && item.CoreId == core.Id)?.Missing == true;
        }
    }

    private void AddEmulatorStatus(StackPanel body, EmulatorShown shown)
    {
        if (shown.Page.ShortcutStatus is { Length: > 0 } shortcutStatus)
        {
            body.Children.Add(Tagged(Caption(shortcutStatus), "emu.shortcut-status"));
        }

        body.Children.Add(new OverlayProgress("emu.progress", ProgressOf(shown.Progress), "emu.stop"));
    }

    /// <summary>
    ///     The manager's running work and its last result, from the service's progress channel: the status line, a
    ///     bar while it works and Stop while it can be stopped.
    /// </summary>
    private OverlayProgressState ProgressOf(EmulatorProgressState progress)
    {
        return new OverlayProgressState(progress.Status, progress.Busy, null,
            progress.Busy
                ? new OverlayProgressCancel("Stop operation", "Keep the active installation",
                    () => Run(_service!.CancelAsync))
                : null);
    }

    /// <summary>
    ///     Sets an emulator level: the service's progress channel updates its progress element in place, and only a
    ///     new catalogue revision or a change of the manager's busy state, which enables and disables its commands,
    ///     redraws it.
    /// </summary>
    /// <param name="body">The level.</param>
    /// <param name="shown">The reading the level was drawn from.</param>
    private void SetEmulatorContent(StackPanel body, EmulatorShown shown)
    {
        SetLiveContent(body, shown, ReadShown, SameStructure, next => ProgressOf(next.Progress));
    }

    /// <summary>Reads what an emulator level draws: the catalogue revision, its page projection and the progress.</summary>
    /// <returns>The reading, or null when the session has no emulator manager.</returns>
    private EmulatorShown? ReadShown()
    {
        // The revision first: a projection that changes after it is read carries a newer revision, so the next
        // publication sees it as a change and redraws.
        return _service is { } service
            ? new EmulatorShown(service.CatalogRevision, service.ReadPageState(), service.ReadProgressState())
            : null;
    }

    /// <summary>Whether two readings draw the same level apart from the status line and the bar.</summary>
    private static bool SameStructure(EmulatorShown shown, EmulatorShown next)
    {
        return shown.Catalog == next.Catalog && shown.Progress.Busy == next.Progress.Busy;
    }

    /// <summary>What an emulator level was drawn from, split as the service publishes it.</summary>
    /// <param name="Catalog">The service's catalogue revision when it was read.</param>
    /// <param name="Page">The page projection: catalogue, installations, BIOS, choices and shortcut status.</param>
    /// <param name="Progress">The manager's busy state and status line.</param>
    private sealed record EmulatorShown(long Catalog, EmulatorPageState Page, EmulatorProgressState Progress)
    {
        /// <summary>The projection's catalogue with the manager's busy state and status line.</summary>
        internal EmulatorSnapshot Snapshot { get; } =
            Page.Emulators with { Busy = Progress.Busy, Status = Progress.Status };
    }

    private void RenderEmulatorDefaults()
    {
        var shown = ReadShown();
        if (shown is null)
        {
            return;
        }

        var state = shown.Page;
        var snapshot = shown.Snapshot;

        var body = NewStack("System defaults");
        body.Children.Add(ManagerTabs(snapshot));
        AddEmulatorStatus(body, shown);
        body.Children.Add(Caption(
            "New ROM libraries start with these choices. Existing libraries and title overrides keep their selections."));
        foreach (var systemGroup in state.RomSystems.GroupBy(system => EmulatorPresentation.SystemGroup(system.Id)))
        {
            var group = Group(body, systemGroup.Key, "defaults:" + systemGroup.Key);
            foreach (var system in systemGroup)
            {
                AddDefault(group, system.Id, system.Name);
            }
        }

        SetEmulatorContent(body, shown);
        return;

        void AddDefault(StackPanel parent, string systemId, string name)
        {
            var preference = snapshot.SystemPreferences.FirstOrDefault(item => item.SystemId == systemId);
            var choices = state.Choices.FirstOrDefault(item => item.SystemId == systemId)?.Installations ?? [];
            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("1.1*,1.5*,1.2*"),
                ColumnSpacing = 10,
                Tag = "default:" + systemId,
                MinHeight = 44
            };
            row.Children.Add(new TextBlock
            {
                Text = name,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            });
            var emulator = Tagged(new OverlayChoice<string>(
                        new[] { ("", "No default") }.Concat(choices.Select(item => (item.Id, item.Label))).ToArray(),
                        preference?.InstallationId ?? "",
                        value => Run(token => _service!.SetPreferredEmulatorAsync(systemId, value,
                            choices.FirstOrDefault(item => item.Id == value)?.DefaultCoreId ?? "", token)))
            { IsEnabled = !snapshot.Busy, HorizontalAlignment = HorizontalAlignment.Stretch },
                "default.emulator:" + systemId);
            AutomationProperties.SetName(emulator, name + " emulator");
            Grid.SetColumn(emulator, 1);
            row.Children.Add(emulator);
            var selected = choices.FirstOrDefault(item => item.Id == preference?.InstallationId);
            Control core;
            if (selected?.RequiresCore == true)
            {
                core = Tagged(new OverlayChoice<string>(selected.Cores.Select(item => (item.Id, item.Label)).ToArray(),
                            preference?.CoreId ?? selected.DefaultCoreId,
                            value => Run(token =>
                                _service!.SetPreferredEmulatorAsync(systemId, selected.Id, value, token)))
                { IsEnabled = !snapshot.Busy, HorizontalAlignment = HorizontalAlignment.Stretch },
                    "default.core:" + systemId);
                AutomationProperties.SetName(core, name + " core");
            }
            else
            {
                core = Caption(selected is null ? "—" : "Standalone");
                core.VerticalAlignment = VerticalAlignment.Center;
            }

            Grid.SetColumn(core, 2);
            row.Children.Add(core);
            parent.Children.Add(row);
        }
    }

    private void RenderEmulatorSetup(string installationId)
    {
        var shown = ReadShown();
        var installation = shown?.Snapshot.Installations.FirstOrDefault(item => item.Id == installationId);
        var definition = shown?.Snapshot.Definitions.FirstOrDefault(item => item.Id == installation?.DefinitionId);
        if (shown is null || installation is null || definition is null)
        {
            Back();
            return;
        }

        var snapshot = shown.Snapshot;

        var body = NewStack(installation.Name + " setup");
        AddEmulatorStatus(body, shown);
        body.Children.Add(Caption("Choose BIOS, firmware or system files you already have."));
        body.Children.Add(Tagged(Row("Recheck setup", "Refresh required files after completing the emulator's setup",
            Icons.Restart, snapshot.Busy ? null : () => Run(_service!.RefreshEmulatorsAsync)), "emu.setup.recheck"));
        foreach (var prerequisite in definition.Prerequisites)
        {
            var kind = prerequisite.Kind;
            body.Children.Add(SectionLabel(prerequisite.Name));
            body.Children.Add(Caption(prerequisite.Description));
            if (installation.ConfiguredPrerequisites.TryGetValue(kind, out var path))
            {
                body.Children.Add(Caption("Configured: " + path));
            }

            body.Children.Add(Tagged(Row("Choose " + prerequisite.Name + " file", "", Icons.Grid4,
                    snapshot.Busy
                        ? null
                        : () => _ = RunSafelyAsync(ChooseAsync(prerequisite, false), "prerequisite file")),
                "emu.setup.file:" + kind));
            if (prerequisite.AllowDirectory)
            {
                body.Children.Add(Tagged(Row("Choose " + prerequisite.Name + " folder", "", Icons.Grid4,
                        snapshot.Busy
                            ? null
                            : () => _ = RunSafelyAsync(ChooseAsync(prerequisite, true), "prerequisite folder")),
                    "emu.setup.folder:" + kind));
            }
        }

        SetEmulatorContent(body, shown);
        return;

        async Task ChooseAsync(EmulatorPrerequisite prerequisite, bool folder)
        {
            var generation = NavigationGeneration;
            var path = await PickPathAsync(folder, prerequisite.Extensions);
            if (path is not null && generation == NavigationGeneration)
            {
                Run(token =>
                    _service!.ConfigureEmulatorPrerequisiteAsync(installationId, path, prerequisite.Kind, token));
            }
        }
    }
}
