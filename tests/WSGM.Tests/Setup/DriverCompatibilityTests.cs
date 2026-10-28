using WindowsDeviceControl;
using WSGM.Setup.Engine;

namespace WSGM.Tests.Setup;

public sealed class DriverCompatibilityTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "wsgm-protection-" + Guid.NewGuid());

    [Fact]
    public void UninstallRestoresAbsentOverridesAndPreservesPreexistingExclusions()
    {
        var system = new FakeSystem();
        var root = Path.Combine(_directory, "WSGM");
        system.Paths.Add(root);
        var service = Create(system);
        Assert.True(service.DisableMemoryIntegrity());
        Assert.True(service.DisableDriverBlocklist());
        Assert.True(service.DisableControlFlowGuard());
        service.AddDefenderExclusions(root);
        Assert.Equal(Path.Combine(root, "App", "WSGM.exe"), Assert.Single(system.Processes));

        Assert.True(service.Restore());
        Assert.Null(system.State.MemoryIntegrity);
        Assert.Null(system.State.VulnerableDriverBlocklist);
        Assert.Equal(ControlFlowGuardSetting.Default, system.State.ControlFlowGuard);
        Assert.Equal(root, Assert.Single(system.Paths));
        Assert.Empty(system.Processes);
    }

    [Fact]
    public void LaterUserChangesArePreservedAndFailedRestoreRetainsRecovery()
    {
        var system = new FakeSystem();
        var service = Create(system);
        service.DisableMemoryIntegrity();
        service.DisableDriverBlocklist();
        system.State = system.State with { MemoryIntegrity = true };
        system.FailBlocklist = true;
        Assert.False(service.Restore());
        Assert.True(system.State.MemoryIntegrity);
        Assert.True(File.Exists(Path.Combine(_directory, "recovery.json")));
        system.FailBlocklist = false;
        Assert.True(service.Restore());
        Assert.Null(system.State.VulnerableDriverBlocklist);
        Assert.False(File.Exists(Path.Combine(_directory, "recovery.json")));
    }

    [Fact]
    public void FatalSetupRollbackRemovesOnlyChangesFromThisRun()
    {
        var system = new FakeSystem();
        var service = Create(system);
        service.DisableControlFlowGuard();
        service.AddDefenderExclusions(Path.Combine(_directory, "WSGM"));
        Assert.True(service.RollbackChangesThisRun());
        Assert.Equal(ControlFlowGuardSetting.Default, system.State.ControlFlowGuard);
        Assert.Empty(system.Paths);
        Assert.Empty(system.Processes);
        Assert.False(File.Exists(Path.Combine(_directory, "recovery.json")));
    }

    private DriverCompatibility Create(FakeSystem system) =>
        new(Path.Combine(_directory, "recovery.json"), system);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class FakeSystem : IDriverCompatibilitySystem
    {
        internal WindowsProtectionState State = new(null, null, ControlFlowGuardSetting.Default, false);
        internal readonly List<string> Paths = [];
        internal readonly List<string> Processes = [];
        internal bool FailBlocklist;
        public WindowsProtectionState Read(bool controlFlowGuard) => controlFlowGuard ? State : State with { ControlFlowGuard = null };
        public void SetMemoryIntegrity(bool? value) => State = State with { MemoryIntegrity = value };
        public void SetDriverBlocklist(bool? value)
        {
            if (FailBlocklist)
            {
                throw new IOException("Simulated Windows refusal");
            }
            State = State with { VulnerableDriverBlocklist = value };
        }
        public void SetControlFlowGuard(ControlFlowGuardSetting value) => State = State with { ControlFlowGuard = value };
        public DefenderExclusionState ReadExclusions() => new(Paths.ToArray(), Processes.ToArray());
        public void AddPath(string path) => Paths.Add(path);
        public void AddProcess(string path) => Processes.Add(path);
        public void RemovePath(string path) => Paths.Remove(path);
        public void RemoveProcess(string path) => Processes.Remove(path);
    }
}
