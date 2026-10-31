using WSGM.Core;

namespace WSGM.Tests.Fakes;

/// <summary>
///     A startup surface made of dictionaries. Every write is recorded, so a test can assert
///     what WSGM changed without going near this machine's registry or task scheduler.
/// </summary>
internal sealed class FakeAutostartSystem : IAutostartSystem
{
    internal Dictionary<(SteamAutostartScope Scope, bool Wow64), Dictionary<string, string>> Run { get; } = [];
    internal Dictionary<(SteamAutostartScope Scope, string List, string Name), byte[]?> Approvals { get; } = [];
    internal Dictionary<SteamAutostartScope, Dictionary<string, string>> Shortcuts { get; } = [];
    internal Dictionary<string, string> Tasks { get; } = [];
    internal Dictionary<string, bool> TaskEnabled { get; } = [];
    internal List<string> Writes { get; } = [];
    internal bool TaskWritesFail { get; init; }
    internal bool StaleTaskReads { get; init; }
    internal bool TaskReadsThrow { get; init; }
    internal int ApprovalReads { get; private set; }
    internal int TaskReads { get; private set; }

    /// <summary>Makes a missing task read as enabled, as the live <see cref="AutostartSystem" /> does.</summary>
    internal bool MissingTasksReadEnabled { get; init; }

    public IReadOnlyDictionary<string, string> ReadRunValues(SteamAutostartScope scope, bool wow64)
    {
        return Run.TryGetValue((scope, wow64), out var values) ? values : [];
    }

    public byte[]? ReadApproval(SteamAutostartScope scope, string list, string name)
    {
        ApprovalReads++;
        return Approvals.GetValueOrDefault((scope, list, name));
    }

    public void WriteApproval(SteamAutostartScope scope, string list, string name, byte[]? value)
    {
        Writes.Add($"{scope}/{list}/{name}={(value is null ? "removed" : value[0].ToString())}");
        if (value is null)
        {
            Approvals.Remove((scope, list, name));
        }
        else
        {
            Approvals[(scope, list, name)] = value;
        }
    }

    public IReadOnlyDictionary<string, string> ReadStartupShortcuts(SteamAutostartScope scope)
    {
        return Shortcuts.TryGetValue(scope, out var values) ? values : [];
    }

    public IReadOnlyDictionary<string, string> ReadLogonTasks()
    {
        return Tasks;
    }

    public bool IsTaskEnabled(string taskPath)
    {
        TaskReads++;
        if (TaskReadsThrow)
        {
            throw new IOException("Task state is unreadable.");
        }

        if (StaleTaskReads)
        {
            return true;
        }

        return TaskEnabled.TryGetValue(taskPath, out var enabled) ? enabled : MissingTasksReadEnabled;
    }

    public bool? ReadTaskEnabled(string taskPath)
    {
        return TaskEnabled.TryGetValue(taskPath, out var enabled) ? enabled : null;
    }

    public bool SetTaskEnabled(string taskPath, bool enabled)
    {
        Writes.Add($"task/{taskPath}={enabled}");
        if (TaskWritesFail)
        {
            return false;
        }

        TaskEnabled[taskPath] = enabled;
        return true;
    }
}
