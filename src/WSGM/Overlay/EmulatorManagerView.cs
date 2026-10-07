using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>The independent emulator tool over its one session-owned backend.</summary>
public sealed class EmulatorManagerView : ServiceSubView
{
    private string _coreSearch = "";

    private string _defaultSystem = "";
    private string _emulatorChannel = "";
    private IEmulatorBackend? _service;

    /// <inheritdoc />
    protected override string LogScope => "Emulator Downloader / Updater";

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
        RenderEmulators();
    }

    private void RenderEmulators()
    {
        var body = NewStack("Emulator Downloader / Updater");
        var snapshot = _service?.ReadState();
        if (snapshot is null)
        {
            body.Children.Add(Caption("Emulator information is not available in this session."));
            SetContent(body);
            return;
        }

        AddEmulatorStatus(body, snapshot);
        body.Children.Add(Tagged(Row("Check for updates", "Refresh available releases from their sources",
            Icons.Restart, snapshot.Busy ? null : () => Run(_service!.RefreshEmulatorsAsync)), "emu.refresh"));
        body.Children.Add(Tagged(Row("System defaults", "Choose the emulator and core new ROM libraries start with",
            Icons.ListLines, () => Navigate(RenderEmulatorDefaults)), "emu.defaults"));
        foreach (var definition in snapshot.Definitions)
        {
            var id = definition.Id;
            var installations = snapshot.Installations.Where(item => item.DefinitionId == id).ToArray();
            var detail = installations.Length == 0
                ? "Not installed"
                : string.Join(" · ", installations.Select(item =>
                    $"{item.Version} ({item.Channel}, {(item.Managed ? "managed" : "external")})"));
            body.Children.Add(Tagged(Row(definition.Name, detail, Icons.Grid4, () =>
            {
                _emulatorChannel = definition.Channels.FirstOrDefault() ?? "";
                Navigate(() => RenderEmulator(id));
            }), "emu:" + id));
        }

        SetContent(body);
    }

    private void RenderEmulator(string definitionId)
    {
        var state = _service?.ReadPageState();
        var snapshot = _service?.ReadState();
        var definition = snapshot?.Definitions.FirstOrDefault(item => item.Id == definitionId);
        if (snapshot is null || definition is null)
        {
            RenderMessage("Emulator", "That emulator is no longer listed.");
            return;
        }

        var body = NewStack(definition.Name);
        AddEmulatorStatus(body, snapshot);
        body.Children.Add(Caption(definition.Source));
        body.Children.Add(Caption("Systems: " + string.Join(", ", definition.Systems)));
        body.Children.Add(ChoiceRow("Release channel",
            definition.Channels.Select(channel => (channel, channel)).ToArray(),
            _emulatorChannel, value =>
            {
                _emulatorChannel = value;
                RenderEmulator(definitionId);
            }));
        var offer = snapshot.Offers.FirstOrDefault(item => item.DefinitionId == definitionId
                                                           && item.Channel == _emulatorChannel
                                                           && item.Architecture == state?.Architecture);
        body.Children.Add(Caption(offer is null ? "Check for updates to read the latest release."
            : offer.Error.Length > 0 ? offer.Error : "Latest: " + offer.Version));
        AddReleaseNotes(body, offer);

        body.Children.Add(Tagged(PrimaryRow("Install " + _emulatorChannel,
            definition.DataPolicy.HasCores
                ? "Installs RetroArch and the complete published Windows core catalogue"
                : "Download and manage this emulator",
            Icons.Grid4, snapshot.Busy
                ? null
                : () => Run(token =>
                    _service!.InstallEmulatorAsync(definitionId, _emulatorChannel, token))), "emu.install"));
        body.Children.Add(Tagged(Row("Use an existing installation", "Choose this emulator's executable",
                Icons.Grid4, snapshot.Busy ? null : () => _ = RunSafelyAsync(ChooseExternalAsync(), "choose emulator")),
            "emu.external"));
        foreach (var installation in snapshot.Installations.Where(item => item.DefinitionId == definitionId))
        {
            var id = installation.Id;
            body.Children.Add(Tagged(Row(installation.Name + " " + installation.Version,
                $"{installation.Channel} · {(installation.Managed ? "Managed" : "External")} · {installation.Architecture}",
                Icons.ListLines, () => Navigate(() => RenderEmulatorInstallation(id))), "emu.installation:" + id));
        }

        SetContent(body);
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
        var state = _service?.ReadPageState();
        var snapshot = _service?.ReadState();
        var installation = snapshot?.Installations.FirstOrDefault(item => item.Id == installationId);
        if (snapshot is null || installation is null)
        {
            RenderMessage("Installation", "That installation is no longer registered.");
            return;
        }

        var body = NewStack(installation.Name);
        AddEmulatorStatus(body, snapshot);
        body.Children.Add(
            Caption($"Installed: {installation.Version} · {installation.Channel} · {installation.Architecture}"));
        body.Children.Add(Caption(installation.ExecutablePath));
        body.Children.Add(Caption("Data: " + installation.DataPath));
        body.Children.Add(Caption("Source: " + installation.Source));
        body.Children.Add(Caption("Verification: " + installation.Integrity));
        foreach (var missing in installation.MissingRequirements)
        {
            body.Children.Add(Caption("Required: " + missing));
        }

        if (snapshot.Definitions.FirstOrDefault(item => item.Id == installation.DefinitionId) is { } definition
            && definition.Prerequisites.Length > 0)
        {
            body.Children.Add(Tagged(Row("BIOS, firmware and system files", "Configure this emulator's required files",
                Icons.Grid4, () => Navigate(() => RenderEmulatorSetup(installationId))), "emu.setup"));
        }

        var offer = snapshot.Offers.FirstOrDefault(item => item.DefinitionId == installation.DefinitionId
                                                           && item.Channel == installation.Channel
                                                           && item.Architecture == installation.Architecture);
        if (offer is not null)
        {
            body.Children.Add(Caption(offer.Error.Length > 0 ? offer.Error : "Latest: " + offer.Version));
            AddReleaseNotes(body, offer);

            if (offer.ReleaseId.Length > 0 && offer.ReleaseId != installation.ReleaseId)
            {
                body.Children.Add(Tagged(PrimaryRow("Update to " + offer.Version,
                    offer.ReleaseId == installation.IgnoredReleaseId
                        ? "You skipped this version; install it now"
                        : "Preserve settings, saves and supplied firmware",
                    Icons.Restart, snapshot.Busy || !installation.Managed
                        ? null
                        : () => Run(token =>
                            _service!.UpdateEmulatorAsync(installationId, token))), "emu.update"));
                body.Children.Add(Tagged(Row("Skip this version", "Remember this release until another is published",
                        Icons.BlockedCircle, snapshot.Busy
                            ? null
                            : () => Run(token =>
                                _service!.IgnoreEmulatorVersionAsync(installationId, offer.ReleaseId, token))),
                    "emu.skip"));
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

            body.Children.Add(Tagged(Row("Repair installation", "Restore the selected package and required core files",
                Icons.Restart, snapshot.Busy
                    ? null
                    : () => Run(token =>
                        _service!.RepairEmulatorAsync(installationId, token))), "emu.repair"));
        }

        var dependency = state?.Dependencies.FirstOrDefault(item => item.InstallationId == installationId);
        var warning = dependency?.Summary ?? "Imported ROMs that use this installation will need another emulator.";
        body.Children.Add(Tagged(DangerRow(installation.Managed ? "Remove emulator" : "Forget external installation",
            warning + " Saves and ROMs stay.", Icons.Close, snapshot.Busy
                ? null
                : () => ConfirmCommand("Remove " + installation.Name + "?",
                    warning + " Your ROMs, saves, settings and firmware are preserved.",
                    token => _service!.RemoveEmulatorAsync(installationId, token))), "emu.remove"));
        if (installation.Cores.Length > 0)
        {
            body.Children.Add(Tagged(Row($"Installed cores ({installation.Cores.Length})",
                "Inspect systems, package metadata and required files", Icons.ListLines,
                () => Navigate(() => RenderEmulatorCores(installationId))), "emu.cores"));
        }

        SetContent(body);
    }

    private void RenderEmulatorCores(string installationId)
    {
        var installation = _service?.ReadState()?.Installations
            .FirstOrDefault(item => item.Id == installationId);
        if (installation is null)
        {
            Back();
            return;
        }

        var body = NewStack("Installed cores");
        body.Children.Add(Tagged(Row("Search cores", _coreSearch, Icons.ListLines, () =>
            EditText("Search cores", _coreSearch, int.MaxValue, value =>
            {
                _coreSearch = value;
                RenderEmulatorCores(installationId);
            })), "cores.search"));
        var cores = installation.Cores.Where(core => core.Name.Contains(_coreSearch, StringComparison.OrdinalIgnoreCase)
                                                     || core.Id.Contains(_coreSearch,
                                                         StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var core in cores)
        {
            body.Children.Add(Tagged(Row(core.Name, string.Join(", ", core.Systems)
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

                    SetContent(details);
                })), "core:" + core.Id));
        }

        SetContent(body);
    }

    private void AddEmulatorStatus(StackPanel body, EmulatorSnapshot snapshot)
    {
        if (snapshot.Status.Length > 0)
        {
            body.Children.Add(Caption(snapshot.Status));
        }

        if (snapshot.Busy)
        {
            body.Children.Add(Tagged(Row("Stop operation", "Keep the active installation", Icons.Close,
                () => Run(_service!.CancelAsync)), "emu.stop"));
        }
    }

    private void RenderEmulatorDefaults()
    {
        var state = _service?.ReadPageState();
        var snapshot = _service?.ReadState();
        if (state is null || snapshot is null)
        {
            return;
        }

        var systems = state.RomSystems;
        if (_defaultSystem.Length == 0)
        {
            _defaultSystem = systems.FirstOrDefault()?.Id ?? "";
        }

        var body = NewStack("System defaults");
        AddEmulatorStatus(body, snapshot);
        body.Children.Add(Caption(
            "New ROM libraries start with these choices. Existing libraries and title overrides keep their selections."));
        body.Children.Add(ChoiceRow("System", systems.Select(system => (system.Id, system.Name)).ToArray(),
            _defaultSystem,
            value =>
            {
                _defaultSystem = value;
                RenderEmulatorDefaults();
            }));
        var preference = snapshot.SystemPreferences.FirstOrDefault(item => item.SystemId == _defaultSystem);
        var systemId = _defaultSystem;
        var choices = state.Choices.FirstOrDefault(item => item.SystemId == systemId)?.Installations ?? [];
        body.Children.Add(ChoiceRow("Emulator",
            new[] { ("", "No default") }.Concat(choices.Select(item => (item.Id, item.Label))).ToArray(),
            preference?.InstallationId ?? "",
            value => Run(token => _service!.SetPreferredEmulatorAsync(systemId, value,
                choices.FirstOrDefault(item => item.Id == value)?.DefaultCoreId ?? "", token))));
        var selected = choices.FirstOrDefault(item => item.Id == preference?.InstallationId);
        if (selected?.RequiresCore == true)
        {
            body.Children.Add(ChoiceRow("Core", selected.Cores.Select(core => (core.Id, core.Label)).ToArray(),
                preference?.CoreId ?? selected.DefaultCoreId,
                value => Run(token => _service!.SetPreferredEmulatorAsync(systemId, selected.Id, value, token))));
        }

        SetContent(body);
    }

    private void RenderEmulatorSetup(string installationId)
    {
        var snapshot = _service?.ReadState();
        var installation = snapshot?.Installations.FirstOrDefault(item => item.Id == installationId);
        var definition = snapshot?.Definitions.FirstOrDefault(item => item.Id == installation?.DefinitionId);
        if (snapshot is null || installation is null || definition is null)
        {
            Back();
            return;
        }

        var body = NewStack(installation.Name + " setup");
        AddEmulatorStatus(body, snapshot);
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

        SetContent(body);
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
