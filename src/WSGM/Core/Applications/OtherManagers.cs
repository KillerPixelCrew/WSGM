using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LibHandheld;
using WSGM.Shared;

namespace WSGM.Core;

/// <summary>
///     Finds Handheld Companion and the handheld makers' own apps, and turns off how they start, so WSGM's
///     Full mode is the one manager of the device. Modelled on Handheld Companion's OEM app management:
///     services are set to disabled and stopped, tasks are disabled, and a running window is asked to close
///     but never ended. Everything is recorded first and put back by the uninstaller. Which apps exist and
///     what they run as comes from <see cref="HandheldDevice.ManagerApps" />; when and how to turn them off
///     stays here.
/// </summary>
public static class OtherManagers
{
    /// <summary>The one-shot that turns the managers off from an elevated instance.</summary>
    public const string DisableArgument = "--disable-other-managers";

    private const string ServiceKind = "service";
    private const string TaskKind = "task";
    internal const string PackageKind = "package";
    internal const string DeviceKind = "device";

    /// <summary>The managers WSGM knows: Handheld Companion, then each handheld family's own app.</summary>
    /// <remarks>
    ///     The makers' apps and their process, service, task, package and keyboard identities are device
    ///     knowledge from LibHandheld's static catalog, so Settings and setup list them with or without a
    ///     detected device. An entry bound to one definition is still considered only on that model.
    /// </remarks>
    public static IReadOnlyList<OtherManager> Known { get; } =
    [
        new("handheld-companion", "Handheld Companion", ["HandheldCompanion"], [], ["HandheldCompanion"]),
        .. HandheldDevice.ManagerApps.Select(OtherManager.From)
    ];

    /// <summary>Finds the known managers that would start or run on this machine.</summary>
    /// <param name="autostart">The startup surfaces; the live ones by default.</param>
    /// <param name="services">The services; the live ones by default.</param>
    /// <param name="running">Running process names; the live list by default.</param>
    /// <param name="targets">Package/device backend; live only when the other detection backends are live.</param>
    /// <returns>The active managers, in catalog order.</returns>
    public static IReadOnlyList<DetectedManager> Detect(IAutostartSystem? autostart = null,
        IServiceSystem? services = null, IReadOnlyCollection<string>? running = null,
        IOtherManagerTargets? targets = null)
    {
        if (autostart is null && services is null && running is null)
        {
            targets ??= new OtherManagerWindowsTargets();
        }
        autostart ??= new AutostartSystem();
        services ??= new ServiceSystem();
        running ??= RunningProcessNames();
        IReadOnlyDictionary<string, string> logonTasks;
        try
        {
            logonTasks = autostart.ReadLogonTasks();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn("Other managers: logon tasks could not be read: " + ex.Message);
            logonTasks = new Dictionary<string, string>();
        }

        List<DetectedManager> found = [];
        foreach (var manager in Known)
        {
            if (manager.DeviceDefinitionId is { } definition && !MatchesDevice(targets, definition))
            {
                continue;
            }
            List<string> activeServices =
                [.. manager.Services.Where(service => services.ReadStart(service, out _) is not (null or 4))];
            // A catalog task counts only when it exists and is enabled.
            HashSet<string> tasks = new(StringComparer.OrdinalIgnoreCase);
            foreach (var task in manager.Tasks.Select(name => @"\" + name)
                         .Where(path => autostart.ReadTaskEnabled(path) == true))
            {
                tasks.Add(task);
            }

            // A logon task that runs one of the manager's programs is its autostart, whatever it is called.
            foreach (var (path, command) in logonTasks)
            {
                if (manager.Processes.Contains(ExecutableName(command), StringComparer.OrdinalIgnoreCase)
                    && autostart.ReadTaskEnabled(path) == true)
                {
                    tasks.Add(path);
                }
            }

            DetectedManager detected = new(manager, activeServices, [.. tasks.Order(StringComparer.OrdinalIgnoreCase)],
                [.. manager.Processes.Where(process => running.Contains(process, StringComparer.OrdinalIgnoreCase))])
            {
                StartsAtSignIn = tasks.Count > 0 && tasks.All(logonTasks.ContainsKey),
                Targets = DetectTargets(targets, manager)
            };
            if (detected.Active)
            {
                found.Add(detected);
            }
        }

        return found;
    }

    // A failing package or device backend costs only its own targets, never the whole detection.
    private static bool MatchesDevice(IOtherManagerTargets? targets, string definition)
    {
        try
        {
            return targets?.MatchesDevice(definition) == true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn("Other managers: the handheld could not be identified: " + ex.Message);
            return false;
        }
    }

    private static IReadOnlyList<OtherManagerRecord> DetectTargets(IOtherManagerTargets? targets, OtherManager manager)
    {
        try
        {
            return targets?.Detect(manager) ?? [];
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"Other managers: {manager.Label} packages or devices could not be read: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    ///     Turns off how each detected manager starts and asks its windows to close. Each change is recorded
    ///     before it is made; a failed change is reported, not retried.
    /// </summary>
    /// <param name="detected">What <see cref="Detect" /> found.</param>
    /// <param name="record">Stores a record before its change.</param>
    /// <param name="autostart">The startup surfaces; the live ones by default.</param>
    /// <param name="services">The services; the live ones by default.</param>
    /// <param name="closeWindows">Asks the named processes to close and returns those still running.</param>
    /// <param name="cancellationToken">Refuses further items once shutdown begins.</param>
    /// <param name="targets">Package/device backend; live by default.</param>
    /// <returns>What changed.</returns>
    public static OtherManagersResult Disable(IReadOnlyList<DetectedManager> detected,
        Action<OtherManagerRecord> record,
        IAutostartSystem? autostart = null, IServiceSystem? services = null,
        Func<IReadOnlyList<string>, IReadOnlyList<string>>? closeWindows = null,
        CancellationToken cancellationToken = default, IOtherManagerTargets? targets = null)
    {
        ArgumentNullException.ThrowIfNull(detected);
        ArgumentNullException.ThrowIfNull(record);
        autostart ??= new AutostartSystem();
        services ??= new ServiceSystem();
        closeWindows ??= CloseWindows;
        targets ??= new OtherManagerWindowsTargets();
        List<string> disabled = [];
        List<string> failed = [];
        var serviceRestartRequired = false;
        foreach (var manager in detected)
        {
            foreach (var target in manager.Targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                record(target);
                (targets.Disable(target) ? disabled : failed).Add($"{manager.Manager.Label} {target.Kind} {target.Name}");
            }

            foreach (var task in manager.Tasks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                record(new OtherManagerRecord { ManagerId = manager.Manager.Id, Kind = TaskKind, Name = task });
                (autostart.SetTaskEnabled(task, false) ? disabled : failed).Add($"{manager.Manager.Label} task {task}");
            }

            foreach (var service in manager.Services)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var start = services.ReadStart(service, out var delayed);
                if (start is null or 4)
                {
                    continue;
                }

                record(new OtherManagerRecord
                {
                    ManagerId = manager.Manager.Id, Kind = ServiceKind, Name = service, PreviousStart = start.Value,
                    PreviousDelayed = delayed
                });
                if (services.SetStart(service, 4, false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    serviceRestartRequired |= !services.Stop(service);
                    disabled.Add($"{manager.Manager.Label} service {service}");
                }
                else
                {
                    failed.Add($"{manager.Manager.Label} service {service}");
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var stillRunning = closeWindows([.. detected.SelectMany(manager => manager.Running)]);
        Log.Info($"Other managers: turned off {disabled.Count}, failed {failed.Count}, still running "
                 + $"[{string.Join(", ", stillRunning)}].");
        return new OtherManagersResult(disabled, failed, stillRunning)
        {
            RestartRequired = serviceRestartRequired || targets.RestartRequired || stillRunning.Count > 0
                && detected.Any(manager => manager.Manager.PackageFamilies.Count > 0
                    && manager.Running.Any(name => stillRunning.Contains(name, StringComparer.OrdinalIgnoreCase)))
        };
    }

    /// <summary>
    ///     Turns the detected managers off from this process when it is elevated, or through the elevated
    ///     one-shot when a prompt is acceptable. Services and tasks need an administrator; nothing partial
    ///     is attempted without one, because a closed helper the service restarts would only hide the state.
    /// </summary>
    /// <param name="store">The process-owned configuration persistence the takeover is recorded in.</param>
    /// <param name="detected">What <see cref="Detect" /> found.</param>
    /// <param name="allowElevation">
    ///     Whether an elevation prompt is acceptable here. False at a shell start, where a prompt over
    ///     the booting desktop would be hostile.
    /// </param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>What this attempt achieved; everything failed when the process could not change it.</returns>
    public static OtherManagersResult Apply(ConfigStore store, IReadOnlyList<DetectedManager> detected,
        bool allowElevation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(detected);
        if (detected.Count == 0)
        {
            return new OtherManagersResult([], [], []);
        }

        if (ElevationCheck.IsCurrentProcessElevated() is true)
        {
            return Disable(detected, entry => Record(store, entry), cancellationToken: cancellationToken);
        }

        if (!allowElevation)
        {
            Log.Warn("Other managers: " + string.Join("; ", detected.Select(manager => manager.Describe()))
                                        + " need an elevated WSGM and were left as they are.");
            return new OtherManagersResult([], [.. detected.Select(manager => manager.Describe())],
                [.. detected.SelectMany(manager => manager.Running)]);
        }

        // One prompt for everything. The elevated instance detects and decides for itself; no name
        // from this side reaches its command line.
        var elevatedExit = SelfElevation.RunElevatedActionExitCode(DisableArgument, "Other managers takeover");
        var remaining = Detect();
        List<string> disabled = [];
        List<string> failed = [];
        foreach (var manager in detected)
        {
            var left = remaining.FirstOrDefault(other => other.Manager.Id == manager.Manager.Id);
            foreach (var task in manager.Tasks)
            {
                (left?.Tasks.Contains(task, StringComparer.OrdinalIgnoreCase) == true ? failed : disabled)
                    .Add($"{manager.Manager.Label} task {task}");
            }

            foreach (var service in manager.Services)
            {
                (left?.Services.Contains(service, StringComparer.OrdinalIgnoreCase) == true ? failed : disabled)
                    .Add($"{manager.Manager.Label} service {service}");
            }

            foreach (var target in manager.Targets)
            {
                (left?.Targets.Any(item => Key(item) == Key(target)) == true && elevatedExit != SessionProtocolNames.RestartRequired ? failed : disabled)
                    .Add($"{manager.Manager.Label} {target.Kind} {target.Name}");
            }
        }

        return new OtherManagersResult(disabled, failed, [.. remaining.SelectMany(manager => manager.Running)])
        {
            RestartRequired = elevatedExit == SessionProtocolNames.RestartRequired
        };
    }

    /// <summary>
    ///     Re-checks at a shell start once the takeover has been accepted, so a manager that came back is
    ///     turned off again: Handheld Companion's uninstaller re-enables the maker's services, and an OEM
    ///     update can re-register packages. Never prompts; an unelevated WSGM only logs what it found.
    /// </summary>
    /// <param name="store">Strict persistence used to read accepted takeover and record pre-change state.</param>
    /// <param name="closeRunning">
    ///     Whether running manager windows are asked to close. Only the shell start does; a later re-check
    ///     must not close an app the user opened on purpose.
    /// </param>
    /// <param name="cancellationToken">Stops further takeover items; cancellation is absorbed during startup.</param>
    public static void ReapplyAtStart(ConfigStore store, bool closeRunning = true,
        CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!store.Read().RequireConfig().OtherManagersTakeoverAccepted)
            {
                return;
            }

            var detected = Detect();
            if (!closeRunning)
            {
                detected = [.. detected.Select(manager => manager with { Running = [] }).Where(manager => manager.Active)];
            }

            if (detected.Count == 0)
            {
                return;
            }

            Log.Info("Other managers: " + string.Join("; ", detected.Select(manager => manager.Describe()))
                                        + " are back; turning them off again.");
            var result = Apply(store, detected, false, cancellationToken);
            if (result.Failed.Count > 0 || result.StillRunning.Count > 0)
            {
                Log.Warn($"Other managers: failed [{string.Join(", ", result.Failed)}], still running "
                         + $"[{string.Join(", ", result.StillRunning)}].");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"Other managers re-check failed: {ex.Message}");
        }
    }

    /// <summary>The elevated one-shot: detects and turns off what only an elevated process can.</summary>
    /// <returns>Zero when every service and task could be changed.</returns>
    /// <param name="store">Strict persistence receiving recovery records before each change.</param>
    public static int RunElevatedDisable(ConfigStore store)
    {
        try
        {
            var detected = Detect();
            var result = Disable(detected, entry => Record(store, entry));
            return result.Failed.Count > 0 ? 1 : result.RestartRequired ? SessionProtocolNames.RestartRequired : 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("Other managers takeover failed", ex);
            return 1;
        }
    }

    /// <summary>Reconciles accepted takeover at startup and every five minutes until the session stops.</summary>
    /// <param name="store">The session's strict configuration and recovery store.</param>
    /// <param name="cancellationToken">The owning session shutdown token.</param>
    /// <returns>Completion after cancellation; each reconciliation failure is logged without retrying its writes.</returns>
    public static async Task MaintainAsync(ConfigStore store, CancellationToken cancellationToken)
    {
        try
        {
            ReapplyAtStart(store, cancellationToken: cancellationToken);
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                ReapplyAtStart(store, closeRunning: false, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    ///     One line for Settings from what was recorded, such as
    ///     <c>Armoury Crate and Handheld Companion: 3 services and 1 scheduled task are turned off</c>.
    /// </summary>
    /// <param name="records">The recorded changes.</param>
    /// <returns>The line, or an empty string when nothing was recorded.</returns>
    public static string DescribeRecords(IReadOnlyList<OtherManagerRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0)
        {
            return "";
        }

        var labels = records.Select(entry => entry.ManagerId).Distinct()
            .Select(id => Known.FirstOrDefault(manager => manager.Id == id)?.Label ?? id)
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var services = records.Count(entry => entry.Kind == ServiceKind);
        var tasks = records.Count(entry => entry.Kind == TaskKind);
        var targets = records.Count(entry => entry.Kind is PackageKind or DeviceKind);
        List<string> parts = [];
        if (services > 0)
        {
            parts.Add(services == 1 ? "1 service" : $"{services} services");
        }

        if (tasks > 0)
        {
            parts.Add(tasks == 1 ? "1 scheduled task" : $"{tasks} scheduled tasks");
        }

        if (targets > 0)
        {
            parts.Add($"{targets} package or keyboard settings");
        }

        var who = labels.Length == 1 ? labels[0] : string.Join(", ", labels[..^1]) + " and " + labels[^1];
        return $"{who}: {string.Join(" and ", parts)} are turned off";
    }

    /// <summary>
    ///     Puts back what WSGM turned off: services get their start type again (and an automatic one is
    ///     started), tasks are enabled. A restored record is removed, so running this twice is safe.
    /// </summary>
    /// <returns>Zero when every record was restored, 3010 when a restored device needs a restart, otherwise 1.</returns>
    /// <param name="store">Persistence holding takeover records; only restored records are removed.</param>
    public static int RestoreAll(ConfigStore store)
    {
        try
        {
            var records = store.Read().RequireConfig().OtherManagersDisabled;
            if (records.Count == 0)
            {
                return 0;
            }

            var targets = new OtherManagerWindowsTargets();
            var restored = Restore(records, new AutostartSystem(), new ServiceSystem(), targets);
            HashSet<string> done = [.. restored.Select(Key)];
            store.Update(config =>
            {
                config.OtherManagersDisabled =
                    [.. config.OtherManagersDisabled.Where(entry => !done.Contains(Key(entry)))];
                return true;
            });
            return restored.Count != records.Count ? 1 : targets.RestartRequired ? SessionProtocolNames.RestartRequired : 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("Other managers: restore failed", ex);
            return 1;
        }
    }

    /// <summary>Records one change in the configuration, keeping the first previous state for an entry.</summary>
    /// <param name="store">Strict configuration persistence; failures propagate before the caller mutates Windows.</param>
    /// <param name="entry">Pre-change state; the first record for the same kind/name is retained.</param>
    public static void Record(ConfigStore store, OtherManagerRecord entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        store.Update(config =>
        {
            if (!config.OtherManagersDisabled.Any(other => Key(other) == Key(entry)))
            {
                config.OtherManagersDisabled.Add(entry);
            }

            return true;
        });
    }

    /// <summary>Restores the given records and returns those that were put back.</summary>
    /// <param name="records">Saved pre-change states to attempt once.</param>
    /// <param name="autostart">Task backend used to restore enabled state.</param>
    /// <param name="services">Service backend used to restore start type and best-effort automatic start.</param>
    /// <param name="targets">Package/device backend; live by default.</param>
    /// <returns>Records whose setting was restored or whose original target no longer exists; backend exceptions propagate.</returns>
    internal static IReadOnlyList<OtherManagerRecord> Restore(IReadOnlyList<OtherManagerRecord> records,
        IAutostartSystem autostart, IServiceSystem services, IOtherManagerTargets? targets = null)
    {
        List<OtherManagerRecord> restored = [];
        targets ??= new OtherManagerWindowsTargets();
        foreach (var entry in records)
        {
            var ok = entry.Kind switch
            {
                // A task the maker's uninstaller removed meanwhile has nothing left to restore either.
                TaskKind when autostart.ReadTaskEnabled(entry.Name) is null => true,
                TaskKind => autostart.SetTaskEnabled(entry.Name, true),
                // A service the maker's uninstaller removed meanwhile has nothing left to restore.
                ServiceKind when services.ReadStart(entry.Name, out _) is null => true,
                ServiceKind => RestoreService(services, entry),
                PackageKind or DeviceKind => targets.Restore(entry),
                _ => true
            };
            if (ok)
            {
                restored.Add(entry);
            }
            else
            {
                Log.Warn($"Other managers: could not restore {entry.Kind} {entry.Name}.");
            }
        }

        return restored;
    }

    private static bool RestoreService(IServiceSystem services, OtherManagerRecord entry)
    {
        if (!services.SetStart(entry.Name, entry.PreviousStart, entry.PreviousDelayed))
        {
            return false;
        }

        // An automatic service was running before; starting it is best effort, the start type is what matters.
        if (entry.PreviousStart == 2)
        {
            services.Start(entry.Name);
        }

        return true;
    }

    /// <summary>Extracts a task command's executable base name for known-manager matching.</summary>
    /// <param name="command">Command with an optionally quoted executable followed by arguments.</param>
    /// <returns>The executable name without path or extension; this is not a general command-line parser.</returns>
    internal static string ExecutableName(string command)
    {
        var text = command.Trim();
        var exe = text.StartsWith('"') && text.IndexOf('"', 1) is > 0 and var end
            ? text[1..end]
            : text.Split(' ', 2)[0];
        return Path.GetFileNameWithoutExtension(exe);
    }

    private static string Key(OtherManagerRecord entry)
    {
        return $"{entry.Kind}|{entry.Name}".ToLower(CultureInfo.InvariantCulture);
    }

    private static HashSet<string> RunningProcessNames()
    {
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    names.Add(process.ProcessName);
                }
                catch (InvalidOperationException)
                {
                    // Exited while listing.
                }
            }
        }

        return names;
    }

    // Asks each window to close and waits briefly. A tray app with no window, or one that ignores the
    // request, is left running and reported: setup never ends another program.
    private static IReadOnlyList<string> CloseWindows(IReadOnlyList<string> names)
    {
        List<string> still = [];
        foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    try
                    {
                        if (!process.CloseMainWindow() || !process.WaitForExit(TimeSpan.FromSeconds(8)))
                        {
                            still.Add(name);
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        // Exited already.
                    }
                }
            }
        }

        return [.. still.Distinct(StringComparer.OrdinalIgnoreCase)];
    }
}
