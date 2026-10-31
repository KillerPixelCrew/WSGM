using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>
///     Reads and writes the Windows startup surfaces. The seam exists so the matching and
///     takeover rules can be tested without touching this machine's registry or task scheduler.
/// </summary>
public interface IAutostartSystem
{
    /// <summary>Reads the values of one <c>Run</c> key.</summary>
    /// <param name="scope">Which hive to read.</param>
    /// <param name="wow64">Whether to read the 32-bit view.</param>
    /// <returns>Value name to command line.</returns>
    IReadOnlyDictionary<string, string> ReadRunValues(SteamAutostartScope scope, bool wow64);

    /// <summary>Reads one <c>StartupApproved</c> state, or null when Windows has never stored one.</summary>
    /// <param name="scope">Which hive to read.</param>
    /// <param name="list">The approval list: <c>Run</c>, <c>Run32</c> or <c>StartupFolder</c>.</param>
    /// <param name="name">The value or file name.</param>
    /// <returns>The raw approval bytes, or null.</returns>
    byte[]? ReadApproval(SteamAutostartScope scope, string list, string name);

    /// <summary>Writes or removes one <c>StartupApproved</c> state.</summary>
    /// <param name="scope">Which hive to write.</param>
    /// <param name="list">The approval list.</param>
    /// <param name="name">The value or file name.</param>
    /// <param name="value">The bytes to write, or null to remove the value.</param>
    void WriteApproval(SteamAutostartScope scope, string list, string name, byte[]? value);

    /// <summary>Lists the shortcuts in one Startup folder with the target each resolves to.</summary>
    /// <param name="scope">Which Startup folder to read.</param>
    /// <returns>File name to resolved target path.</returns>
    IReadOnlyDictionary<string, string> ReadStartupShortcuts(SteamAutostartScope scope);

    /// <summary>Lists every scheduled task with a logon trigger, as task path to executed command.</summary>
    /// <returns>Task path to the command its action runs.</returns>
    IReadOnlyDictionary<string, string> ReadLogonTasks();

    /// <summary>Whether the named task exists and is enabled.</summary>
    /// <param name="taskPath">The full task path.</param>
    /// <returns>The task's enabled state, or null when it does not exist or cannot be read.</returns>
    bool? ReadTaskEnabled(string taskPath);

    /// <summary>Enables or disables one scheduled task.</summary>
    /// <param name="taskPath">The full task path.</param>
    /// <param name="enabled">The state to set.</param>
    /// <returns>True when the change was accepted.</returns>
    bool SetTaskEnabled(string taskPath, bool enabled);
}
