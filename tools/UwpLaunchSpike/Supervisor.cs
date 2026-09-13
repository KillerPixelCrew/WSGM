using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace Wsgm.UwpSpike;

/// Stays alive for the whole game session so Steam keeps the shortcut in a running
/// state, while tracking the processes the activation actually produced.
internal sealed class Supervisor(Options options, SpikeLog log, GameContainment? containment)
{
    private readonly Dictionary<int, ProcessReport> tracked = [];
    private readonly HashSet<int> overlayReported = [];
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
                sawGame = true;
                goneSince = null;
                ProcessProbe.WriteReport(log, $"+{clock.Elapsed.TotalSeconds:F1}s appeared", report);
                NoteOverlay(report);
                if (IsGameBinary(report))
                {
                    containment?.Contain(report.Pid, report.Name);
                }
            }

            foreach (var pid in tracked.Keys.Where(pid => !current.ContainsKey(pid)).ToList())
            {
                log.Info($"+{clock.Elapsed.TotalSeconds:F1}s exited: pid {pid} \"{tracked[pid].Name}\"");
                tracked.Remove(pid);
                overlayReported.Remove(pid);
            }

            // Overlay injection usually lands well after the process starts, so rescan
            // the survivors instead of trusting the first snapshot.
            foreach (var pid in tracked.Keys.Where(pid => !overlayReported.Contains(pid)).ToList())
            {
                if (!current.TryGetValue(pid, out var entry))
                {
                    continue;
                }

                var parentName = byPid.TryGetValue(entry.ParentPid, out var parent) ? parent.Name : "<gone>";
                var refreshed = ProcessProbe.Describe(entry, parentName, probeRights: false);
                tracked[pid] = refreshed;
                NoteOverlay(refreshed);
            }

            if (tracked.Count == 0)
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

    private void NoteOverlay(ProcessReport report)
    {
        var overlay = report.InterestingModules
            .Where(path => path.Contains("gameoverlayrenderer", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (overlay.Count == 0)
        {
            return;
        }

        overlayReported.Add(report.Pid);
        log.Info($"OVERLAY: Steam injected into pid {report.Pid} \"{report.Name}\" after {clock.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)}s");
        foreach (var path in overlay)
        {
            log.Line($"            * {path}");
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
