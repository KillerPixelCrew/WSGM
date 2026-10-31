using WSGM.Core;
using WSGM.Tests.Fakes;

namespace WSGM.Tests.Core;

public sealed class SteamAutostartTakeoverTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void TaskRestoreWritesWithoutQueryingAndRetainsTheRecordWhenRefused(bool throwingRead, bool refused)
    {
        FakeAutostartSystem system = new()
        {
            TaskEnabled = { [@"\Steam"] = false },
            StaleTaskReads = true,
            TaskReadsThrow = throwingRead,
            TaskWritesFail = refused
        };
        SteamAutostartRecord record = new()
        {
            Kind = SteamAutostartKind.ScheduledTask,
            Scope = SteamAutostartScope.Machine,
            Location = @"\Steam",
            Name = @"\Steam"
        };

        var restored = SteamAutostartTakeover.Restore(system, [record], true);

        Assert.Equal(0, system.TaskReads);
        Assert.Equal([@"task/\Steam=True"], system.Writes);
        Assert.Equal(!refused, system.TaskEnabled[@"\Steam"]);
        if (refused)
        {
            Assert.Empty(restored);
        }
        else
        {
            Assert.Same(record, Assert.Single(restored));
        }
    }

    [Fact]
    public void AFailedRecoveryRecordLeavesTasksAndApprovalsUntouched()
    {
        byte[] previous = [2, 0, 0, 0, 0, 0, 0, 0];
        FakeAutostartSystem system = new()
        {
            TaskEnabled = { [@"\Steam"] = true },
            Approvals = { [(SteamAutostartScope.User, "Run", "Steam")] = previous }
        };
        var result = SteamAutostartTakeover.Disable(system, [RunSource(), TaskSource()], true,
            entry => SteamAutostartService.RecordDisabled(entry, _ => throw new IOException("record failed")));

        Assert.Empty(result.Disabled);
        Assert.Equal(2, result.Pending.Count);
        Assert.Empty(system.Writes);
        Assert.True(system.TaskEnabled[@"\Steam"]);
        Assert.Equal(previous, system.Approvals[(SteamAutostartScope.User, "Run", "Steam")]);
    }

    [Fact]
    public void ASuccessfulTaskDisableDoesNotWaitForAnEnabledRead()
    {
        FakeAutostartSystem system = new() { TaskEnabled = { [@"\Steam"] = true }, StaleTaskReads = true };
        List<SteamAutostartRecord> records = [];

        var result = SteamAutostartTakeover.Disable(system, [TaskSource()], true, Collect(records));

        Assert.Single(result.Disabled);
        Assert.Empty(result.Pending);
        Assert.False(Assert.Single(records).Pending);
        Assert.Equal(0, system.TaskReads);
        Assert.False(system.TaskEnabled[@"\Steam"]);
    }

    private static SteamAutostartSource RunSource(bool enabled = true)
    {
        return new SteamAutostartSource(SteamAutostartKind.RunValue, SteamAutostartScope.User, @"HKCU\...\Run", "Steam",
            enabled);
    }

    private static SteamAutostartSource TaskSource()
    {
        return new SteamAutostartSource(SteamAutostartKind.ScheduledTask, SteamAutostartScope.Machine, @"\Steam",
            @"\Steam", true);
    }

    /// <summary>
    ///     Collects records the way the configuration does: one per entry, updated in place.
    ///     The takeover records the same entry twice, pending and then confirmed.
    /// </summary>
    private static Action<SteamAutostartRecord> Collect(List<SteamAutostartRecord> records)
    {
        return record =>
        {
            if (!records.Contains(record))
            {
                records.Add(record);
            }
        };
    }

    [Fact]
    public void ThePreviousStateIsRecordedBeforeTheWrite()
    {
        FakeAutostartSystem system = new()
        {
            Approvals = { [(SteamAutostartScope.User, "Run", "Steam")] = [2, 0, 0, 0, 0, 0, 0, 0] }
        };
        List<(string Write, bool Pending)> order = [];

        var result = SteamAutostartTakeover.Disable(system, [RunSource()], false, record =>
            order.Add((system.Writes.Count == 0 ? "before" : "after", record.Pending)));

        Assert.Single(result.Disabled);
        // Recorded pending first, so an interrupted disable is still undoable.
        Assert.Equal([("before", true), ("after", false)], order);
    }

    [Fact]
    public void ASuccessfulApprovalWriteDoesNotWaitForAnEnabledRead()
    {
        FakeAutostartSystem system = new();
        List<SteamAutostartRecord> records = [];
        // The API accepted the write; a stale read cannot turn that into a pending outcome.
        system.Approvals[(SteamAutostartScope.User, "Run", "Steam")] = [2, 0, 0, 0, 0, 0, 0, 0];
        FakeRefusingSystem refusing = new(system);

        var result = SteamAutostartTakeover.Disable(refusing, [RunSource()], false, Collect(records));

        Assert.Single(result.Disabled);
        Assert.Empty(result.Pending);
        var record = Assert.Single(records);
        Assert.False(record.Pending);
        Assert.Equal(Convert.ToBase64String(refusing.Written!), record.WrittenApproval);
        Assert.Equal(1, system.ApprovalReads);
    }

    [Fact]
    public void AMachineSourceIsLeftAloneWithoutElevation()
    {
        FakeAutostartSystem system = new();

        var result = SteamAutostartTakeover.Disable(system, [TaskSource()], false, _ => { });

        Assert.Single(result.NeedsElevation);
        Assert.Empty(system.Writes);
        Assert.False(result.Complete);
    }

    [Fact]
    public void AnAlreadyDisabledSourceIsNotTouched()
    {
        FakeAutostartSystem system = new();

        var result = SteamAutostartTakeover.Disable(system, [RunSource(false)], false, _ => { });

        Assert.Empty(result.Disabled);
        Assert.Empty(system.Writes);
        Assert.True(result.Complete);
    }

    [Fact]
    public void RestorePutsBackTheExactPreviousValue()
    {
        FakeAutostartSystem system = new()
        {
            Approvals = { [(SteamAutostartScope.User, "Run", "Steam")] = [2, 0, 0, 0, 0, 0, 0, 0] }
        };
        List<SteamAutostartRecord> records = [];
        SteamAutostartTakeover.Disable(system, [RunSource()], false, Collect(records));

        var restored = SteamAutostartTakeover.Restore(system, records, false);

        Assert.Single(restored);
        Assert.Equal([2, 0, 0, 0, 0, 0, 0, 0], system.Approvals[(SteamAutostartScope.User, "Run", "Steam")]);
    }

    [Fact]
    public void RestoreRemovesAValueWindowsNeverHad()
    {
        FakeAutostartSystem system = new();
        List<SteamAutostartRecord> records = [];
        SteamAutostartTakeover.Disable(system, [RunSource()], false, Collect(records));

        SteamAutostartTakeover.Restore(system, records, false);

        Assert.False(system.Approvals.ContainsKey((SteamAutostartScope.User, "Run", "Steam")));
    }

    [Fact]
    public void ADecisionTheUserMadeAfterwardsSurvivesRestore()
    {
        FakeAutostartSystem system = new();
        List<SteamAutostartRecord> records = [];
        SteamAutostartTakeover.Disable(system, [RunSource()], false, Collect(records));
        // The user turned it back on in Task Manager; those are not WSGM's bytes any more.
        system.Approvals[(SteamAutostartScope.User, "Run", "Steam")] = [2, 0, 0, 0, 0, 0, 0, 0];
        system.Writes.Clear();

        var restored = SteamAutostartTakeover.Restore(system, records, false);

        Assert.Single(restored);
        Assert.Empty(system.Writes);
        Assert.Equal([2, 0, 0, 0, 0, 0, 0, 0], system.Approvals[(SteamAutostartScope.User, "Run", "Steam")]);
    }

    [Fact]
    public void ATaskIsDisabledAndReEnabledByRestore()
    {
        FakeAutostartSystem system = new() { TaskEnabled = { [@"\Steam"] = true } };
        List<SteamAutostartRecord> records = [];

        var result = SteamAutostartTakeover.Disable(system, [TaskSource()], true, Collect(records));
        Assert.Single(result.Disabled);
        Assert.False(system.TaskEnabled[@"\Steam"]);

        SteamAutostartTakeover.Restore(system, records, true);
        Assert.True(system.TaskEnabled[@"\Steam"]);
    }

    [Fact]
    public void ARefusedTaskWriteStaysPending()
    {
        FakeAutostartSystem system = new() { TaskEnabled = { [@"\Steam"] = true }, TaskWritesFail = true };

        var result = SteamAutostartTakeover.Disable(system, [TaskSource()], true, _ => { });

        Assert.Single(result.Pending);
        Assert.True(system.TaskEnabled[@"\Steam"]);
    }

    [Fact]
    public void RestoreLeavesMachineScopeToAnElevatedRun()
    {
        FakeAutostartSystem system = new() { TaskEnabled = { [@"\Steam"] = false } };
        SteamAutostartRecord record = new()
        {
            Kind = SteamAutostartKind.ScheduledTask,
            Scope = SteamAutostartScope.Machine,
            Location = @"\Steam",
            Name = @"\Steam"
        };

        Assert.Empty(SteamAutostartTakeover.Restore(system, [record], false));
        Assert.Empty(system.Writes);
    }

    /// <summary>
    ///     A surface whose approval write does not take, standing in for a policy or a tool
    ///     that puts the entry straight back.
    /// </summary>
    private sealed class FakeRefusingSystem(FakeAutostartSystem inner) : IAutostartSystem
    {
        internal byte[]? Written { get; private set; }

        public IReadOnlyDictionary<string, string> ReadRunValues(SteamAutostartScope scope, bool wow64)
        {
            return inner.ReadRunValues(scope, wow64);
        }

        public byte[]? ReadApproval(SteamAutostartScope scope, string list, string name)
        {
            return inner.ReadApproval(scope, list, name);
        }

        public void WriteApproval(SteamAutostartScope scope, string list, string name, byte[]? value)
        {
            Written = value;
            inner.Writes.Add($"{scope}/{list}/{name}=refused");
        }

        public IReadOnlyDictionary<string, string> ReadStartupShortcuts(SteamAutostartScope scope)
        {
            return inner.ReadStartupShortcuts(scope);
        }

        public IReadOnlyDictionary<string, string> ReadLogonTasks()
        {
            return inner.ReadLogonTasks();
        }

        public bool IsTaskEnabled(string taskPath)
        {
            return inner.IsTaskEnabled(taskPath);
        }

        public bool? ReadTaskEnabled(string taskPath)
        {
            return inner.ReadTaskEnabled(taskPath);
        }

        public bool SetTaskEnabled(string taskPath, bool enabled)
        {
            return inner.SetTaskEnabled(taskPath, enabled);
        }
    }
}
