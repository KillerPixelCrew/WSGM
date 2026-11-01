using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using WSGM.Interop;

namespace WSGM.Core;

/// <summary>
///     Current-session process queries by name or exact image path, for liveness polls that need no
///     window enumeration.
/// </summary>
public static class ProcessLookup
{
    // Names whose session-id query has already been reported once. The callers are
    // polls, so an unthrottled warning per pid per tick would flood the capped log.
    private static readonly ConcurrentDictionary<string, byte> WarnedSessionIdNames =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     This process's session id, read once: a process cannot change
    ///     sessions, and the callers are polls that must not leak a Process handle
    ///     per query.
    /// </summary>
    public static int CurrentSessionId { get; } = ReadCurrentSessionId();

    private static int ReadCurrentSessionId()
    {
        using var self = Process.GetCurrentProcess();
        return self.SessionId;
    }

    /// <summary>Finds process identifiers whose names appear in a semicolon-separated allowlist.</summary>
    /// <param name="semicolonNames">Case-insensitive process names separated by semicolons.</param>
    /// <returns>The matching process identifiers.</returns>
    public static HashSet<uint> FindProcessIds(string semicolonNames)
    {
        var result = new HashSet<uint>();
        // Disposed, and resolved once per process: this runs on the splash's 250 ms
        // Big-Picture poll and the 5 s Steam monitor, so an undisposed Process per
        // call leaked a handle four times a second for the life of the session. Our
        // own session id cannot change while we run.
        var session = CurrentSessionId;
        foreach (var name in semicolonNames.Split(';',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var plain = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
            foreach (var p in Process.GetProcessesByName(plain))
            {
                // Other sessions (RDP, fast user switching) run their own Steam and
                // startup tools — only this session's processes count.
                try
                {
                    if (p.SessionId == session)
                    {
                        result.Add((uint)p.Id);
                    }
                }
                // Catch EVERYTHING, deliberately, and warn only once per name: this
                // feeds the splash's Big Picture detection poll, where a propagated
                // exception or an unthrottled log line each broke a boot — see
                // docs\boot-and-shell.md, "The detection poll must not throw". Do not narrow it.
                catch (Exception ex)
                {
                    if (WarnedSessionIdNames.TryAdd(plain, 0))
                    {
                        Log.Warn($"Session id unreadable for {plain} (pid {p.Id}): {ex.Message}. "
                                 + "Further occurrences for this name are not logged.");
                    }
                }
                finally
                {
                    p.Dispose();
                }
            }
        }

        return result;
    }

    /// <summary>Reads exact executable liveness; an inaccessible matching process is unknown.</summary>
    /// <param name="paths">Configured executable paths to expand and normalize; duplicate keys are case-insensitive.</param>
    /// <returns>
    ///     Original path keys mapped to current-session liveness: true for an exact match, false for absent, null for
    ///     inaccessible candidates.
    /// </returns>
    internal static IReadOnlyDictionary<string, bool?> ReadRunningPaths(IEnumerable<string> paths)
    {
        var targets = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(path => path,
            path => Path.GetFullPath(Environment.ExpandEnvironmentVariables(path)), StringComparer.OrdinalIgnoreCase);
        var result = targets.Keys.ToDictionary(path => path, _ => (bool?)false, StringComparer.OrdinalIgnoreCase);
        // Process names carry no extension, so targets are grouped by file name once per read.
        var byName = new Dictionary<string, List<KeyValuePair<string, string>>>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in targets)
        {
            var name = Path.GetFileNameWithoutExtension(target.Value);
            if (!byName.TryGetValue(name, out var group))
            {
                group = [];
                byName[name] = group;
            }

            group.Add(target);
        }

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                List<KeyValuePair<string, string>>? candidates = null;
                try
                {
                    if (!byName.TryGetValue(process.ProcessName, out candidates)
                        || process.SessionId != CurrentSessionId)
                    {
                        continue;
                    }

                    // The limited query right reads an elevated process's image too, where
                    // MainModule would need a full module snapshot and fails across integrity.
                    var image = NativeShellProcess.TryGetImagePath(checked((uint)process.Id));
                    foreach (var target in candidates)
                    {
                        if (image is null)
                        {
                            if (result[target.Key] != true)
                            {
                                result[target.Key] = null;
                            }
                        }
                        else if (string.Equals(Path.GetFullPath(image), target.Value,
                                     StringComparison.OrdinalIgnoreCase))
                        {
                            result[target.Key] = true;
                        }
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    if (candidates is null)
                    {
                        continue;
                    }

                    foreach (var target in candidates)
                    {
                        if (result[target.Key] != true)
                        {
                            result[target.Key] = null;
                        }
                    }
                }
            }
        }

        return result;
    }

    /// <summary>
    ///     Best-effort check that a process's image path equals the expected full
    ///     path. Unreadable processes count as matching (fail-open) so the caller's focus
    ///     poll cannot go blind when the image path cannot be queried.
    /// </summary>
    /// <param name="pid">The process to inspect.</param>
    /// <param name="expectedFullPath">The full image path required.</param>
    /// <returns>Whether the image path matches (or could not be read).</returns>
    public static bool ProcessImagePathEquals(uint pid, string expectedFullPath)
    {
        return NativeShellProcess.TryGetImagePath(pid) is not { } path
               || string.Equals(path, expectedFullPath, StringComparison.OrdinalIgnoreCase);
    }
}
