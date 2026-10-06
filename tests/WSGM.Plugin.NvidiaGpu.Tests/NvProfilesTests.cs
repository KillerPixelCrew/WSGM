using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Plugin.Gpu;
using WSGM.Plugin.Sdk;
using WSGM.Testing;
using Xunit;

namespace WSGM.Plugin.NvidiaGpu.Tests;

public sealed class NvProfilesTests
{
    private const uint Setting = 0x1057eb71;
    private static readonly WriteAdmission Admitted = new(CancellationToken.None, Deadline.Never, () => true);
    private static readonly Action<string, string> Ignore = (_, _) => { };

    [Fact]
    public void SupportProbeDoesNotMaterializeAnInheritedDrsSetting()
    {
        var api = new Drs();
        api.Values[123] = 99;
        var control = Controls(api).Values.Single();
        control.ProbeSupport(control.Read(), Admitted);
        Assert.Equal(0, api.Saves);
        Assert.False(api.Values.ContainsKey(Setting));
        Assert.Equal(99u, api.Values[123]);
    }

    [Fact]
    public void SupportProbeSavesAnExplicitDrsSettingWithoutChangingIt()
    {
        var api = new Drs { GlobalExplicit = true };
        api.Values[Setting] = 9;
        api.Values[123] = 99;
        var control = Controls(api).Values.Single();
        control.ProbeSupport(control.Read(), Admitted);
        Assert.Equal(1, api.Saves);
        Assert.Equal(9u, api.Values[Setting]);
        Assert.Equal(99u, api.Values[123]);
    }

    [Fact]
    public void InheritRestoresPriorExplicitValueAndPreservesUnrelatedSettings()
    {
        using var directory = new TemporaryDirectory();
        var api = new Drs();
        api.Values[Setting] = 7;
        api.Values[123] = 99;
        var profiles = new NvProfiles(api, directory.Root, Ignore);
        Assert.Empty(profiles.Sync(Sync(1, 9), Controls(api), Admitted, CancellationToken.None).Failures);
        Assert.Equal(9u, api.Values[Setting]);
        Assert.Equal(1, profiles.Sync(Empty(2), Controls(api), Admitted, CancellationToken.None).Removed);
        Assert.Equal(7u, api.Values[Setting]);
        Assert.Equal(99u, api.Values[123]);
    }

    [Fact]
    public void InheritRemovesOnlyOwnedOverrideAndReadsTheCurrentGlobalValue()
    {
        using var directory = new TemporaryDirectory();
        var api = new Drs();
        var profiles = new NvProfiles(api, directory.Root, Ignore);
        profiles.Sync(Sync(1, 9), Controls(api), Admitted, CancellationToken.None);
        api.Global = 12;
        profiles.Sync(Empty(2), Controls(api), Admitted, CancellationToken.None);
        Assert.False(api.Values.ContainsKey(Setting));
        Assert.Equal((12u, false), api.Get(2, Setting));
    }

    [Fact]
    public void ExternalEditIsPreservedDuringSyncAndRemoval()
    {
        using var directory = new TemporaryDirectory();
        var api = new Drs();
        var profiles = new NvProfiles(api, directory.Root, Ignore);
        profiles.Sync(Sync(1, 9), Controls(api), Admitted, CancellationToken.None);
        api.Values[Setting] = 12;
        Assert.Single(profiles.Sync(Sync(2, 9), Controls(api), Admitted, CancellationToken.None).Failures);
        profiles.Sync(Empty(3), Controls(api), Admitted, CancellationToken.None);
        Assert.Equal(12u, api.Values[Setting]);
        Assert.Equal(1, api.Saves);
    }

    [Fact]
    public void FailedSaveRestoresTheJournalAndTheNextRevisionWritesAgain()
    {
        using var directory = new TemporaryDirectory();
        var api = new Drs { FailSave = true };
        var profiles = new NvProfiles(api, directory.Root, Ignore);
        Assert.Single(profiles.Sync(Sync(1, 9), Controls(api), Admitted, CancellationToken.None).Failures);
        Assert.Empty(DriverStateFile.Read(Path.Combine(directory.Root, "nvidia-profiles.v1.json"),
            new NvProfileJournal([])).Entries);
        api.FailSave = false;
        Assert.Empty(profiles.Sync(Sync(2, 9), Controls(api), Admitted, CancellationToken.None).Failures);
        Assert.Equal(2, api.Saves);
        Assert.Equal(9u, api.Values[Setting]);
    }

    [Fact]
    public void SaveThatCommittedDespiteAFailureIsAdoptedWithoutAnotherWrite()
    {
        using var directory = new TemporaryDirectory();
        var api = new Drs { FailAfterSave = true };
        var profiles = new NvProfiles(api, directory.Root, Ignore);
        Assert.Single(profiles.Sync(Sync(1, 9), Controls(api), Admitted, CancellationToken.None).Failures);
        api.FailAfterSave = false;
        Assert.Empty(profiles.Sync(Sync(2, 9), Controls(api), Admitted, CancellationToken.None).Failures);
        Assert.Equal(1, api.Saves);
        Assert.Equal(9u, api.Values[Setting]);
    }

    [Fact]
    public void FailedRestorationKeepsTheEntryAndTheNextSyncRestoresAgain()
    {
        using var directory = new TemporaryDirectory();
        var api = new Drs();
        api.Values[Setting] = 7;
        var profiles = new NvProfiles(api, directory.Root, Ignore);
        profiles.Sync(Sync(1, 9), Controls(api), Admitted, CancellationToken.None);
        api.FailSave = true;
        Assert.Single(profiles.Sync(Empty(2), Controls(api), Admitted, CancellationToken.None).Failures);
        api.FailSave = false;
        Assert.Equal(1, profiles.Sync(Empty(3), Controls(api), Admitted, CancellationToken.None).Removed);
        Assert.Equal(3, api.Saves);
        Assert.Equal(7u, api.Values[Setting]);
    }

    [Fact]
    public void FailedRecordSaveAfterRestorationKeepsOwnershipUntilOnlyTheRecordCanBeSaved()
    {
        using var directory = new TemporaryDirectory();
        var api = new Drs();
        api.Values[Setting] = 7;
        var profiles = new NvProfiles(api, directory.Root, Ignore);
        profiles.Sync(Sync(1, 9), Controls(api), Admitted, CancellationToken.None);
        var path = Path.Combine(directory.Root, "nvidia-profiles.v1.json");
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Single(profiles.Sync(Empty(2), Controls(api), Admitted, CancellationToken.None).Failures);
            Assert.Equal(7u, api.Values[Setting]);
            Assert.Equal(2, api.Saves);
        }

        Assert.Single(DriverStateFile.Read(path, new NvProfileJournal([])).Entries);
        Assert.Equal(1, profiles.Sync(Empty(3), Controls(api), Admitted, CancellationToken.None).Removed);
        Assert.Equal(2, api.Saves);
        Assert.Empty(DriverStateFile.Read(path, new NvProfileJournal([])).Entries);
    }

    [Fact]
    public void GamesSharingOneNativeProfileCannotRequestConflictingValues()
    {
        using var directory = new TemporaryDirectory();
        var api = new Drs();
        var profiles = new NvProfiles(api, directory.Root, Ignore);
        var sync = new ApplicationProfileSync(1, 1,
            [Game("one", "one.exe", 9), Game("two", "two.exe", 10)]);
        Assert.Equal(2, profiles.Sync(sync, Controls(api), Admitted, CancellationToken.None).Failures.Count);
        Assert.Equal(0, api.Saves);
    }

    [Fact]
    public void SameValueInSharedNativeProfileIsWrittenOnce()
    {
        using var directory = new TemporaryDirectory();
        var api = new Drs();
        var profiles = new NvProfiles(api, directory.Root, Ignore);
        var sync = new ApplicationProfileSync(1, 1,
            [Game("one", "one.exe", 9), Game("two", "two.exe", 9)]);
        Assert.Equal(2, profiles.Sync(sync, Controls(api), Admitted, CancellationToken.None).Written);
        Assert.Equal(1, api.Saves);
    }

    [Fact]
    public void OlderSyncCannotRemoveNewerOverrides()
    {
        using var directory = new TemporaryDirectory();
        var api = new Drs();
        var profiles = new NvProfiles(api, directory.Root, Ignore);
        profiles.Sync(Sync(2, 9), Controls(api), Admitted, CancellationToken.None);
        profiles.Sync(Empty(1), Controls(api), Admitted, CancellationToken.None);
        Assert.Equal(9u, api.Values[Setting]);
    }

    [Theory]
    [InlineData("broken")]
    [InlineData("{}")]
    [InlineData("{\"Entries\":null}")]
    [InlineData("{\"Entries\":[null]}")]
    public void CorruptJournalFailsEverySyncWithoutReplacingIt(string content)
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Root, "nvidia-profiles.v1.json");
        File.WriteAllText(path, content);
        var api = new Drs();
        var reports = new List<string>();
        var profiles = new NvProfiles(api, directory.Root, (key, detail) => reports.Add(key + ": " + detail));
        var failure = Assert.Single(profiles.Sync(Sync(1, 9), Controls(api), Admitted, CancellationToken.None)
            .Failures);
        Assert.Equal("game.exe", failure.Executable);
        Assert.Equal(0, api.Saves);
        Assert.Single(reports);
        Assert.Equal(content, File.ReadAllText(path));
    }

    private static ApplicationProfileSync Empty(long revision)
    {
        return new ApplicationProfileSync(revision, 1, []);
    }

    private static ApplicationProfileSync Sync(long revision, uint value)
    {
        return new ApplicationProfileSync(revision, 1, [Game("game", "game.exe", value)]);
    }

    private static ApplicationCapabilityProfile Game(string id, string executable, uint value)
    {
        return new ApplicationCapabilityProfile(id, id, [executable],
        [
            new ApplicationCapabilityValue("driver.1057eb71", "driver",
                CapabilityValue.Choice(NvSettingControl.Encode(value)))
        ]);
    }

    private static Dictionary<string, NvSettingControl> Controls(Drs api)
    {
        return new Dictionary<string, NvSettingControl>
        {
            ["driver.1057eb71"] = new(api,
                new NvSettingDefinition(Setting, "Power", "graphics", true, true, []),
                "graphics", [7, 9, 10, 12])
        };
    }

    private sealed class Drs : INvProfiles
    {
        private Dictionary<uint, uint> _pending = [];
        internal Dictionary<uint, uint> Values { get; } = [];
        internal uint Global { get; set; } = 5;
        internal bool GlobalExplicit { get; set; }
        internal int Saves { get; private set; }
        internal bool FailSave { get; set; }
        internal bool FailAfterSave { get; set; }

        public void Load()
        {
            _pending = new Dictionary<uint, uint>(Values);
        }

        public void Save(WriteAdmission admission)
        {
            Saves++;
            if (FailSave)
            {
                throw new DriverFailure("Save failed before commit.", true);
            }

            Values.Clear();
            foreach (var pair in _pending)
            {
                Values.Add(pair.Key, pair.Value);
            }

            if (FailAfterSave)
            {
                throw new DriverFailure("Save failed after commit.", true);
            }
        }

        public nint GlobalProfile()
        {
            return 1;
        }

        public nint Application(string executable, bool create, string profileName)
        {
            return 2;
        }

        public nint FindProfile(string name)
        {
            return name == "Native game" ? 2 : 0;
        }

        public string ProfileName(nint profile)
        {
            return "Native game";
        }

        public (uint Value, bool Explicit) Get(nint profile, uint setting)
        {
            return (profile != 1 || GlobalExplicit) && _pending.TryGetValue(setting, out var value)
                ? (value, true)
                : (Global, false);
        }

        public void Set(nint profile, uint setting, uint value, WriteAdmission admission)
        {
            _pending[setting] = value;
        }

        public void Inherit(nint profile, uint setting, WriteAdmission admission)
        {
            _pending.Remove(setting);
        }
    }
}
