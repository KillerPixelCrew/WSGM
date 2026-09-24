using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace WSGM.DeviceLab.Wizard;

/// <summary>A known manager and what it does to the evidence.</summary>
/// <param name="Process">Process name without extension.</param>
/// <param name="Label">Product name shown to the tester.</param>
/// <param name="Closable">Whether the wizard may ask it to close; services are never touched.</param>
/// <param name="Why">What it does to a capture.</param>
internal sealed record KnownManager(string Process, string Label, bool Closable, string Why);

/// <summary>A known manager that is running now.</summary>
/// <param name="Manager">Which manager.</param>
/// <param name="ProcessId">Its process ID.</param>
internal sealed record RunningManager(KnownManager Manager, int ProcessId);

/// <summary>How a close request ended.</summary>
/// <param name="Label">Manager name.</param>
/// <param name="Exited">Whether it had exited when the grace period ended.</param>
/// <param name="Detail">Why not, when it did not.</param>
internal sealed record ManagerCloseResult(string Label, bool Exited, string? Detail);

/// <summary>Drivers that change what the lab can see.</summary>
/// <param name="HidHide">Whether HidHide.sys is present.</param>
/// <param name="ViGEmBus">Whether ViGEmBus.sys is present.</param>
internal sealed record ManagerDrivers(bool HidHide, bool ViGEmBus);

/// <summary>
///     Finds other handheld managers. While one runs, the controller is often hidden, replaced by a
///     virtual pad or switched into another mode, so what the lab records is not the device's own
///     behaviour.
/// </summary>
/// <remarks>
///     Ported from AllyXLab 0.3.2. Matching is on the exact process name: a substring test would label
///     ArmouryCrateControlInterface as Armoury Crate and make the answer depend on table order.
/// </remarks>
internal static class ManagerConflicts
{
    private static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(8);

    /// <summary>Every manager the lab knows. Services are listed so the tester learns why a device stays hidden.</summary>
    public static IReadOnlyList<KnownManager> Known { get; } =
    [
        new("HandheldCompanion", "Handheld Companion", true,
            "hides the physical controller and presents a virtual one"),
        new("ControllerService", "Handheld Companion controller service", false, "hides the physical controller"),
        new("ArmouryCrate", "Armoury Crate", true, "owns the ASUS controller and its modes"),
        new("ArmouryCrateControlInterface", "Armoury Crate control interface", true,
            "owns the ASUS controller and its modes"),
        new("ArmourySocketServer", "Armoury Crate socket server", true, "relays ASUS controller commands"),
        new("ArmouryCrateUserSessionHelper", "Armoury Crate session helper", true, "relays ASUS OEM button events"),
        new("AsusAppService", "ASUS app service", false, "carries ASUS OEM button events and power commands"),
        new("AsusOptimization", "ASUS Optimization", false, "handles ASUS hotkeys"),
        new("AsusSystemDiagnosis", "ASUS System Diagnosis", false, "runs ASUS system services"),
        new("GHelper", "G-Helper", true, "writes ASUS power and controller settings"),
        new("MSI Center", "MSI Center", true, "owns MSI device services"),
        new("MSI.CentralServer", "MSI Center service", false, "owns MSI device services"),
        new("Winhanced", "Winhanced", true, "changes power and input behaviour"),
        new("HidHideClient", "HidHide Configuration Client", true,
            "edits the device hiding list while this tool reads it"),
        new("DS4Windows", "DS4Windows", true, "hides controllers and presents virtual ones"),
        new("x360ce", "x360ce", true, "presents a virtual controller"),
        new("WSGM", "WSGM", true, "owns the device integration")
    ];

    /// <summary>Lists the known managers running now.</summary>
    /// <returns>One entry per matching process, ordered by name.</returns>
    public static IReadOnlyList<RunningManager> Running()
    {
        List<RunningManager> found = [];
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                string name;
                try
                {
                    name = process.ProcessName;
                }
                catch (InvalidOperationException)
                {
                    continue;
                }

                if (Match(name) is { } known)
                {
                    found.Add(new RunningManager(known, process.Id));
                }
            }
        }

        return
        [
            .. found.DistinctBy(manager => manager.ProcessId)
                .OrderBy(manager => manager.Manager.Label, StringComparer.Ordinal)
        ];
    }

    /// <summary>Finds the manager with exactly this process name.</summary>
    /// <param name="processName">Process name without extension.</param>
    /// <returns>The manager, or null.</returns>
    public static KnownManager? Match(string processName)
    {
        return Known.FirstOrDefault(known => known.Process.Equals(processName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Asks one manager to close its main window.</summary>
    /// <param name="running">The manager the tester chose to close.</param>
    /// <returns>How the request ended.</returns>
    /// <remarks>
    ///     A close request only, never a kill and never a service stop: the lab does not take other software
    ///     down by force, and a manager that ignores the request is reported to the tester.
    /// </remarks>
    public static ManagerCloseResult Close(RunningManager running)
    {
        ArgumentNullException.ThrowIfNull(running);
        var manager = running.Manager;
        if (!manager.Closable)
        {
            return new ManagerCloseResult(manager.Label, false, "Services are never stopped by this tool.");
        }

        try
        {
            using var process = Process.GetProcessById(running.ProcessId);
            if (!process.ProcessName.Equals(manager.Process, StringComparison.OrdinalIgnoreCase))
            {
                return new ManagerCloseResult(manager.Label, false, "The process ID belongs to something else now.");
            }

            process.CloseMainWindow();
            var exited = process.WaitForExit(CloseGrace);
            return new ManagerCloseResult(manager.Label, exited,
                exited ? null : "It did not close. Close it yourself.");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or SystemException)
        {
            return new ManagerCloseResult(manager.Label, false, ex.Message);
        }
    }

    /// <summary>Reports the drivers that change what the lab can see.</summary>
    /// <returns>Driver presence. A present driver is not proof that it is active.</returns>
    public static ManagerDrivers Drivers()
    {
        var drivers = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers");
        return new ManagerDrivers(
            File.Exists(Path.Combine(drivers, "HidHide.sys")),
            File.Exists(Path.Combine(drivers, "ViGEmBus.sys")));
    }
}
