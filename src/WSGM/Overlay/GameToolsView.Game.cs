using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using SteamUiToolkit;
using WSGM.Controls;
using WSGM.Shell;

namespace WSGM.Overlay;

public sealed partial class GameToolsView
{
    private readonly Dictionary<string, Dictionary<string, string>> _draftSettings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _draftProxy = new(StringComparer.Ordinal);
    private uint _appId;
    private string _gameName = "";

    /// <summary>Opens one game's page above the list and starts a scan of its folder.</summary>
    /// <param name="appId">Steam AppID or non-Steam shortcut id.</param>
    /// <param name="name">The library name, shown until the scan reports one; may be empty.</param>
    private void OpenGame(uint appId, string name)
    {
        ClearGame();
        _appId = appId;
        _gameName = name;
        Navigate(RenderGame);
        _ = RunSafelyAsync(ScanAsync(appId), "scan");
    }

    private void ClearGame()
    {
        _appId = 0;
        _gameName = "";
        _draftSettings.Clear();
        _draftProxy.Clear();
    }

    private async Task ScanAsync(uint appId)
    {
        if (_service is not { } service)
        {
            return;
        }

        var generation = NavigationGeneration;
        await Task.Run(() => service.ReadAsync(appId));
        // The service raised Changed as well; this redraw covers a page hidden behind a surface meanwhile.
        if (generation == NavigationGeneration && appId == _appId)
        {
            CurrentLevel?.Invoke();
        }
    }

    private GameToolsState? CurrentState()
    {
        return _service is { } service && _appId != 0 ? service.Peek(_appId) : null;
    }

    private void RenderGame()
    {
        if (CurrentState() is not { } state)
        {
            RenderMessage("Game Tools", "Game Tools is not available in this session.");
            return;
        }

        var name = state.Name.Length > 0 ? state.Name : _gameName.Length > 0 ? _gameName : "Game Tools";
        var body = NewStack(name);
        body.Children.Add(Tagged(Caption(state.InstallFolder.Length > 0
            ? state.InstallFolder
            : "Install folder not reported."), "game.folder"));
        AddProgress(body, state);
        var ready = state.Availability is "ready" or "running";
        if (state.Reason is { Length: > 0 } reason && state.Availability != "ready")
        {
            body.Children.Add(Tagged(Caption(reason), "game.reason"));
        }

        if (ready && !(state.Loading && state.Tools.Count == 0))
        {
            AddAntiCheat(body, state);
            AddRuntime(body, state);
            foreach (var tool in state.Tools)
            {
                AddTool(body, state, tool);
            }

            // Supporting evidence follows the tools so each primary action stays near the top.
            AddDetection(body, state);
        }

        var idle = state.Busy is null;
        body.Children.Add(SectionLabel("Page"));
        body.Children.Add(Tagged(Row("Refresh", "Scan the game folder again", Icons.Restart,
            idle && !state.Loading
                ? () => RunTool((service, token) => service.RefreshAsync(_appId, token), "refresh")
                : null), "game.refresh"));
        if (ready && state.Tools.Count > 0)
        {
            body.Children.Add(Tagged(Row("Check for updates", "Ask for the latest releases now", Icons.ArrowDown,
                idle && state.Tools.All(tool => !tool.CheckingLatest)
                    ? () => RunTool((service, token) => service.CheckUpdatesAsync(_appId, token), "check updates")
                    : null), "game.check-updates"));
        }

        body.Children.Add(Tagged(Row("Back", "Return to the game list", Icons.ArrowLeft, () => Back()),
            "game.back"));
        SetGameContent(body, state, ProgressOf);
    }

    /// <summary>
    ///     Sets a level of the open game: download and phase ticks update its progress element in place, and only
    ///     a structural change of the game's state redraws it.
    /// </summary>
    /// <param name="body">The level.</param>
    /// <param name="state">The state the level was drawn from.</param>
    /// <param name="progress">The level's progress element state, or null for a level without one.</param>
    private void SetGameContent(StackPanel body, GameToolsState state,
        Func<GameToolsState, OverlayProgressState?>? progress)
    {
        SetLiveContent(body, state, CurrentState, SameStructure, progress);
    }

    /// <summary>
    ///     Whether two states of the game draw the same page apart from the running work's phase, amount and
    ///     Cancel availability. Work starting, ending or changing operation is structural, because every action's
    ///     enablement follows it.
    /// </summary>
    private static bool SameStructure(GameToolsState shown, GameToolsState next)
    {
        // The cached scan's lists keep their identity between publications; Peek re-projects only the tools.
        return shown with { Busy = null, Tools = Array.Empty<GameToolsTool>() }
               == next with { Busy = null, Tools = Array.Empty<GameToolsTool>() }
               && shown.Busy?.Operation == next.Busy?.Operation
               && (shown.Busy is null) == (next.Busy is null)
               && shown.Tools.SequenceEqual(next.Tools);
    }

    /// <summary>The game page's progress element: the operation's phase and amount, or the folder scan.</summary>
    private OverlayProgressState? ProgressOf(GameToolsState state)
    {
        if (state.Busy is { } busy)
        {
            var amount = busy.Bytes <= 0
                ? ""
                : busy.Total > 0
                    ? $" · {LibraryManagerService.FormatBytes(busy.Bytes)} of {LibraryManagerService.FormatBytes(busy.Total)}"
                    : $" · {LibraryManagerService.FormatBytes(busy.Bytes)}";
            var appId = state.AppId;
            return new OverlayProgressState(busy.Phase + "…" + amount, true,
                busy.Total > 0 ? (double)busy.Bytes / busy.Total : null,
                busy.Cancellable
                    ? new OverlayProgressCancel("Cancel", "Stop before any game file is changed",
                        () => RunTool((service, token) => service.CancelAsync(appId, token), "cancel"))
                    : null);
        }

        return state.Loading ? new OverlayProgressState("Scanning the game folder…", true) : null;
    }

    private void AddProgress(StackPanel body, GameToolsState state)
    {
        body.Children.Add(new OverlayProgress("game.busy", ProgressOf(state), "game.cancel"));
        AddStatus(body, false, state.Error, state.Notice, "game.result");
    }

    private void AddRuntime(StackPanel body, GameToolsState state)
    {
        body.Children.Add(SectionLabel("Program"));
        var idle = state.Busy is null && state.InstallFolder.Length > 0;
        if (state.Runtime is { } runtime)
        {
            var detail = string.Join(" · ", new[]
            {
                state.EngineLabel, runtime.ArchitectureLabel, runtime.ConfidenceLabel
            }.Where(part => part.Length > 0));
            body.Children.Add(Tagged(Row(runtime.FileName, detail, Icons.Rocket,
                idle ? () => Navigate(RenderCandidates) : null), "runtime.change"));
            body.Children.Add(Tagged(Caption(runtime.RelativePath), "runtime.path"));
            return;
        }

        body.Children.Add(Tagged(Caption(state.RememberedMissing
            ? "The program you chose earlier is gone, maybe after an update. Choose it again."
            : "Choose the program the game runs. WSGM couldn't tell which one it is."), "runtime.prompt"));
        body.Children.Add(Tagged(PrimaryRow("Choose the program…", state.EngineLabel, Icons.Rocket,
            idle ? () => Navigate(RenderCandidates) : null), "runtime.change"));
    }

    private static void AddAntiCheat(StackPanel body, GameToolsState state)
    {
        if (state.AntiCheatWarning is { } warning)
        {
            body.Children.Add(Tagged(Caption(warning), "detect.anticheat"));
        }
    }

    private void AddDetection(StackPanel body, GameToolsState state)
    {
        body.Children.Add(SectionLabel("Found in the game"));
        if (state.Upscalers.Count == 0)
        {
            body.Children.Add(Tagged(Caption(
                "No standalone upscaler library found. The game may still have one built in."), "detect.none"));
        }

        foreach (var upscaler in state.Upscalers)
        {
            var version = upscaler.Version is { Length: > 0 } v ? " " + v : "";
            body.Children.Add(Tagged(Caption($"{upscaler.Technology}{version} · {upscaler.RelativePath}"),
                "detect:" + upscaler.RelativePath));
        }

        if (state.GpuVendors.Count > 0)
        {
            body.Children.Add(Tagged(Caption("Graphics: " + string.Join(", ", state.GpuVendors.Select(VendorLabel))),
                "detect.gpu"));
        }
    }

    private void AddTool(StackPanel body, GameToolsState state, GameToolsTool tool)
    {
        var key = "tool:" + tool.Id;
        var idle = state.Busy is null;
        var installed = tool.InstalledVersion is not null;
        body.Children.Add(SectionLabel(tool.Name));
        body.Children.Add(Tagged(Caption(tool.Description), key + ".description"));
        var versions = installed ? "Installed " + tool.InstalledVersion : "";
        var latest = tool.CheckingLatest
            ? "Checking for the latest release…"
            : tool.LatestVersion is { } tag
                ? "Latest " + tag
                : tool.LatestError ?? "";
        // The status is a sentence; the version facts follow it as a list, as on the Steam page's status note.
        var facts = string.Join(" · ", new[] { versions, latest }.Where(part => part.Length > 0));
        body.Children.Add(Tagged(Caption(string.Join(" ",
                new[] { tool.StatusText, facts }.Where(part => part.Length > 0))),
            key + ".status"));
        if (tool.IncompatibleReason is { Length: > 0 } incompatible)
        {
            body.Children.Add(Tagged(Caption(incompatible), key + ".incompatible"));
        }

        foreach (var conflict in tool.Conflicts)
        {
            body.Children.Add(Tagged(Caption($"{conflict.KindLabel}: {conflict.Path}. {conflict.Message}"),
                key + ".conflict:" + conflict.Path));
        }

        for (var index = 0; index < tool.Warnings.Count; index++)
        {
            body.Children.Add(Tagged(Caption(tool.Warnings[index]), key + ".warning:" + index));
        }

        if (tool.Files.Missing.Count > 0 || tool.Files.Modified.Count > 0)
        {
            body.Children.Add(Tagged(Caption("Changed or missing: "
                                             + string.Join(", ", tool.Files.Missing.Concat(tool.Files.Modified))),
                key + ".files"));
        }

        AddPrimaryAction(body, state, tool, idle);
        if (tool.ShowOptions)
        {
            AddOptions(body, tool, idle && tool.CanEditOptions, installed && tool.Actions.Contains("configure"));
        }

        if (tool.Actions.Contains("remove"))
        {
            body.Children.Add(Tagged(DangerRow("Remove " + tool.Name,
                "Delete the files WSGM added and restore the game's own", Icons.Close,
                idle ? () => ConfirmRemove(state, tool) : null), key + ".remove"));
        }

        body.Children.Add(Tagged(Caption($"{tool.Notice} {tool.Homepage} · {tool.License}"), key + ".about"));
    }

    private void AddPrimaryAction(StackPanel body, GameToolsState state, GameToolsTool tool, bool idle)
    {
        var key = "tool:" + tool.Id;
        if (tool.Actions.Contains("update"))
        {
            body.Children.Add(Tagged(PrimaryRow("Update " + tool.Name,
                tool.LatestVersion is { } tag ? "Install " + tag + " and keep its settings" : "Install the latest release",
                Icons.ArrowDown, idle ? () => StartUpdate(state, tool) : null), key + ".update"));
        }

        if (tool.Actions.Contains("repair"))
        {
            // Repair leads only when files are missing or changed; otherwise it is a supporting command.
            Func<string, string, Geometry?, Action?, ActionButton> row = tool.Status == "modified" ? PrimaryRow : Row;
            body.Children.Add(Tagged(row("Repair " + tool.Name, "Restore its missing or changed files", Icons.Wrench,
                idle ? () => RunTool((service, token) => service.RepairAsync(_appId, tool.Id, token), "repair") : null),
                key + ".repair"));
        }

        if (tool.Actions.Contains("replaceExisting"))
        {
            body.Children.Add(Tagged(PrimaryRow("Replace existing " + tool.Name + " installation",
                "Back up the manual installation and let WSGM manage it", Icons.Restart,
                idle ? () => ConfirmReplace(state, tool) : null), key + ".replace"));
        }

        if (tool.Actions.Contains("install"))
        {
            body.Children.Add(Tagged(PrimaryRow("Install " + tool.Name, "As " + ProxyFor(tool), Icons.ArrowDown,
                idle ? () => StartInstall(state, tool) : null), key + ".install"));
        }
        else if (tool.Status == "notInstalled")
        {
            body.Children.Add(Tagged(PrimaryRow("Install " + tool.Name, tool.IncompatibleReason ?? tool.StatusText,
                Icons.ArrowDown, null), key + ".install"));
        }
    }

    private void AddOptions(StackPanel body, GameToolsTool tool, bool enabled, bool applyNow)
    {
        var key = "tool:" + tool.Id;
        var proxy = ProxyFor(tool);
        var hint = tool.ProxyOptions.FirstOrDefault(option => option.Name == proxy)?.Hint ?? "";
        body.Children.Add(Tagged(Row("File name: " + proxy, hint, Icons.ListLines,
            enabled ? () => Navigate(() => RenderProxy(tool.Id)) : null), key + ".proxy"));
        foreach (var setting in tool.Settings)
        {
            var id = setting.Id;
            var value = applyNow
                ? setting.Value
                : _draftSettings.GetValueOrDefault(tool.Id)?.GetValueOrDefault(id) ?? setting.Value;
            var options = setting.Options.Select(option => (option.Value, option.Label)).ToList();
            if (!options.Any(option => option.Value == value))
            {
                // The file holds a value no option names; show it as is instead of claiming a default.
                options.Insert(0, (value, setting.IniValue is { Length: > 0 } raw ? "Custom (" + raw + ")" : "Custom"));
            }

            var row = Tagged(ChoiceRow(setting.Label, options, value, chosen =>
            {
                if (chosen == value || chosen.Length == 0)
                {
                    return;
                }

                if (applyNow)
                {
                    RunTool((service, token) => service.ConfigureAsync(_appId, tool.Id, null,
                        new Dictionary<string, string> { [id] = chosen }, token), "configure");
                    return;
                }

                if (!_draftSettings.TryGetValue(tool.Id, out var draft))
                {
                    draft = new Dictionary<string, string>(StringComparer.Ordinal);
                    _draftSettings[tool.Id] = draft;
                }

                draft[id] = chosen;
                CurrentLevel?.Invoke();
            }), key + ".setting:" + id);
            row.IsEnabled = enabled && setting.Available;
            body.Children.Add(row);
            var note = string.Join(" ", new[] { setting.Description, setting.Note ?? "" }.Where(part => part.Length > 0));
            body.Children.Add(Tagged(Caption(note), key + ".setting-note:" + id));
        }
    }

    private void RenderProxy(string toolId)
    {
        // One read: the level registers the state it was drawn from, so a later change still redraws it.
        if (CurrentState() is not { } state || state.Tools.FirstOrDefault(tool => tool.Id == toolId) is not { } tool)
        {
            Back();
            return;
        }

        var installed = tool.InstalledVersion is not null;
        var enabled = state.Busy is null && tool.CanEditOptions;
        var body = NewStack(tool.Name + " file name");
        body.Children.Add(Tagged(Caption(tool.ProxyIntro), "proxy.intro"));
        var current = ProxyFor(tool);
        foreach (var option in tool.ProxyOptions)
        {
            var name = option.Name;
            var description = option.Conflict ?? option.Hint;
            var choose = enabled && option.Available && name != current
                ? () => ChooseProxy(tool, name, installed)
                : (Action?)null;
            // The current name stays readable and active; choosing it again keeps it and returns.
            var row = name == current
                ? PrimaryRow(name + " (selected)", description, Icons.ListLines, () => Back())
                : Row(name, description, Icons.ListLines, choose);
            body.Children.Add(Tagged(row, "proxy:" + name));
        }

        body.Children.Add(Tagged(Row("Back", "", Icons.ArrowLeft, () => Back()), "proxy.back"));
        SetGameContent(body, state, null);
    }

    private void ChooseProxy(GameToolsTool tool, string name, bool installed)
    {
        if (installed)
        {
            RunTool((service, token) => service.ConfigureAsync(_appId, tool.Id, name,
                new Dictionary<string, string>(), token), "rename", () => Back());
            return;
        }

        _draftProxy[tool.Id] = name;
        Back();
    }

    private void RenderCandidates()
    {
        if (CurrentState() is not { } state)
        {
            Back();
            return;
        }

        var body = NewStack("Choose the program");
        body.Children.Add(Tagged(Caption(
            "Pick the program that runs the game itself, not its launcher. WSGM installs tools beside it."),
            "candidates.intro"));
        if (state.ScanTruncated)
        {
            body.Children.Add(Tagged(Caption(
                "The folder is too large to list completely. Use Browse if the program is missing."),
                "candidates.truncated"));
        }

        var idle = state.Busy is null;
        var folder = state.InstallFolder;
        body.Children.Add(Tagged(Row("Browse…", "Choose the program file yourself", Icons.FolderPlus,
            idle ? () => _ = RunSafelyAsync(BrowseAsync(folder), "browse") : null), "candidates.browse"));
        if (state.Candidates.Count == 0)
        {
            body.Children.Add(Tagged(Caption("No programs were found in the game folder."), "candidates.empty"));
        }

        foreach (var candidate in state.Candidates)
        {
            var path = candidate.RelativePath;
            var detail = string.Join(" · ", new[]
            {
                candidate.RoleLabel, candidate.ArchitectureLabel, path
            }.Where(part => part.Length > 0));
            if (candidate.Reasons.Count > 0)
            {
                detail += "\n" + string.Join(". ", candidate.Reasons);
            }

            var choose = idle && !candidate.Chosen ? () => ChooseRuntime(path) : (Action?)null;
            var row = candidate.Chosen
                ? PrimaryRow(candidate.FileName + " (current)", detail, Icons.Rocket, () => Back())
                : Row(candidate.FileName, detail, Icons.Rocket, choose);
            body.Children.Add(Tagged(row, "candidate:" + path));
        }

        body.Children.Add(Tagged(Row("Back", "", Icons.ArrowLeft, () => Back()), "candidates.back"));
        SetGameContent(body, state, null);
    }

    private void ChooseRuntime(string path)
    {
        var appId = _appId;
        RunTool((service, token) => service.ChooseRuntimeAsync(appId, path, token), "choose program", () => Back());
    }

    private async Task BrowseAsync(string folder)
    {
        if (TopLevel.GetTopLevel(this) is not OverlayWindow window)
        {
            return;
        }

        var generation = NavigationGeneration;
        var appId = _appId;
        var picker = new OverlayFilePicker(false, [".exe"], start: folder.Length > 0 ? folder : null);
        var path = await window.PickLocalPathAsync(picker, false);
        if (path is null || generation != NavigationGeneration || appId != _appId)
        {
            return;
        }

        ChooseRuntime(path);
    }

    private void StartInstall(GameToolsState state, GameToolsTool tool)
    {
        var request = InstallRequest(tool, state.AntiCheatWarning is not null, false);
        if (state.AntiCheatWarning is null)
        {
            RunTool((service, token) => service.InstallAsync(request, token), "install");
            return;
        }

        ConfirmTool("Install " + tool.Name,
            $"{state.AntiCheatWarning} "
            + $"Install {tool.Name} for {state.Name} anyway?",
            service => service.InstallAsync(request, default));
    }

    private void ConfirmReplace(GameToolsState state, GameToolsTool tool)
    {
        var request = InstallRequest(tool, state.AntiCheatWarning is not null, true);
        var risk = state.AntiCheatWarning is { } warning ? " " + warning : "";
        ConfirmTool("Replace existing " + tool.Name,
            $"WSGM backs up the {tool.Name} files that were installed outside WSGM in {state.Name}, removes them and "
            + $"installs its own copy. Removing it later restores the backed-up files.{risk}",
            service => service.InstallAsync(request, default));
    }

    private void StartUpdate(GameToolsState state, GameToolsTool tool)
    {
        if (!tool.UpdateChangesConfig)
        {
            RunTool((service, token) => service.UpdateAsync(_appId, tool.Id, false, token), "update");
            return;
        }

        var appId = _appId;
        ConfirmTool("Update " + tool.Name,
            $"This update changes the {tool.Name} settings file format. WSGM replaces the settings file in "
            + $"{state.Name} with the new default, so changes made in {tool.Name}'s menu are lost.",
            service => service.UpdateAsync(appId, tool.Id, true, default));
    }

    private void ConfirmRemove(GameToolsState state, GameToolsTool tool)
    {
        var appId = _appId;
        ConfirmTool("Remove " + tool.Name,
            $"Remove {tool.Name} from {state.Name}? WSGM deletes the files it added, including the settings file, "
            + "and restores the game files it replaced. Files the game changed since are left in place.",
            service => service.RemoveAsync(appId, tool.Id, default));
    }

    private void ConfirmTool(string title, string message,
        Func<IGameToolsSource, Task<SteamUiCommandResult>> command)
    {
        if (_service is not { } service)
        {
            return;
        }

        ConfirmCommand(title, message, _ => command(service));
    }

    private GameToolsInstallRequest InstallRequest(GameToolsTool tool, bool acknowledgeRisk, bool replaceExisting)
    {
        var settings = _draftSettings.TryGetValue(tool.Id, out var draft)
            ? new Dictionary<string, string>(draft, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        // Send what the page shows: a draft that is no longer offered falls back to the service's name.
        var shown = ProxyFor(tool);
        var proxy = shown == tool.ProxyName ? null : shown;
        return new GameToolsInstallRequest(_appId, tool.Id, proxy, settings, acknowledgeRisk, replaceExisting);
    }

    /// <summary>The draft name while not installed and still offered, otherwise the service's name.</summary>
    private string ProxyFor(GameToolsTool tool)
    {
        if (tool.InstalledVersion is null && _draftProxy.TryGetValue(tool.Id, out var draft)
                                          && tool.ProxyOptions.Any(option => option.Name == draft && option.Available))
        {
            return draft;
        }

        return tool.ProxyName;
    }

    private static string VendorLabel(string vendor)
    {
        return vendor switch
        {
            "amd" => "AMD",
            "intel" => "Intel",
            "nvidia" => "NVIDIA",
            _ => vendor
        };
    }
}
