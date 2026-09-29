using Microsoft.Win32;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Tests;
using WSGM.Plugin.IntelGpu.Graphics;
using WSGM.Plugin.IntelGpu.Profiles;
using WSGM.Plugin.Sdk;
using Xunit;

namespace WSGM.Plugin.IntelGpu.Tests;

/// <summary>
///     The per-application sync against a disposable HKCU subtree standing in for the adapter's
///     <c>3DKeys</c>.
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
    private readonly string _keys;
    private readonly TemporaryRegistryKey _scope = new("intel-3dkeys");
    private readonly TemporaryDirectory _state = new();

    public ApplicationProfileSynchronizerTests()
    {
        _keys = $@"{_scope.Path}\0000\3DKeys";
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _scope.Dispose();
        _state.Dispose();
    }

    [Fact]
    public void AWriteRecordsTheNamesThatAppeared()
    {
        using var key = Registry.CurrentUser.CreateSubKey(_keys);
        key.SetValue("game.exe_Existing", 1);
        var synchronizer = Create();

        var result = synchronizer.Apply(Sync(1, ("p1", "game.exe", "graphics.cmaa", "enhance")), Resolve,
            CancellationToken.None);

        Assert.Equal(1, result.Written);
        var entry = Assert.Single(synchronizer.Record.Entries);
        Assert.Equal(["game.exe_Cmaa"], entry.Names.Select(value => value.Name));
    }

    [Fact]
    public void RemovalDeletesOnlyTheRecordedNames()
    {
        using var key = Registry.CurrentUser.CreateSubKey(_keys);
        key.SetValue("game.exe_Existing", 1);
        key.SetValue("other.exe_Cmaa", 1);
        var synchronizer = Create();
        synchronizer.Apply(Sync(1, ("p1", "game.exe", "graphics.cmaa", "enhance")), Resolve, CancellationToken.None);

        var result = synchronizer.Apply(Sync(2), Resolve, CancellationToken.None);

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
        using var key = Registry.CurrentUser.CreateSubKey(_keys);
        var synchronizer = Create();
        synchronizer.Apply(
            Sync(1, ("p1", "game.exe", "graphics.endurance-gaming", "on"),
                ("p1", "game.exe", "graphics.endurance-gaming-target", "battery")),
            Resolve,
            CancellationToken.None);

        synchronizer.Apply(Sync(2, ("p1", "game.exe", "graphics.endurance-gaming", "on")), Resolve,
            CancellationToken.None);

        Assert.NotNull(key.GetValue("game.exe_EnduranceGaming"));
        Assert.Single(synchronizer.Record.Entries);
    }

    [Fact]
    public void ThePerApplicationSwitchIsWrittenOnceAndRemovedWithTheLastOverride()
    {
        // Intel's sample: a per-application value applies only once feature 15 says per-application.
        using var key = Registry.CurrentUser.CreateSubKey(_keys);
        var synchronizer = Create();

        synchronizer.Apply(
            Sync(1, ("p1", "game.exe", "graphics.switched-cmaa", "enhance"),
                ("p1", "game.exe", "graphics.switched-low-latency", "on")),
            Resolve,
            CancellationToken.None);

        Assert.NotNull(key.GetValue("game.exe_GlobalOrPerApp"));
        var switchEntry = Assert.Single(synchronizer.Record.Entries,
            entry => entry.CapabilityId == SwitchRecordId);
        Assert.Equal(["game.exe_GlobalOrPerApp"], switchEntry.Names.Select(value => value.Name));
        Assert.All(synchronizer.Record.Entries.Where(entry => entry != switchEntry),
            entry => Assert.DoesNotContain(entry.Names, value => value.Name == "game.exe_GlobalOrPerApp"));

        synchronizer.Apply(Sync(2, ("p1", "game.exe", "graphics.switched-cmaa", "enhance")), Resolve,
            CancellationToken.None);
        Assert.NotNull(key.GetValue("game.exe_GlobalOrPerApp"));

        synchronizer.Apply(Sync(3), Resolve, CancellationToken.None);
        Assert.Null(key.GetValue("game.exe_GlobalOrPerApp"));
        Assert.Empty(synchronizer.Record.Entries);
    }

    [Fact]
    public void AnOlderRevisionIsSkipped()
    {
        Registry.CurrentUser.CreateSubKey(_keys).Dispose();
        var synchronizer = Create();
        synchronizer.Apply(Sync(5, ("p1", "game.exe", "graphics.cmaa", "enhance")), Resolve, CancellationToken.None);

        var result = synchronizer.Apply(Sync(4), Resolve, CancellationToken.None);

        Assert.Equal((0, 0), (result.Written, result.Removed));
        Assert.Single(synchronizer.Record.Entries);
    }

    [Fact]
    public void ARestartAcceptsAnyRevision()
    {
        // WSGM's revision restarts with WSGM, so a new process never compares it with an older run's.
        using var key = Registry.CurrentUser.CreateSubKey(_keys);
        Create().Apply(Sync(5, ("p1", "game.exe", "graphics.cmaa", "enhance")), Resolve, CancellationToken.None);

        var result = Create().Apply(Sync(1), Resolve, CancellationToken.None);

        Assert.Equal(1, result.Removed);
        Assert.Null(key.GetValue("game.exe_Cmaa"));
    }

    [Fact]
    public void AnotherProfileTakesOverTheNamesOfTheSameExecutable()
    {
        using var key = Registry.CurrentUser.CreateSubKey(_keys);
        var synchronizer = Create();
        synchronizer.Apply(Sync(1, ("p1", "game.exe", "graphics.cmaa", "enhance")), Resolve, CancellationToken.None);

        synchronizer.Apply(Sync(2, ("p2", "game.exe", "graphics.cmaa", "off")), Resolve, CancellationToken.None);

        var entry = Assert.Single(synchronizer.Record.Entries);
        Assert.Equal("p2", entry.ProfileId);
        Assert.Equal(["game.exe_Cmaa"], entry.Names.Select(value => value.Name));
        Assert.NotNull(key.GetValue("game.exe_Cmaa"));
    }

    [Fact]
    public void AnUnchangedOverrideIsConfirmedWithoutAWrite()
    {
        using var key = Registry.CurrentUser.CreateSubKey(_keys);
        var synchronizer = Create();
        synchronizer.Apply(Sync(1, ("p1", "game.exe", "graphics.cmaa", "enhance")), Resolve, CancellationToken.None);
        key.SetValue("game.exe_Cmaa", 42);

        var result = synchronizer.Apply(Sync(2, ("p1", "game.exe", "graphics.cmaa", "enhance")), Resolve,
            CancellationToken.None);

        Assert.Equal(1, result.Written);
        Assert.Equal(42, key.GetValue("game.exe_Cmaa"));
    }

    [Fact]
    public void TheRecordSurvivesARestart()
    {
        using var key = Registry.CurrentUser.CreateSubKey(_keys);
        Create().Apply(Sync(1, ("p1", "game.exe", "graphics.cmaa", "enhance")), Resolve, CancellationToken.None);

        var restarted = Create();
        restarted.Apply(Sync(2), Resolve, CancellationToken.None);

        Assert.Null(key.GetValue("game.exe_Cmaa"));
    }

    [Fact]
    public void ARefusedWriteIsReportedAndRecordsNothing()
    {
        Registry.CurrentUser.CreateSubKey(_keys).Dispose();
        var synchronizer = Create();

        var result = synchronizer.Apply(Sync(1, ("p1", "game.exe", "graphics.refused", "on")), Resolve,
            CancellationToken.None);

        var failure = Assert.Single(result.Failures);
        Assert.Equal("graphics.refused", failure.CapabilityId);
        Assert.Empty(synchronizer.Record.Entries);
    }

    [Fact]
    public void AnUnknownCapabilityIsRefused()
    {
        var result = Create().Apply(Sync(1, ("p1", "game.exe", "display.scaling", "centered")), Resolve,
            CancellationToken.None);

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
        using (var adapter = _scope.Create("0000"))
        {
            adapter.SetValue("MatchingDeviceId", @"PCI\VEN_8086&DEV_4688");
        }

        using (var other = _scope.Create("0001"))
        {
            other.SetValue("MatchingDeviceId", @"pci\ven_10de&dev_2520");
        }

        var keys = ThreeDKeysLocator.Find(
            AdapterClassKey.Enumerate(Registry.CurrentUser, _scope.Path, IntelLog.None),
            0x4688);

        Assert.Equal([$@"{_scope.Path}\0000\3DKeys"], keys);
    }

    private ApplicationProfileSynchronizer Create()
    {
        return new ApplicationProfileSynchronizer(Registry.CurrentUser, _state.Root, IntelLog.None);
    }

    private INativeProfileTarget? Resolve(string capabilityId, string? instanceId)
    {
        if (instanceId != Instance)
        {
            return null;
        }

        return capabilityId switch
        {
            "graphics.cmaa" => new FakeTarget(_keys, "Cmaa", "cmaa", false),
            "graphics.endurance-gaming" or "graphics.endurance-gaming-target" =>
                new FakeTarget(_keys, "EnduranceGaming", "endurance", false),
            "graphics.refused" => new FakeTarget(_keys, "Refused", "refused", true),
            "graphics.switched-cmaa" => new FakeTarget(_keys, "Cmaa", "cmaa", false)
            {
                ApplicationSwitch = new FakeSwitch(_keys)
            },
            "graphics.switched-low-latency" => new FakeTarget(_keys, "LowLatency", "low-latency", false)
            {
                ApplicationSwitch = new FakeSwitch(_keys)
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
    private sealed class FakeTarget(string keyPath, string setting, string group, bool refuse) : INativeProfileTarget
    {
        public string GroupKey => $"{Instance}|{group}";

        public IReadOnlyList<string> RegistryKeys => [keyPath];

        public INativeApplicationSwitch? ApplicationSwitch { get; init; }

        public string? WriteForApplication(
            string executable,
            IReadOnlyList<(string CapabilityId, CapabilityValue Value)> values)
        {
            if (refuse)
            {
                return "The driver answered 0x4000000a.";
            }

            using var key = Registry.CurrentUser.CreateSubKey(keyPath);
            key.SetValue($"{executable}_{setting}", values.Count);
            return null;
        }
    }

    /// <summary>Writes feature 15's per-application value the way a feature write lands.</summary>
    private sealed class FakeSwitch(string keyPath) : INativeApplicationSwitch
    {
        public string RecordId => SwitchRecordId;

        public string GroupKey => $"{Instance}|15";

        public IReadOnlyList<string> RegistryKeys => [keyPath];

        public string? EnableFor(string executable)
        {
            using var key = Registry.CurrentUser.CreateSubKey(keyPath);
            key.SetValue($"{executable}_GlobalOrPerApp", 1);
            return null;
        }
    }
}
