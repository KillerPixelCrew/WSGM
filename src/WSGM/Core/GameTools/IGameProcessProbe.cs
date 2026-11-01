using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>Reports whether a game or its programs are running; the session that knows Steam supplies it.</summary>
internal interface IGameProcessProbe
{
    /// <summary>Whether Steam reports the game running.</summary>
    /// <param name="appId">The Steam AppID or non-Steam shortcut id.</param>
    /// <returns>True or false, or null when Steam's answer is unknown.</returns>
    bool? IsAppRunning(uint appId);

    /// <summary>Whether each program runs in the current session, matched by full image path.</summary>
    /// <param name="executables">Absolute executable paths.</param>
    /// <returns>Each path mapped to true when it runs, false when it doesn't and null when that is unknown.</returns>
    IReadOnlyDictionary<string, bool?> ReadRunningPaths(IReadOnlyList<string> executables);
}
