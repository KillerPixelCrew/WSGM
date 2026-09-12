using System.Xml.Linq;
using WSGM.Core;

namespace WSGM.Tests;

/// <summary>A startup surface made of dictionaries. Every write is recorded, so a test can assert
/// what WSGM changed without going near this machine's registry or task scheduler.</summary>
internal sealed class FakeAutostartSystem : IAutostartSystem
{
    internal Dictionary<(SteamAutostartScope Scope, bool Wow64), Dictionary<string, string>> Run { get; } = [];
    internal Dictionary<(SteamAutostartScope Scope, string List, string Name), byte[]?> Approvals { get; } = [];
    internal Dictionary<SteamAutostartScope, Dictionary<string, string>> Shortcuts { get; } = [];
    internal Dictionary<string, string> Tasks { get; } = [];
    internal Dictionary<string, bool> TaskEnabled { get; } = [];
    internal List<string> Writes { get; } = [];
    internal bool TaskWritesFail { get; set; }

    public IReadOnlyDictionary<string, string> ReadRunValues(SteamAutostartScope scope, bool wow64) =>
        Run.TryGetValue((scope, wow64), out var values) ? values : [];

    public byte[]? ReadApproval(SteamAutostartScope scope, string list, string name) =>
        Approvals.TryGetValue((scope, list, name), out var value) ? value : null;

    public void WriteApproval(SteamAutostartScope scope, string list, string name, byte[]? value)
    {
        Writes.Add($"{scope}/{list}/{name}={(value is null ? "removed" : value[0].ToString())}");
        if (value is null) { Approvals.Remove((scope, list, name)); }
        else { Approvals[(scope, list, name)] = value; }
    }

    public IReadOnlyDictionary<string, string> ReadStartupShortcuts(SteamAutostartScope scope) =>
        Shortcuts.TryGetValue(scope, out var values) ? values : [];

    public IReadOnlyDictionary<string, string> ReadLogonTasks() => Tasks;

    public bool IsTaskEnabled(string taskPath) => TaskEnabled.TryGetValue(taskPath, out bool enabled) && enabled;

    public bool SetTaskEnabled(string taskPath, bool enabled)
    {
        Writes.Add($"task/{taskPath}={enabled}");
        if (TaskWritesFail) { return false; }
        TaskEnabled[taskPath] = enabled;
        return true;
    }
}

public sealed class SteamAutostartScannerTests
{
    private const string SteamExe = @"C:\Program Files (x86)\Steam\steam.exe";

    private static FakeAutostartSystem WithRunValue(string name, string command) =>
        new() { Run = { [(SteamAutostartScope.User, false)] = new() { [name] = command } } };

    [Theory]
    [InlineData("\"C:\\Program Files (x86)\\Steam\\steam.exe\" -silent")]
    [InlineData(@"C:\Program Files (x86)\Steam\steam.exe -silent")]
    [InlineData(@"c:\program files (x86)\steam\STEAM.EXE")]
    public void EveryWayOfSpellingSteamsOwnEntryIsFound(string command)
    {
        var found = SteamAutostartScanner.Scan(WithRunValue("Steam", command), SteamExe);

        var source = Assert.Single(found);
        Assert.Equal(SteamAutostartKind.RunValue, source.Kind);
        Assert.True(source.Enabled);
        Assert.False(source.NeedsElevation);
    }

    [Theory]
    // Another program that merely mentions Steam, and Steam's own tools, are not Steam.
    [InlineData(@"C:\Program Files\Wallpaper Engine\wallpaper64.exe -steam")]
    [InlineData(@"C:\Program Files (x86)\Steam\steamerrorreporter.exe")]
    [InlineData("")]
    public void SomethingElseIsLeftAlone(string command)
        => Assert.Empty(SteamAutostartScanner.Scan(WithRunValue("Other", command), SteamExe));

    [Fact]
    public void ASecondSteamInstallationIsNotThisOne()
        => Assert.Empty(SteamAutostartScanner.Scan(
            WithRunValue("Steam", @"D:\Games\Steam\steam.exe"), SteamExe));

    [Fact]
    public void WithoutAKnownInstallationAnySteamCounts()
    {
        var found = SteamAutostartScanner.Scan(WithRunValue("Steam", @"D:\Games\Steam\steam.exe"), null);

        Assert.Single(found);
    }

    [Fact]
    public void AnEnvironmentVariableInTheCommandIsExpanded()
    {
        var system = WithRunValue("Steam", "\"%ProgramFiles(x86)%\\Steam\\steam.exe\" -silent");

        Assert.Single(SteamAutostartScanner.Scan(system, SteamExe));
    }

    [Theory]
    // Byte zero carries the flag; an absent value is Windows' enabled default.
    [InlineData(null, true)]
    [InlineData(new byte[] { 2, 0, 0, 0, 0, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 3, 0, 0, 0, 0, 0, 0, 0 }, false)]
    public void WindowsOwnApprovalStateIsRead(byte[]? approval, bool expected)
    {
        var system = WithRunValue("Steam", SteamExe);
        if (approval is not null) { system.Approvals[(SteamAutostartScope.User, "Run", "Steam")] = approval; }

        Assert.Equal(expected, Assert.Single(SteamAutostartScanner.Scan(system, SteamExe)).Enabled);
    }

    [Fact]
    public void AStartupShortcutAndALogonTaskAreBothFound()
    {
        FakeAutostartSystem system = new()
        {
            Shortcuts = { [SteamAutostartScope.User] = new() { ["Steam.lnk"] = SteamExe } },
            Tasks = { [@"\Steam"] = SteamExe },
            TaskEnabled = { [@"\Steam"] = true },
        };

        var found = SteamAutostartScanner.Scan(system, SteamExe);

        Assert.Equal(2, found.Count);
        var shortcut = found.Single(source => source.Kind is SteamAutostartKind.StartupShortcut);
        Assert.False(shortcut.NeedsElevation);
        var task = found.Single(source => source.Kind is SteamAutostartKind.ScheduledTask);
        // A task always needs elevation to change, whoever registered it.
        Assert.True(task.NeedsElevation);
        Assert.Equal(@"\Steam", task.Location);
    }

    [Fact]
    public void AMachineRunValueNeedsElevation()
    {
        FakeAutostartSystem system = new()
        {
            Run = { [(SteamAutostartScope.Machine, true)] = new() { ["Steam"] = SteamExe } },
        };

        var source = Assert.Single(SteamAutostartScanner.Scan(system, SteamExe));
        Assert.True(source.NeedsElevation);
        Assert.True(source.Wow64);
    }

    [Fact]
    public void TaskDefinitionsAreSplitAndNamedByTheirOwnUri()
    {
        const string output = """
            <?xml version="1.0" encoding="UTF-16"?>
            <!-- \Other -->
            <Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><URI>\Other</URI></RegistrationInfo>
              <Actions><Exec><Command>C:\other.exe</Command></Exec></Actions>
            </Task>
            <?xml version="1.0" encoding="UTF-16"?>
            <!-- \Steam -->
            <Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><URI>\Steam</URI></RegistrationInfo>
              <Triggers><LogonTrigger /></Triggers>
              <Settings><Enabled>false</Enabled></Settings>
              <Actions><Exec><Command>C:\steam.exe</Command></Exec></Actions>
            </Task>
            """;

        var definitions = AutostartSystem.SplitTaskDefinitions(output).ToArray();

        Assert.Equal([@"\Other", @"\Steam"], definitions.Select(entry => entry.Path));
        XNamespace ns = definitions[1].Definition.Name.Namespace;
        Assert.Equal("false", definitions[1].Definition.Element(ns + "Settings")?.Element(ns + "Enabled")?.Value);
    }
}

public sealed class SteamAutostartTakeoverTests
{
    private const string SteamExe = @"C:\Steam\steam.exe";

    private static SteamAutostartSource RunSource(bool enabled = true) =>
        new(SteamAutostartKind.RunValue, SteamAutostartScope.User, @"HKCU\...\Run", "Steam", SteamExe, enabled);

    private static SteamAutostartSource TaskSource() =>
        new(SteamAutostartKind.ScheduledTask, SteamAutostartScope.Machine, @"\Steam", @"\Steam", SteamExe, true);

    /// <summary>Collects records the way the configuration does: one per entry, updated in place.
    /// The takeover records the same entry twice, pending and then confirmed.</summary>
    private static Action<SteamAutostartRecord> Collect(List<SteamAutostartRecord> records) =>
        record => { if (!records.Contains(record)) { records.Add(record); } };

    [Fact]
    public void ThePreviousStateIsRecordedBeforeTheWrite()
    {
        FakeAutostartSystem system = new()
        {
            Approvals = { [(SteamAutostartScope.User, "Run", "Steam")] = [2, 0, 0, 0, 0, 0, 0, 0] },
        };
        List<(string Write, bool Pending)> order = [];

        var result = SteamAutostartTakeover.Disable(system, [RunSource()], elevated: false, record =>
            order.Add((system.Writes.Count == 0 ? "before" : "after", record.Pending)));

        Assert.Single(result.Disabled);
        // Recorded pending first, so an interrupted disable is still undoable.
        Assert.Equal([("before", true), ("after", false)], order);
    }

    [Fact]
    public void AnUnverifiedWriteStaysPending()
    {
        FakeAutostartSystem system = new();
        List<SteamAutostartRecord> records = [];
        // A surface that silently keeps the entry enabled: the readback is what decides.
        system.Approvals[(SteamAutostartScope.User, "Run", "Steam")] = [2, 0, 0, 0, 0, 0, 0, 0];
        FakeRefusingSystem refusing = new(system);

        var result = SteamAutostartTakeover.Disable(refusing, [RunSource()], elevated: false, Collect(records));

        Assert.Empty(result.Disabled);
        Assert.Single(result.Pending);
        Assert.True(Assert.Single(records).Pending);
    }

    [Fact]
    public void AMachineSourceIsLeftAloneWithoutElevation()
    {
        FakeAutostartSystem system = new();

        var result = SteamAutostartTakeover.Disable(system, [TaskSource()], elevated: false, _ => { });

        Assert.Single(result.NeedsElevation);
        Assert.Empty(system.Writes);
        Assert.False(result.Complete);
    }

    [Fact]
    public void AnAlreadyDisabledSourceIsNotTouched()
    {
        FakeAutostartSystem system = new();

        var result = SteamAutostartTakeover.Disable(system, [RunSource(enabled: false)], elevated: false, _ => { });

        Assert.Empty(result.Disabled);
        Assert.Empty(system.Writes);
        Assert.True(result.Complete);
    }

    [Fact]
    public void RestorePutsBackTheExactPreviousValue()
    {
        FakeAutostartSystem system = new()
        {
            Approvals = { [(SteamAutostartScope.User, "Run", "Steam")] = [2, 0, 0, 0, 0, 0, 0, 0] },
        };
        List<SteamAutostartRecord> records = [];
        SteamAutostartTakeover.Disable(system, [RunSource()], elevated: false, Collect(records));

        var restored = SteamAutostartTakeover.Restore(system, records, elevated: false);

        Assert.Single(restored);
        Assert.Equal<byte[]?>([2, 0, 0, 0, 0, 0, 0, 0], system.Approvals[(SteamAutostartScope.User, "Run", "Steam")]);
    }

    [Fact]
    public void RestoreRemovesAValueWindowsNeverHad()
    {
        FakeAutostartSystem system = new();
        List<SteamAutostartRecord> records = [];
        SteamAutostartTakeover.Disable(system, [RunSource()], elevated: false, Collect(records));

        SteamAutostartTakeover.Restore(system, records, elevated: false);

        Assert.False(system.Approvals.ContainsKey((SteamAutostartScope.User, "Run", "Steam")));
    }

    [Fact]
    public void ADecisionTheUserMadeAfterwardsSurvivesRestore()
    {
        FakeAutostartSystem system = new();
        List<SteamAutostartRecord> records = [];
        SteamAutostartTakeover.Disable(system, [RunSource()], elevated: false, Collect(records));
        // The user turned it back on in Task Manager; those are not WSGM's bytes any more.
        system.Approvals[(SteamAutostartScope.User, "Run", "Steam")] = [2, 0, 0, 0, 0, 0, 0, 0];
        system.Writes.Clear();

        var restored = SteamAutostartTakeover.Restore(system, records, elevated: false);

        Assert.Single(restored);
        Assert.Empty(system.Writes);
        Assert.Equal<byte[]?>([2, 0, 0, 0, 0, 0, 0, 0], system.Approvals[(SteamAutostartScope.User, "Run", "Steam")]);
    }

    [Fact]
    public void ATaskIsDisabledAndReEnabledByRestore()
    {
        FakeAutostartSystem system = new() { TaskEnabled = { [@"\Steam"] = true } };
        List<SteamAutostartRecord> records = [];

        var result = SteamAutostartTakeover.Disable(system, [TaskSource()], elevated: true, Collect(records));
        Assert.Single(result.Disabled);
        Assert.False(system.TaskEnabled[@"\Steam"]);

        SteamAutostartTakeover.Restore(system, records, elevated: true);
        Assert.True(system.TaskEnabled[@"\Steam"]);
    }

    [Fact]
    public void ARefusedTaskWriteStaysPending()
    {
        FakeAutostartSystem system = new() { TaskEnabled = { [@"\Steam"] = true }, TaskWritesFail = true };

        var result = SteamAutostartTakeover.Disable(system, [TaskSource()], elevated: true, _ => { });

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
            Name = @"\Steam",
        };

        Assert.Empty(SteamAutostartTakeover.Restore(system, [record], elevated: false));
        Assert.Empty(system.Writes);
    }

    /// <summary>A surface whose approval write does not take, standing in for a policy or a tool
    /// that puts the entry straight back.</summary>
    private sealed class FakeRefusingSystem(FakeAutostartSystem inner) : IAutostartSystem
    {
        public IReadOnlyDictionary<string, string> ReadRunValues(SteamAutostartScope scope, bool wow64) =>
            inner.ReadRunValues(scope, wow64);
        public byte[]? ReadApproval(SteamAutostartScope scope, string list, string name) =>
            inner.ReadApproval(scope, list, name);
        public void WriteApproval(SteamAutostartScope scope, string list, string name, byte[]? value) =>
            inner.Writes.Add($"{scope}/{list}/{name}=refused");
        public IReadOnlyDictionary<string, string> ReadStartupShortcuts(SteamAutostartScope scope) =>
            inner.ReadStartupShortcuts(scope);
        public IReadOnlyDictionary<string, string> ReadLogonTasks() => inner.ReadLogonTasks();
        public bool IsTaskEnabled(string taskPath) => inner.IsTaskEnabled(taskPath);
        public bool SetTaskEnabled(string taskPath, bool enabled) => inner.SetTaskEnabled(taskPath, enabled);
    }
}
