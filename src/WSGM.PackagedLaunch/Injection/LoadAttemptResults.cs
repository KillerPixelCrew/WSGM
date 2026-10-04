using System;
using System.Collections.Generic;

namespace WSGM.PackagedLaunch;

/// <summary>Preserves the first load outcome without dispatching another attempt.</summary>
public sealed class LoadAttemptResults
{
    private readonly Dictionary<string, bool> _results = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Loads once per identity, treating an interrupted attempt as unsuccessful.</summary>
    /// <param name="identity">The process and component identity.</param>
    /// <param name="processLatched">Whether uncertainty has closed all work on the process.</param>
    /// <param name="load">The single load operation.</param>
    /// <returns>The first attempt's success result.</returns>
    public bool Load(string identity, bool processLatched, Func<bool> load)
    {
        if (processLatched)
        {
            return false;
        }

        if (_results.TryGetValue(identity, out var result))
        {
            return result;
        }

        // Record uncertainty before dispatch, so an exception cannot permit a second attempt.
        _results.Add(identity, false);
        result = load();
        _results[identity] = result;
        return result;
    }
}
