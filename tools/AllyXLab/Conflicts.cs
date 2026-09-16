using System.Diagnostics;

namespace WSGM.AllyXLab;

/// <summary>A manager that is running and would change what this tool can see.</summary>
/// <param name="Label">The product name shown to the tester.</param>
/// <param name="Process">The process name.</param>
/// <param name="ProcessId">The process id.</param>
/// <param name="Closable">Whether the wizard may ask it to close; services are never touched.</param>
/// <param name="Why">What it does to the evidence.</param>
internal sealed record RunningManager(string Label, string Process, int ProcessId, bool Closable, string Why);

/// <summary>
/// Finds the other handheld managers. While one of them runs, the controller is often hidden from
/// this tool, replaced by a virtual pad, or driven into another mode, so its reports are not the
/// device's own behavior.
/// </summary>
internal static class Conflicts
{
    // Process name, product, closable, and what it does to a capture. Services are listed so the
    // tester learns why a device stays hidden, but this tool never stops a service.
    private static readonly (string Process, string Label, bool Closable, string Why)[] Known =
    [
        ("HandheldCompanion", "Handheld Companion", true, "hides the physical controller and presents a virtual one"),
        ("ControllerService", "Handheld Companion controller service", false, "hides the physical controller"),
        ("ArmouryCrate", "Armoury Crate", true, "owns the ASUS controller and its modes"),
        ("ArmouryCrateControlInterface", "Armoury Crate control interface", true, "owns the ASUS controller and its modes"),
        ("ArmourySocketServer", "Armoury Crate socket server", true, "relays ASUS controller commands"),
        ("ArmouryCrateUserSessionHelper", "Armoury Crate session helper", true, "relays ASUS OEM button events"),
        ("AsusAppService", "ASUS app service", false, "carries ASUS OEM button events and power commands"),
        ("AsusOptimization", "ASUS Optimization", false, "handles ASUS hotkeys"),
        ("AsusSystemDiagnosis", "ASUS System Diagnosis", false, "runs ASUS system services"),
        ("GHelper", "G-Helper", true, "writes ASUS power and controller settings"),
        ("MSI Center", "MSI Center", true, "owns MSI device services"),
        ("MSI.CentralServer", "MSI Center service", false, "owns MSI device services"),
        ("Winhanced", "Winhanced", true, "changes power and input behavior"),
        ("HidHideClient", "HidHide Configuration Client", true, "edits the device hiding list while this tool reads it"),
        ("DS4Windows", "DS4Windows", true, "hides controllers and presents virtual ones"),
        ("x360ce", "x360ce", true, "presents a virtual controller"),
        ("WSGM", "WSGM", true, "owns the device integration"),
    ];

    /// <summary>Lists the managers running right now.</summary>
    /// <returns>One entry per matching process.</returns>
    internal static IReadOnlyList<RunningManager> Running()
    {
        List<RunningManager> found = [];
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                string name;
                try { name = process.ProcessName; }
                catch (InvalidOperationException) { continue; }
                if (Match(name) is { } known)
                {
                    found.Add(new(known.Label, name, process.Id, known.Closable, known.Why));
                }
            }
        }

        return [.. found.DistinctBy(manager => manager.ProcessId).OrderBy(manager => manager.Label, StringComparer.Ordinal)];
    }

    /// <summary>Finds the known manager with exactly this process name.</summary>
    /// <param name="processName">A process name without its extension.</param>
    /// <returns>The table entry, or null. A substring test would label ArmouryCrateControlInterface
    /// as Armoury Crate and make the result depend on table order.</returns>
    internal static (string Process, string Label, bool Closable, string Why)? Match(string processName) =>
        Array.FindIndex(Known, known => known.Process.Equals(processName, StringComparison.OrdinalIgnoreCase)) is var index and >= 0
            ? Known[index]
            : null;

    /// <summary>Asks one manager to close itself.</summary>
    /// <param name="manager">The manager the tester chose to close.</param>
    /// <param name="log">The session log.</param>
    /// <returns>Whether it had exited when the grace period ended.</returns>
    /// <remarks>
    /// A close request only, never a kill and never a service stop: this tool does not take other
    /// software down by force, and a manager that ignores the request is reported to the tester.
    /// </remarks>
    internal static bool Close(RunningManager manager, SessionLog log)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(log);
        if (!manager.Closable)
        {
            log.Add("manager-close-refused", new { manager.Label, manager.Process, Reason = "Services are never stopped by this tool." });
            return false;
        }

        try
        {
            using Process process = Process.GetProcessById(manager.ProcessId);
            if (!process.ProcessName.Equals(manager.Process, StringComparison.OrdinalIgnoreCase))
            {
                log.Add("manager-close-refused", new { manager.Label, Reason = "The process id belongs to something else now." });
                return false;
            }

            log.Add("manager-close-request", new { manager.Label, manager.Process, manager.ProcessId });
            process.CloseMainWindow();
            bool exited = process.WaitForExit(8000);
            log.Add("manager-close-result", new { manager.Label, Exited = exited });
            return exited;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or SystemException)
        {
            log.Add("manager-close-result", new { manager.Label, Exited = false, e.Message });
            return false;
        }
    }

    /// <summary>Reports the drivers that change what a capture can see.</summary>
    /// <returns>Driver name and whether its file is present.</returns>
    internal static object Drivers()
    {
        string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        return new
        {
            HidHide = File.Exists(Path.Combine(system, "drivers", "HidHide.sys")),
            ViGEmBus = File.Exists(Path.Combine(system, "drivers", "ViGEmBus.sys")),
            Note = "A present driver is not proof that it is active; HidHide's own state is read separately.",
        };
    }
}
