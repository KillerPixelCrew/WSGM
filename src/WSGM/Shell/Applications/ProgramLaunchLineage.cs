using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WindowsDeviceControl;

namespace WSGM.Shell;

/// <summary>One launch's observed ancestry; filenames narrow reads but never establish ownership.</summary>
internal sealed class ProgramLaunchLineage(string executable, DateTime dispatched, string? physicalExecutable = null)
{
    private readonly Dictionary<uint, ProcessImageIdentity> _known = [];
    private readonly Dictionary<ulong, ProcessStartIdentity> _starts = [];
    private readonly HashSet<ulong> _sequences = [];

    /// <summary>Records kernel creation sequences, including bootstraps too brief for snapshots.</summary>
    internal void ObserveStart(ProcessStartIdentity start)
    {
        _starts[start.Sequence] = start;
        bool added;
        do
        {
            added = false;
            foreach (var item in _starts.Values)
            {
                if (_sequences.Contains(item.Sequence))
                {
                    continue;
                }

                var root = item.Identity.Started >= dispatched && IsRoot(item.Identity.Image);
                if (root || _sequences.Contains(item.ParentSequence))
                {
                    _sequences.Add(item.Sequence);
                    _known[item.Identity.Id] = item.Identity;
                    added = true;
                }
            }
        } while (added);
    }

    /// <summary>Admits exact roots created for this request and their verified descendants.</summary>
    /// <param name="snapshot">One process-parent snapshot.</param>
    /// <param name="inspect">Reads a live exact process identity in the session.</param>
    internal void Observe(IReadOnlyList<ProcessSnapshotEntry> snapshot,
        Func<uint, ProcessImageIdentity?> inspect)
    {
        // Multiple passes handle a child whose row precedes its parent. Previously observed exited
        // parents remain useful, but a reused parent id invalidates its old ancestry.
        var byId = snapshot.ToDictionary(process => process.Id);
        foreach (var known in _known.Values.ToArray())
        {
            if (byId.ContainsKey(known.Id) && inspect(known.Id) is { } live && live.Started != known.Started)
            {
                _known.Remove(known.Id);
            }
        }

        bool added;
        do
        {
            added = false;
            foreach (var process in snapshot)
            {
                if (_known.ContainsKey(process.Id))
                {
                    continue;
                }

                var rootName = string.Equals(process.Name, Path.GetFileName(executable),
                    StringComparison.OrdinalIgnoreCase);
                _known.TryGetValue(process.ParentId, out var parent);
                if ((!rootName && parent is null) || inspect(process.Id) is not { } identity)
                {
                    continue;
                }

                var root = identity.Started >= dispatched && IsRoot(identity.Image);
                var child = parent is not null && identity.Started >= parent.Started;
                if (root || child)
                {
                    _known.Add(identity.Id, identity);
                    added = true;
                }
            }
        } while (added);
    }

    /// <summary>Requires the same live creation identity, including after a process id is recycled.</summary>
    internal bool Owns(ProcessImageIdentity identity)
    {
        return _known.TryGetValue(identity.Id, out var known) && known.Started == identity.Started;
    }

    private bool IsRoot(string image)
    {
        return string.Equals(image, executable, StringComparison.OrdinalIgnoreCase)
               || physicalExecutable is not null
               && string.Equals(image, physicalExecutable, StringComparison.OrdinalIgnoreCase);
    }
}
