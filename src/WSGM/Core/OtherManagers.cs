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
            parts.Add(Tasks.Count == 1 ? "starts at sign-in" : $"{Tasks.Count} scheduled tasks");
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
    private const string ServiceKind = "service";
    private const string TaskKind = "task";

    /// <summary>The managers WSGM knows, from Handheld Companion's own OEM lists plus Handheld Companion itself.</summary>
    public static IReadOnlyList<OtherManager> Known { get; } =
    [
        new("handheld-companion", "Handheld Companion", ["HandheldCompanion"], [], ["HandheldCompanion"]),
        new("msi-center-m", "MSI Center M",
            ["MSI_Center_M_Server", "MSI Center M", "MCMOSDInfo", "MSI Center OSD Info"],
            ["MSI Foundation Service"], ["MSI_Center_M_Server", "MSI_Center_M_Updater"]),
        new("armoury-crate", "Armoury Crate", ["ArmouryCrate", "ArmourySocketServer", "ArmouryCrateUserSessionHelper"],
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
            HashSet<string> tasks = new(StringComparer.OrdinalIgnoreCase);
            foreach (var task in manager.Tasks.Select(name => @"\" + name).Where(autostart.IsTaskEnabled))
            {
                tasks.Add(task);
            }

            // A logon task that runs one of the manager's programs is its autostart, whatever it is called.
            foreach (var (path, command) in logonTasks)
            {
                if (manager.Processes.Contains(ExecutableName(command), StringComparer.OrdinalIgnoreCase)
                    && autostart.IsTaskEnabled(path))
                {
                    tasks.Add(path);
                }
            }

            DetectedManager detected = new(manager, activeServices, [.. tasks.Order(StringComparer.OrdinalIgnoreCase)],
                [.. manager.Processes.Where(process => running.Contains(process, StringComparer.OrdinalIgnoreCase))]);
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
