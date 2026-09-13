using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;

namespace Wsgm.UwpSpike;

/// Stays alive for the whole game session so Steam keeps the shortcut in a running
/// state, while tracking the processes the activation actually produced.
internal sealed class Supervisor(Options options, SpikeLog log, GameContainment? containment, ForegroundProxy? proxy, Injection? injection)
{
    private readonly Dictionary<int, TimeSpan> injectAt = [];
    private readonly Dictionary<int, TimeSpan> envAt = [];
    private string lastForeground = string.Empty;
    private readonly Dictionary<int, ProcessReport> tracked = [];
    private readonly Dictionary<int, HashSet<string>> reportedModules = [];
    private readonly Dictionary<int, string> reportedWindows = [];
    private readonly Stopwatch clock = Stopwatch.StartNew();

    /// <summary>Runs until the tracked game has exited, or until nothing ever appeared.</summary>
    /// <returns>True when a game was seen and then exited normally.</returns>
    internal bool Run(int seedPid, CancellationToken cancellationToken)
    {
        var sawGame = false;
        TimeSpan? goneSince = null;
        var lastHeartbeat = TimeSpan.Zero;

        while (!cancellationToken.IsCancellationRequested)
        {
            var snapshot = ProcessProbe.Snapshot();
            var byPid = snapshot.ToDictionary(entry => entry.Pid);
            var current = Interesting(snapshot, seedPid);

            foreach (var pid in current.Keys.Where(pid => !tracked.ContainsKey(pid)).ToList())
            {
                var entry = current[pid];
                var parentName = byPid.TryGetValue(entry.ParentPid, out var parent) ? parent.Name : "<gone>";
                var report = ProcessProbe.Describe(entry, parentName, options.ProbeRights);
                tracked[pid] = report;
                ProcessProbe.WriteReport(log, $"+{clock.Elapsed.TotalSeconds:F1}s appeared", report);
                NoteModules(report);
                NoteWindows(report);
                if (IsGameBinary(report))
                {
                    sawGame = true;
                    goneSince = null;
                    containment?.Contain(report.Pid, report.Name);
                    proxy?.SetTarget(report.Pid);

                    // Scheduled, not immediate: the renderer reads these at load so the
                    // environment must land before injection, but editing the block while
                    // the process was still starting killed the game about a second in.
                    if (options.PassSteamEnvironment && options.SteamEnvironment.Count > 0)
                    {
                        envAt[report.Pid] = clock.Elapsed + options.EnvironmentDelay;
                    }

                    if (injection is not null && options.Inject.Count > 0)
                    {
                        injectAt[report.Pid] = clock.Elapsed + options.InjectDelay;
                    }
                }
            }

            foreach (var pid in tracked.Keys.Where(pid => !current.ContainsKey(pid)).ToList())
            {
                log.Info($"+{clock.Elapsed.TotalSeconds:F1}s exited: pid {pid} \"{tracked[pid].Name}\"");
                tracked.Remove(pid);
                reportedModules.Remove(pid);
                reportedWindows.Remove(pid);
            }

            // Injection lands well after a process starts, and an overlay that hooks on the
            // first present call can be seconds in. Rescan every survivor on every poll
            // rather than trusting the snapshot taken when it appeared.
            foreach (var pid in tracked.Keys.ToList())
            {
                if (!current.TryGetValue(pid, out var entry))
                {
                    continue;
                }

                var parentName = byPid.TryGetValue(entry.ParentPid, out var parent) ? parent.Name : "<gone>";
                var refreshed = ProcessProbe.Describe(entry, parentName, probeRights: false);
                tracked[pid] = refreshed;
                NoteModules(refreshed);
                NoteWindows(refreshed);

                if (envAt.TryGetValue(pid, out var envDue) && clock.Elapsed >= envDue)
                {
                    envAt.Remove(pid);
                    EnvironmentPatch.Apply(pid, options.SteamEnvironment, log);
                }

                if (injectAt.TryGetValue(pid, out var due) && clock.Elapsed >= due)
                {
                    injectAt.Remove(pid);
                    injection?.InjectAll(options.Inject, refreshed);

                    foreach (var call in options.Call)
                    {
                        var separator = call.LastIndexOf('!');
                        if (separator <= 0 || separator == call.Length - 1)
                        {
                            log.Error($"remote call: \"{call}\" is not in dll!Export form.");
                            continue;
                        }

                        injection?.CallExport(call[..separator], call[(separator + 1)..], refreshed);
                    }
                }
            }

            // Only the title's own binaries hold the session open. A packaged app's
            // RuntimeBroker outlived Moonlighter by half a minute in a recorded run, which
            // would have kept Steam showing the shortcut as running long after the game
            // was gone.
            if (tracked.Values.Count(IsGameBinary) == 0)
            {
                goneSince ??= clock.Elapsed;
                if (sawGame && clock.Elapsed - goneSince.Value >= options.ExitGrace)
                {
                    log.Info($"Game gone for {options.ExitGrace.TotalSeconds:F0}s; the wrapper is releasing Steam's running state.");
                    return true;
                }

                if (!sawGame && clock.Elapsed >= options.Settle)
                {
                    log.Warn($"No game process matched within {options.Settle.TotalSeconds:F0}s. Nothing to supervise.");
                    return false;
                }
            }

            NoteForeground(byPid);

            if (clock.Elapsed - lastHeartbeat >= options.Heartbeat)
            {
                lastHeartbeat = clock.Elapsed;
                var names = tracked.Count == 0
                    ? "waiting for the game to appear"
                    : string.Join(", ", tracked.Values.Select(report => $"{report.Pid} {report.Name}"));
                log.Info($"+{clock.Elapsed.TotalSeconds:F0}s alive; tracking: {names}");
            }

            cancellationToken.WaitHandle.WaitOne(options.Poll);
        }

        log.Warn("Cancelled; leaving the game running and exiting the wrapper.");
        return false;
    }

    /// Only the title's own binaries get contained. A packaged app's helpers under
    /// System32 — RuntimeBroker above all — are shared Windows infrastructure, and killing
    /// one when the wrapper stops would reach well outside this game session.
    private static bool IsGameBinary(ProcessReport report)
    {
        if (report.ImagePath.Length == 0 || report.ImagePath.StartsWith('<'))
        {
            return false;
        }

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return windows.Length == 0
            || !report.ImagePath.StartsWith(windows, StringComparison.OrdinalIgnoreCase);
    }

    /// Reports each foreign overlay/instrumentation module the first time it shows up in a
    /// tracked process. Steam's renderer is called out by name because it is the answer to
    /// the issue's main question; RTSS and the rest are the control, since a third-party
    /// overlay getting in where Steam's does not says the obstacle is Steam's, not Windows'.
    /// Whatever currently owns the foreground, and which process it belongs to.
    ///
    /// Steam picks the window it draws the overlay over, and the one it routes Steam Input
    /// to, from the app it is tracking. The tracked app here is the wrapper, and the game
    /// showed no enumerable window at all for a whole session while input went to Big
    /// Picture behind it. This says who really owns the screen when the game is up.
    private void NoteForeground(Dictionary<int, ProcessEntry> byPid)
    {
        var handle = Native.GetForegroundWindow();
        if (handle == IntPtr.Zero)
        {
            return;
        }

        Native.GetWindowThreadProcessId(handle, out var owner);
        var className = new StringBuilder(256);
        Native.GetClassNameW(handle, className, className.Capacity);
        var title = new StringBuilder(512);
        Native.GetWindowTextW(handle, title, title.Capacity);
        var name = byPid.TryGetValue((int)owner, out var entry) ? entry.Name : "<unknown>";

        var description = $"0x{handle.ToInt64():X} [{className}] pid {owner} \"{name}\" title \"{title}\"";
        if (description == lastForeground)
        {
            return;
        }

        lastForeground = description;
        log.Info($"foreground: after {clock.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)}s -> {description}");
    }

    /// A UWP title's window does not exist when the process does, and for the frame case it
    /// never belongs to the process at all, so the window set is reported whenever it
    /// changes rather than once at discovery.
    private void NoteWindows(ProcessReport report)
    {
        var description = report.Windows.Count == 0
            ? "none"
            : string.Join(", ", report.Windows
                .Select(window => $"0x{window.Handle.ToInt64():X} [{window.ClassName}] visible={window.Visible} \"{window.Title}\""));

        if (reportedWindows.TryGetValue(report.Pid, out var previous) && previous == description)
        {
            return;
        }

        reportedWindows[report.Pid] = description;
        var elapsed = clock.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture);
        log.Info($"windows: pid {report.Pid} \"{report.Name}\" after {elapsed}s -> {description}");
    }

    private void NoteModules(ProcessReport report)
    {
        if (!reportedModules.TryGetValue(report.Pid, out var seen))
        {
            seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            reportedModules[report.Pid] = seen;
        }

        foreach (var path in report.InterestingModules)
        {
            if (!seen.Add(path))
            {
                continue;
            }

            var elapsed = clock.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture);
            var label = path.Contains("gameoverlayrenderer", StringComparison.OrdinalIgnoreCase)
                ? "STEAM OVERLAY"
                : "INJECTED";
            log.Info($"{label}: pid {report.Pid} \"{report.Name}\" loaded {path} after {elapsed}s");
        }
    }

    /// The processes this run considers "the game": anything carrying the target package
    /// identity, anything below the seed process, and anything matching --match.
    private Dictionary<int, ProcessEntry> Interesting(IReadOnlyList<ProcessEntry> snapshot, int seedPid)
    {
        var result = new Dictionary<int, ProcessEntry>();
        var family = options.PackageFamily;
        var self = Environment.ProcessId;

        // Seeded from the activation result and from everything already tracked, so a
        // title that replaces its first process keeps its lineage recognised after the
        // original seed is gone.
        var roots = new List<int>(tracked.Keys);
        if (seedPid > 0)
        {
            roots.Add(seedPid);
        }

        var descendants = new HashSet<int>();
        foreach (var root in roots)
        {
            descendants.UnionWith(ProcessProbe.Descendants(snapshot, root));
        }

        foreach (var entry in snapshot)
        {
            if (entry.Pid == self || entry.Pid <= 4)
            {
                continue;
            }

            var matched = descendants.Contains(entry.Pid)
                || options.Match.Any(hint => entry.Name.Contains(hint, StringComparison.OrdinalIgnoreCase));

            if (!matched && family is not null)
            {
                matched = string.Equals(ProcessProbe.PackageFamilyOf(entry.Pid), family, StringComparison.OrdinalIgnoreCase);
            }

            if (matched)
            {
                result[entry.Pid] = entry;
            }
        }

        // A PowerShell/Explorer middleman is part of the launch chain, not the game: it
        // must not keep the wrapper alive, and it must not end the session when it exits.
        if (options.Mode == LaunchMode.PowerShell)
        {
            foreach (var pid in result.Where(pair => pair.Value.Name.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase)
                || pair.Value.Name.Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.Key).ToList())
            {
                result.Remove(pid);
            }
        }

        return result;
    }
}
