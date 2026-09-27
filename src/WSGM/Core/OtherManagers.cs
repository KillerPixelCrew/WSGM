using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace WSGM.Core;

/// <summary>A handheld manager WSGM's Full mode replaces, and how it starts itself.</summary>
/// <param name="Id">Stable id, for the log and the records.</param>
/// <param name="Label">Product name shown in setup.</param>
/// <param name="Processes">Its process names, without extension. A logon task running one of them is its autostart.</param>
/// <param name="Services">Its Windows services.</param>
/// <param name="Tasks">Its scheduled tasks, by name in the root folder.</param>
public sealed record OtherManager(
    string Id,
    string Label,
    IReadOnlyList<string> Processes,
    IReadOnlyList<string> Services,
    IReadOnlyList<string> Tasks);

/// <summary>A manager found on this machine and what of it would start.</summary>
/// <param name="Manager">Which manager.</param>
/// <param name="Services">Its services that are not disabled.</param>
/// <param name="Tasks">Its enabled tasks, by path.</param>
/// <param name="Running">Its processes running now.</param>
public sealed record DetectedManager(
    OtherManager Manager,
    IReadOnlyList<string> Services,
    IReadOnlyList<string> Tasks,
    IReadOnlyList<string> Running)
{
    /// <summary>Whether anything of it would start or runs now.</summary>
    public bool Active => Services.Count > 0 || Tasks.Count > 0 || Running.Count > 0;

    /// <summary>Whether every one of its tasks has a sign-in trigger.</summary>
    public bool StartsAtSignIn { get; init; }

    /// <summary>One line for setup, such as <c>MSI Center M: 1 service, 2 scheduled tasks</c>.</summary>
    public string Describe()
    {
        List<string> parts = [];
        if (Services.Count > 0)
        {
            parts.Add(Services.Count == 1 ? "1 service" : $"{Services.Count} services");
        }

        if (Tasks.Count > 0)
        {
            parts.Add(Tasks.Count == 1
                ? StartsAtSignIn ? "starts at sign-in" : "1 scheduled task"
                : $"{Tasks.Count} scheduled tasks");
        }

        if (Running.Count > 0)
        {
            parts.Add("running now");
        }

        return parts.Count == 0 ? Manager.Label : $"{Manager.Label}: {string.Join(", ", parts)}";
    }
}

/// <summary>One service or task WSGM turned off, and how to put it back.</summary>
/// <remarks>
///     An install-lifecycle recovery snapshot like <see cref="SteamAutostartRecord" />: recorded before the
///     change, read by the uninstaller to undo exactly what WSGM changed, and removed once restored.
/// </remarks>
public sealed class OtherManagerRecord
{
    /// <summary>The manager it belongs to.</summary>
    public string ManagerId { get; set; } = "";

    /// <summary><c>service</c> or <c>task</c>.</summary>
    public string Kind { get; set; } = "";

    /// <summary>Service name or task path.</summary>
    public string Name { get; set; } = "";

    /// <summary>A service's start type before WSGM changed it (2 automatic, 3 manual).</summary>
    public int PreviousStart { get; set; }

    /// <summary>Whether a service started delayed before WSGM changed it.</summary>
    public bool PreviousDelayed { get; set; }
}

/// <summary>The Windows services the manager takeover reads and changes.</summary>
public interface IServiceSystem
{
    /// <summary>Reads a service's start type (2 automatic, 3 manual, 4 disabled), or null when it does not exist.</summary>
    int? ReadStart(string service, out bool delayed);

    /// <summary>Sets a service's start type.</summary>
    bool SetStart(string service, int start, bool delayed);

    /// <summary>Asks a service to stop.</summary>
    bool Stop(string service);

    /// <summary>Asks a service to start.</summary>
    bool Start(string service);
}

/// <summary>The live services: start types from the registry, changes through <c>sc.exe</c> so the service manager knows.</summary>
public sealed class ServiceSystem : IServiceSystem
{
    /// <inheritdoc />
    public int? ReadStart(string service, out bool delayed)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{service}");
        delayed = key?.GetValue("DelayedAutostart") is int delay && delay != 0;
        return key?.GetValue("Start") is int start ? start : null;
    }

    /// <inheritdoc />
    public bool SetStart(string service, int start, bool delayed)
    {
        var mode = start switch
        {
            2 when delayed => "delayed-auto",
            2 => "auto",
            3 => "demand",
            4 => "disabled",
            _ => null
        };
        return mode is not null &&
               ConsoleTool.Run(ConsoleTool.System32("sc.exe"), $"config \"{service}\" start= {mode}");
    }

    /// <inheritdoc />
    public bool Stop(string service)
    {
        return ConsoleTool.Run(ConsoleTool.System32("sc.exe"), $"stop \"{service}\"");
    }

    /// <inheritdoc />
    public bool Start(string service)
    {
        return ConsoleTool.Run(ConsoleTool.System32("sc.exe"), $"start \"{service}\"");
    }
}

/// <summary>What turning the managers off achieved.</summary>
/// <param name="Disabled">Services and tasks now off.</param>
/// <param name="Failed">Services and tasks that could not be changed.</param>
/// <param name="StillRunning">Manager windows that did not close when asked; setup never ends them.</param>
public sealed record OtherManagersResult(
    IReadOnlyList<string> Disabled,
    IReadOnlyList<string> Failed,
    IReadOnlyList<string> StillRunning);

/// <summary>
///     Finds Handheld Companion and the handheld makers' own apps, and turns off how they start, so WSGM's
///     Full mode is the one manager of the device. Modelled on Handheld Companion's OEM app management
///     (its per-device service, task and process lists): services are set to disabled and stopped, tasks
///     are disabled, and a running window is asked to close but never ended. Everything is recorded first
///     and put back by the uninstaller.
/// </summary>
public static class OtherManagers
{
    /// <summary>The one-shot that turns the managers off from an elevated instance.</summary>
    public const string DisableArgument = "--disable-other-managers";

    private const string ServiceKind = "service";
    private const string TaskKind = "task";

    /// <summary>The managers WSGM knows, from Handheld Companion's own OEM lists plus Handheld Companion itself.</summary>
    public static IReadOnlyList<OtherManager> Known { get; } =
    [
        new("handheld-companion", "Handheld Companion", ["HandheldCompanion"], [], ["HandheldCompanion"]),
        new("msi-center-m", "MSI Center M",
            ["MSI_Center_M_Server", "MSI Center M", "MCMOSDInfo", "MSI Center OSD Info"],
            ["MSI Foundation Service"], ["MSI_Center_M_Server", "MSI_Center_M_Updater"]),
        // ArmouryCrateKeyControl is the helper the ArmouryCrateControlInterface service starts in the user
        // session; without Armoury Crate installed it answers the Armoury Crate button with a dialog that
        // asks to install it (seen on an Ally after Handheld Companion's uninstall put the service back).
        new("armoury-crate", "Armoury Crate",
            ["ArmouryCrate", "ArmourySocketServer", "ArmouryCrateUserSessionHelper", "ArmouryCrateKeyControl"],
            ["ArmouryCrateSEService", "AsusAppService", "ArmouryCrateControlInterface"], []),
        new("legion-space", "Legion Space", ["LegionGoQuickSettings", "LegionSpace", "LSDaemon"], ["DAService"], []),
        new("zotac-launcher", "Zotac Gaming Zone", ["ZotacHandheldQuickSetting"],
            ["ZotacHandheldDatabaseService", "ZotacHandheldService"], [])
    ];

    /// <summary>Finds the known managers that would start or run on this machine.</summary>
    /// <param name="autostart">The startup surfaces; the live ones by default.</param>
    /// <param name="services">The services; the live ones by default.</param>
    /// <param name="running">Running process names; the live list by default.</param>
    /// <returns>The active managers, in catalog order.</returns>
    public static IReadOnlyList<DetectedManager> Detect(IAutostartSystem? autostart = null,
        IServiceSystem? services = null, IReadOnlyCollection<string>? running = null)
    {
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
            List<string> activeServices =
                [.. manager.Services.Where(service => services.ReadStart(service, out _) is not (null or 4))];
            // A catalog task counts only when it exists: IsTaskEnabled reads a missing task as enabled.
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
                StartsAtSignIn = tasks.Count > 0 && tasks.All(logonTasks.ContainsKey)
            };
            if (detected.Active)
            {
                found.Add(detected);
            }
        }

        return found;
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
    /// <returns>What changed.</returns>
    public static OtherManagersResult Disable(IReadOnlyList<DetectedManager> detected,
        Action<OtherManagerRecord> record,
        IAutostartSystem? autostart = null, IServiceSystem? services = null,
        Func<IReadOnlyList<string>, IReadOnlyList<string>>? closeWindows = null)
    {
        ArgumentNullException.ThrowIfNull(detected);
        ArgumentNullException.ThrowIfNull(record);
        autostart ??= new AutostartSystem();
        services ??= new ServiceSystem();
        closeWindows ??= CloseWindows;
        List<string> disabled = [];
        List<string> failed = [];
        foreach (var manager in detected)
        {
            foreach (var task in manager.Tasks)
            {
                record(new OtherManagerRecord { ManagerId = manager.Manager.Id, Kind = TaskKind, Name = task });
                (autostart.SetTaskEnabled(task, false) ? disabled : failed).Add($"{manager.Manager.Label} task {task}");
            }

            foreach (var service in manager.Services)
            {
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
                    services.Stop(service);
                    disabled.Add($"{manager.Manager.Label} service {service}");
                }
                else
                {
                    failed.Add($"{manager.Manager.Label} service {service}");
                }
            }
        }

        var stillRunning = closeWindows([.. detected.SelectMany(manager => manager.Running)]);
        Log.Info($"Other managers: turned off {disabled.Count}, failed {failed.Count}, still running "
                 + $"[{string.Join(", ", stillRunning)}].");
        return new OtherManagersResult(disabled, failed, stillRunning);
    }

    /// <summary>
    ///     Turns the detected managers off from this process when it is elevated, or through the elevated
    ///     one-shot when a prompt is acceptable. Services and tasks need an administrator; nothing partial
    ///     is attempted without one, because a closed helper the service restarts would only hide the state.
    /// </summary>
    /// <param name="detected">What <see cref="Detect" /> found.</param>
    /// <param name="allowElevation">
    ///     Whether an elevation prompt is acceptable here. False at a shell start, where a prompt over
    ///     the booting desktop would be hostile.
    /// </param>
    /// <returns>What this attempt achieved; everything failed when the process could not change it.</returns>
    public static OtherManagersResult Apply(IReadOnlyList<DetectedManager> detected, bool allowElevation)
    {
        ArgumentNullException.ThrowIfNull(detected);
        if (detected.Count == 0)
        {
            return new OtherManagersResult([], [], []);
        }

        if (ElevationCheck.IsCurrentProcessElevated() is true)
        {
            return Disable(detected, Record);
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
        SelfElevation.RunElevatedAction(DisableArgument, "Other managers takeover");
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
        }

        return new OtherManagersResult(disabled, failed, [.. remaining.SelectMany(manager => manager.Running)]);
    }

    /// <summary>
    ///     Re-checks at a shell start once the takeover has been accepted, so a manager that came back is
    ///     turned off again: Handheld Companion's uninstaller re-enables the maker's services, and a driver
    ///     update can re-register them. Never prompts; an unelevated WSGM only logs what it found.
    /// </summary>
    public static void ReapplyAtStart()
    {
        try
        {
            if (!ConfigStore.Load().OtherManagersTakeoverAccepted)
            {
                return;
            }

            var detected = Detect();
            if (detected.Count == 0)
            {
                return;
            }

            Log.Info("Other managers: " + string.Join("; ", detected.Select(manager => manager.Describe()))
                                        + " are back; turning them off again.");
            var result = Apply(detected, false);
            if (result.Failed.Count > 0 || result.StillRunning.Count > 0)
            {
                Log.Warn($"Other managers: failed [{string.Join(", ", result.Failed)}], still running "
                         + $"[{string.Join(", ", result.StillRunning)}].");
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"Other managers re-check failed: {ex.Message}");
        }
    }

    /// <summary>The elevated one-shot: detects and turns off what only an elevated process can.</summary>
    /// <returns>Zero when every service and task could be changed.</returns>
    public static int RunElevatedDisable()
    {
        try
        {
            var detected = Detect();
            return detected.Count == 0 || Disable(detected, Record).Failed.Count == 0 ? 0 : 1;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("Other managers takeover failed", ex);
            return 1;
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
        List<string> parts = [];
        if (services > 0)
        {
            parts.Add(services == 1 ? "1 service" : $"{services} services");
        }

        if (tasks > 0)
        {
            parts.Add(tasks == 1 ? "1 scheduled task" : $"{tasks} scheduled tasks");
        }

        var who = labels.Length == 1 ? labels[0] : string.Join(", ", labels[..^1]) + " and " + labels[^1];
        return $"{who}: {string.Join(" and ", parts)} are turned off";
    }

    /// <summary>
    ///     Puts back what WSGM turned off: services get their start type again (and an automatic one is
    ///     started), tasks are enabled. A restored record is removed, so running this twice is safe.
    /// </summary>
    /// <returns>Zero when every record was restored.</returns>
    public static int RestoreAll()
    {
        try
        {
            var records = ConfigStore.Load().OtherManagersDisabled;
            if (records.Count == 0)
            {
                return 0;
            }

            var restored = Restore(records, new AutostartSystem(), new ServiceSystem());
            HashSet<string> done = [.. restored.Select(Key)];
            ConfigStore.Mutate(config =>
                config.OtherManagersDisabled =
                    [.. config.OtherManagersDisabled.Where(entry => !done.Contains(Key(entry)))]);
            return restored.Count == records.Count ? 0 : 1;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("Other managers: restore failed", ex);
            return 1;
        }
    }

    /// <summary>Records one change in the configuration, keeping the first previous state for an entry.</summary>
    public static void Record(OtherManagerRecord entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ConfigStore.Mutate(config =>
        {
            if (!config.OtherManagersDisabled.Any(other => Key(other) == Key(entry)))
            {
                config.OtherManagersDisabled.Add(entry);
            }
        });
    }

    /// <summary>Restores the given records and returns those that were put back.</summary>
    internal static IReadOnlyList<OtherManagerRecord> Restore(IReadOnlyList<OtherManagerRecord> records,
        IAutostartSystem autostart, IServiceSystem services)
    {
        List<OtherManagerRecord> restored = [];
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
