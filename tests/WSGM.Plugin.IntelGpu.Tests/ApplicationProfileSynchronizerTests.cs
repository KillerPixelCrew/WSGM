using System.Text.Json;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Plugin.Gpu;
using WSGM.Plugin.IntelGpu.Graphics;
using WSGM.Plugin.IntelGpu.Profiles;
using WSGM.Plugin.IntelGpu.Tests.Fakes;
using WSGM.Plugin.Sdk;
using WSGM.Testing;
using Xunit;

namespace WSGM.Plugin.IntelGpu.Tests;

/// <summary>
///     The per-application sync against an in-memory hive standing in for the adapter's <c>3DKeys</c>.
/// </summary>
/// <remarks>
///     The fake target writes what the driver was seen to write on 2026-09-29: one value named
///     <c>&lt;exe&gt;_&lt;Setting&gt;</c> per feature. The assertions pin the one rule that matters: WSGM
///     deletes only names it recorded appearing, never anything that was there before or that another
///     executable owns.
/// </remarks>
public sealed class ApplicationProfileSynchronizerTests : IDisposable
{
    private const string Instance = "pci-8086-4688-00-02-0";
    private const string SwitchRecordId = "graphics.per-application";
    private const string ClassPath = "Class";
    private const string Keys = $@"{ClassPath}\0000\3DKeys";
    private readonly MemoryRegistryNode _hive = new();
    private readonly TemporaryDirectory _state = new();
    private static WriteAdmission Admission => new(CancellationToken.None, Deadline.Never, static () => true);

    /// <inheritdoc />
    public void Dispose()
    {
        _state.Dispose();
    }

    [Theory]
    [InlineData("broken")]
    [InlineData("{}")]
    [InlineData("{\"Entries\":null}")]
    [InlineData("{\"Entries\":[null]}")]
    public void AnUnreadableOwnershipRecordNeverWritesOrReplacesTheOriginal(string content)
    {
        var path = Path.Combine(_state.Root, "application-profiles.v1.json");
        File.WriteAllText(path, content);
        var synchronizer = Create();

        var result = synchronizer.Apply(Sync(1, ("p1", "game.exe", "graphics.cmaa", "enhance")), Resolve,
            CancellationToken.None, Admission);

        Assert.Single(result.Failures);
        Assert.Equal(0, result.Written);
        Assert.Empty(_hive.Create(Keys).GetValueNames());
        Assert.Equal(content, File.ReadAllText(path));
    }

    [Fact]
    public void ALockedRecordIsPreservedWithoutChangingTheRegistry()
    {
        var path = Path.Combine(_state.Root, "application-profiles.v1.json");
        var content = JsonSerializer.Serialize(SyncRecord.Empty);
        File.WriteAllText(path, content);
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = Create().Apply(Sync(1, ("p1", "game.exe", "graphics.cmaa", "enhance")), Resolve,
                CancellationToken.None, Admission);
            Assert.Single(result.Failures);
            Assert.Empty(_hive.Create(Keys).GetValueNames());
        }

        Assert.Equal(content, File.ReadAllText(path));
    }

    [Fact]
    public void ADirectoryAtTheRecordPathIsPreservedWithoutChangingTheRegistry()
    {
        var path = Path.Combine(_state.Root, "application-profiles.v1.json");
        Directory.CreateDirectory(path);
        var result = Create().Apply(Sync(1, ("p1", "game.exe", "graphics.cmaa", "enhance")), Resolve,
            CancellationToken.None, Admission);
        Assert.Single(result.Failures);
        Assert.Empty(_hive.Create(Keys).GetValueNames());
        Assert.True(Directory.Exists(path));
    }

    [Fact]
    public void DuplicateOwnersArePreservedInsteadOfDroppingOneOwnersNames()
    {
        var path = Path.Combine(_state.Root, "application-profiles.v1.json");
        var entry = new SyncEntry("p1", "game.exe", "graphics.cmaa", Instance, "enhance",
            [new RegistryValueName(Keys, "game.exe_Cmaa")]);
        var content = JsonSerializer.Serialize(new SyncRecord([
            entry, entry with
            {
                Names = [new RegistryValueName(Keys, "game.exe_Second")]
            }
        ]));
        File.WriteAllText(path, content);
        var key = _hive.Create(Keys);
        key.Set("game.exe_Cmaa", 1);
        key.Set("game.exe_Second", 2);
        var result = Create().Apply(Sync(1, ("p1", "game.exe", "graphics.cmaa", "off")), Resolve,
            CancellationToken.None, Admission);
        Assert.Single(result.Failures);
        Assert.Equal(content, File.ReadAllText(path));
        Assert.Equal(1, key.GetValue("game.exe_Cmaa"));
        Assert.Equal(2, key.GetValue("game.exe_Second"));
    }

    [Fact]
    public void RepeatedRevisionCannotRemoveAnAppliedOverride()
    {
        var key = _hive.Create(Keys);
        var synchronizer = Create();
        synchronizer.Apply(Sync(5, ("p1", "game.exe", "graphics.cmaa", "enhance")), Resolve,
            CancellationToken.None, Admission);
        var result = synchronizer.Apply(Sync(5), Resolve, CancellationToken.None, Admission);
        Assert.Equal((0, 0), (result.Written, result.Removed));
        Assert.NotNull(key.GetValue("game.exe_Cmaa"));
        Assert.Single(synchronizer.Record.Entries);
    }

    [Fact]
    public void FailedRecordSaveIsReportedAgainWithoutRepeatingTheDriverWrite()
    {
        var path = Path.Combine(_state.Root, "application-profiles.v1.json");
        var key = _hive.Create(Keys);
        var synchronizer = Create();
        Directory.CreateDirectory(path);
        try
        {
            var first = synchronizer.Apply(Sync(1, ("p1", "game.exe", "graphics.cmaa", "enhance")), Resolve,
                CancellationToken.None, Admission);
            Assert.Single(first.Failures);
            Assert.Equal(0, first.Written);
            Assert.NotNull(key.GetValue("game.exe_Cmaa"));
            key.Set("game.exe_Cmaa", 42);
            var second = synchronizer.Apply(Sync(2, ("p1", "game.exe", "graphics.cmaa", "enhance")), Resolve,
                CancellationToken.None, Admission);
            Assert.Single(second.Failures);
            Assert.Equal(42, key.GetValue("game.exe_Cmaa"));
        }
        finally
        {
            Directory.Delete(path);
        }

        var third = synchronizer.Apply(Sync(3, ("p1", "game.exe", "graphics.cmaa", "enhance")), Resolve,
            CancellationToken.None, Admission);
        Assert.Empty(third.Failures);
        Assert.Equal(42, key.GetValue("game.exe_Cmaa"));
        Assert.Single(DriverStateFile.Read(path, SyncRecord.Empty).Entries);
    }

    [Fact]
    public void AWriteRecordsTheNamesThatAppeared()
    {
        var key = _hive.Create(Keys);
        key.Set("game.exe_Existing", 1);
        var synchronizer = Create();

        var result = synchronizer.Apply(Sync(1, ("p1", "game.exe", "graphics.cmaa", "enhance")), Resolve,
            CancellationToken.None, Admission);

        Assert.Equal(1, result.Written);
        var entry = Assert.Single(synchronizer.Record.Entries);
        Assert.Equal(["game.exe_Cmaa"], entry.Names.Select(value => value.Name));
    }

    [Fact]
    public void RemovalDeletesOnlyTheRecordedNames()
    {
        var key = _hive.Create(Keys);
        key.Set("game.exe_Existing", 1);
        key.Set("other.exe_Cmaa", 1);
        var synchronizer = Create();
        synchronizer.Apply(Sync(1, ("p1", "game.exe", "graphics.cmaa", "enhance")), Resolve, CancellationToken.None,
            Admission);

        var result = synchronizer.Apply(Sync(2), Resolve, CancellationToken.None, Admission);

        Assert.Equal(1, result.Removed);
        Assert.Empty(synchronizer.Record.Entries);
        Assert.Null(key.GetValue("game.exe_Cmaa"));
        Assert.NotNull(key.GetValue("game.exe_Existing"));
        Assert.NotNull(key.GetValue("other.exe_Cmaa"));
    }

    [Fact]
    public void ANameAnotherEntryStillHoldsIsKept()
    {
        // Endurance Gaming's control and target are one driver value, so dropping one keeps the other.
        var key = _hive.Create(Keys);
        var synchronizer = Create();
        synchronizer.Apply(
            Sync(1, ("p1", "game.exe", "graphics.endurance-gaming", "on"),
                ("p1", "game.exe", "graphics.endurance-gaming-target", "battery")),
            Resolve,
            CancellationToken.None, Admission);

        synchronizer.Apply(Sync(2, ("p1", "game.exe", "graphics.endurance-gaming", "on")), Resolve,
            CancellationToken.None, Admission);

        Assert.NotNull(key.GetValue("game.exe_EnduranceGaming"));
        Assert.Single(synchronizer.Record.Entries);
    }

    [Fact]
    public void ThePerApplicationSwitchIsWrittenOnceAndRemovedWithTheLastOverride()
    {
        // Intel's sample: a per-application value applies only once feature 15 says per-application.
        var key = _hive.Create(Keys);
        var synchronizer = Create();

        synchronizer.Apply(
            Sync(1, ("p1", "game.exe", "graphics.switched-cmaa", "enhance"),
                ("p1", "game.exe", "graphics.switched-low-latency", "on")),
            Resolve,
            CancellationToken.None, Admission);

        Assert.NotNull(key.GetValue("game.exe_GlobalOrPerApp"));
        var switchEntry = Assert.Single(synchronizer.Record.Entries,
            entry => entry.CapabilityId == SwitchRecordId);
        Assert.Equal(["game.exe_GlobalOrPerApp"], switchEntry.Names.Select(value => value.Name));
        Assert.All(synchronizer.Record.Entries.Where(entry => entry != switchEntry),
            entry => Assert.DoesNotContain(entry.Names, value => value.Name == "game.exe_GlobalOrPerApp"));

        synchronizer.Apply(Sync(2, ("p1", "game.exe", "graphics.switched-cmaa", "enhance")), Resolve,
            CancellationToken.None, Admission);
        Assert.NotNull(key.GetValue("game.exe_GlobalOrPerApp"));

        synchronizer.Apply(Sync(3), Resolve, CancellationToken.None, Admission);
        Assert.Null(key.GetValue("game.exe_GlobalOrPerApp"));
        Assert.Empty(synchronizer.Record.Entries);
    }

    [Fact]
    public void AnOlderRevisionIsSkipped()
    {
        _hive.Create(Keys);
        var synchronizer = Create();
        synchronizer.Apply(Sync(5, ("p1", "game.exe", "graphics.cmaa", "enhance")), Resolve, CancellationToken.None,
            Admission);

        var result = synchronizer.Apply(Sync(4), Resolve, CancellationToken.None, Admission);

        Assert.Equal((0, 0), (result.Written, result.Removed));
        Assert.Single(synchronizer.Record.Entries);
    }

    [Fact]
    public void ARestartAcceptsAnyRevision()
    {
        // WSGM's revision restarts with WSGM, so a new process never compares it with an older run's.
        var key = _hive.Create(Keys);
        Create().Apply(Sync(5, ("p1", "game.exe", "graphics.cmaa", "enhance")), Resolve, CancellationToken.None,
            Admission);

        var result = Create().Apply(Sync(1), Resolve, CancellationToken.None, Admission);

        Assert.Equal(1, result.Removed);
        Assert.Null(key.GetValue("game.exe_Cmaa"));
    }

    [Fact]
    public void AnotherProfileTakesOverTheNamesOfTheSameExecutable()
    {
        var key = _hive.Create(Keys);
        var synchronizer = Create();
        synchronizer.Apply(Sync(1, ("p1", "game.exe", "graphics.cmaa", "enhance")), Resolve, CancellationToken.None,
            Admission);

        synchronizer.Apply(Sync(2, ("p2", "game.exe", "graphics.cmaa", "off")), Resolve, CancellationToken.None,
            Admission);

        var entry = Assert.Single(synchronizer.Record.Entries);
        Assert.Equal("p2", entry.ProfileId);
        Assert.Equal(["game.exe_Cmaa"], entry.Names.Select(value => value.Name));
        Assert.NotNull(key.GetValue("game.exe_Cmaa"));
    }

    [Fact]
    public void AnUnchangedOverrideIsConfirmedWithoutAWrite()
    {
        var key = _hive.Create(Keys);
        var synchronizer = Create();
        synchronizer.Apply(Sync(1, ("p1", "game.exe", "graphics.cmaa", "enhance")), Resolve, CancellationToken.None,
            Admission);
        key.Set("game.exe_Cmaa", 42);

        var result = synchronizer.Apply(Sync(2, ("p1", "game.exe", "graphics.cmaa", "enhance")), Resolve,
            CancellationToken.None, Admission);

        Assert.Equal(1, result.Written);
        Assert.Equal(42, key.GetValue("game.exe_Cmaa"));
    }

    [Fact]
    public void TheRecordSurvivesARestart()
    {
        var key = _hive.Create(Keys);
        Create().Apply(Sync(1, ("p1", "game.exe", "graphics.cmaa", "enhance")), Resolve, CancellationToken.None,
            Admission);

        var restarted = Create();
        restarted.Apply(Sync(2), Resolve, CancellationToken.None, Admission);

        Assert.Null(key.GetValue("game.exe_Cmaa"));
    }

    [Fact]
    public void ARefusedWriteIsReportedAndRecordsNothing()
    {
        _hive.Create(Keys);
        var synchronizer = Create();

        var result = synchronizer.Apply(Sync(1, ("p1", "game.exe", "graphics.refused", "on")), Resolve,
            CancellationToken.None, Admission);

        var failure = Assert.Single(result.Failures);
        Assert.Equal("graphics.refused", failure.CapabilityId);
        Assert.Empty(synchronizer.Record.Entries);
    }

    [Fact]
    public void AnUnknownCapabilityIsRefused()
    {
        var result = Create().Apply(Sync(1, ("p1", "game.exe", "display.scaling", "centered")), Resolve,
            CancellationToken.None, Admission);

        Assert.Single(result.Failures);
    }

    [Theory]
    [InlineData("game.exe", true)]
    [InlineData(@"C:\Games\game.exe", false)]
    [InlineData("spiel\u00e4.exe", false)]
    [InlineData("", false)]
    public void OnlyPlainAsciiFileNamesAreWritten(string executable, bool expected)
    {
        Assert.Equal(expected, ApplicationProfileSynchronizer.IsExecutableName(executable));
    }

    [Fact]
    public void TheLocatorFindsTheAdapterByDeviceId()
    {
        _hive.Create($@"{ClassPath}\0000").Set("MatchingDeviceId", @"PCI\VEN_8086&DEV_4688");
        _hive.Create($@"{ClassPath}\0001").Set("MatchingDeviceId", @"pci\ven_10de&dev_2520");

        var keys = ThreeDKeysLocator.Find(AdapterClassKey.Enumerate(_hive, ClassPath, IntelLog.None), 0x4688);

        Assert.Equal([Keys], keys);
    }

    private ApplicationProfileSynchronizer Create()
    {
        return new ApplicationProfileSynchronizer(_hive, _state.Root, IntelLog.None);
    }

    private INativeProfileTarget? Resolve(string capabilityId, string? instanceId)
    {
        if (instanceId != Instance)
        {
            return null;
        }

        return capabilityId switch
        {
            "graphics.cmaa" => new FakeTarget(_hive, "Cmaa", "cmaa", false),
            "graphics.endurance-gaming" or "graphics.endurance-gaming-target" =>
                new FakeTarget(_hive, "EnduranceGaming", "endurance", false),
            "graphics.refused" => new FakeTarget(_hive, "Refused", "refused", true),
            "graphics.switched-cmaa" => new FakeTarget(_hive, "Cmaa", "cmaa", false)
            {
                ApplicationSwitch = new FakeSwitch(_hive)
            },
            "graphics.switched-low-latency" => new FakeTarget(_hive, "LowLatency", "low-latency", false)
            {
                ApplicationSwitch = new FakeSwitch(_hive)
            },
            _ => null
        };
    }

    private static ApplicationProfileSync Sync(
        long revision,
        params (string Profile, string Executable, string Capability, string Value)[] overrides)
    {
        return new ApplicationProfileSync(
            revision,
            1,
            [
                .. overrides.GroupBy(entry => (entry.Profile, entry.Executable)).Select(group =>
                    new ApplicationCapabilityProfile(
                        group.Key.Profile,
                        group.Key.Profile,
                        [group.Key.Executable],
                        [
                            .. group.Select(entry => new ApplicationCapabilityValue(entry.Capability, Instance,
                                CapabilityValue.Choice(entry.Value)))
                        ]))
            ]);
    }

    /// <summary>Writes one value per feature the way the driver does.</summary>
    private sealed class FakeTarget(MemoryRegistryNode hive, string setting, string group, bool refuse)
        : INativeProfileTarget
    {
        public string GroupKey => $"{Instance}|{group}";

        public IReadOnlyList<string> RegistryKeys => [Keys];

        public INativeApplicationSwitch? ApplicationSwitch { get; init; }

        public string? WriteForApplication(
            string executable,
            IReadOnlyList<(string CapabilityId, CapabilityValue Value)> values, WriteAdmission admission)
        {
            if (refuse)
            {
                return "The driver answered 0x4000000a.";
            }

            admission.Check();
            hive.Create(Keys).Set($"{executable}_{setting}", values.Count);
            return null;
        }
    }

    /// <summary>Writes feature 15's per-application value the way a feature write lands.</summary>
    private sealed class FakeSwitch(MemoryRegistryNode hive) : INativeApplicationSwitch
    {
        public string RecordId => SwitchRecordId;

        public string GroupKey => $"{Instance}|15";

        public IReadOnlyList<string> RegistryKeys => [Keys];

        public string? EnableFor(string executable, WriteAdmission admission)
        {
            admission.Check();
            hive.Create(Keys).Set($"{executable}_GlobalOrPerApp", 1);
            return null;
        }
    }
}
