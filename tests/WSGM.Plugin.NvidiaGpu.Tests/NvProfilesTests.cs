using System.Text.Json;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Plugin.Gpu;
using WSGM.Plugin.Sdk;
using WSGM.Testing;
using Xunit;

namespace WSGM.Plugin.NvidiaGpu.Tests;

public sealed class NvProfilesTests
{
    private const uint Setting = 0x1057eb71;

    [Fact]
    public void InheritRestoresPriorExplicitValueAndPreservesUnrelatedSettings()
    {
        using var directory = new TemporaryDirectory();
        var api = new Drs();
        api.Values[Setting] = 7;
        api.Values[123] = 99;
        var profiles = new NvProfiles(api, directory.Root);
        Assert.Empty(profiles.Sync(Sync(1, 9), Controls(api), CancellationToken.None).Failures);
        Assert.Equal(9u, api.Values[Setting]);
        Assert.Equal(1, profiles.Sync(Empty(2), Controls(api), CancellationToken.None).Removed);
        Assert.Equal(7u, api.Values[Setting]);
        Assert.Equal(99u, api.Values[123]);
    }

    [Fact]
    public void InheritRemovesOnlyOwnedOverrideAndReadsTheCurrentGlobalValue()
    {
        using var directory = new TemporaryDirectory();
        var api = new Drs();
        var profiles = new NvProfiles(api, directory.Root);
        profiles.Sync(Sync(1, 9), Controls(api), CancellationToken.None);
        api.Global = 12;
        profiles.Sync(Empty(2), Controls(api), CancellationToken.None);
        Assert.False(api.Values.ContainsKey(Setting));
        Assert.Equal((12u, false), api.Get(2, Setting));
    }

    [Fact]
    public void ExternalEditIsPreservedDuringSyncAndRemoval()
    {
        using var directory = new TemporaryDirectory();
        var api = new Drs();
        var profiles = new NvProfiles(api, directory.Root);
        profiles.Sync(Sync(1, 9), Controls(api), CancellationToken.None);
        api.Values[Setting] = 12;
        Assert.Single(profiles.Sync(Sync(2, 9), Controls(api), CancellationToken.None).Failures);
        profiles.Sync(Empty(3), Controls(api), CancellationToken.None);
        Assert.Equal(12u, api.Values[Setting]);
        Assert.Equal(1, api.Saves);
    }

    [Fact]
    public void UnconfirmedSaveIsNotRepeatedAfterReloadOrProcessRestart()
    {
        using var directory = new TemporaryDirectory();
        var api = new Drs { FailSave = true };
        var profiles = new NvProfiles(api, directory.Root);
        Assert.Single(profiles.Sync(Sync(1, 9), Controls(api), CancellationToken.None).Failures);
        api.FailSave = false;
        profiles = new NvProfiles(api, directory.Root);
        Assert.Single(profiles.Sync(Sync(2, 9), Controls(api), CancellationToken.None).Failures);
        Assert.Equal(1, api.Saves);
        Assert.False(api.Values.ContainsKey(Setting));
    }

    [Fact]
    public void SaveThatCommittedBeforeFailureIsConfirmedWithoutAnotherWrite()
    {
        using var directory = new TemporaryDirectory();
        var api = new Drs { FailAfterSave = true };
        var profiles = new NvProfiles(api, directory.Root);
        Assert.Single(profiles.Sync(Sync(1, 9), Controls(api), CancellationToken.None).Failures);
        api.FailAfterSave = false;
        Assert.Empty(profiles.Sync(Sync(2, 9), Controls(api), CancellationToken.None).Failures);
        Assert.Equal(1, api.Saves);
        Assert.Equal(9u, api.Values[Setting]);
    }

    [Fact]
    public void UnconfirmedRestorationIsNotRepeated()
    {
        using var directory = new TemporaryDirectory();
        var api = new Drs();
        api.Values[Setting] = 7;
        var profiles = new NvProfiles(api, directory.Root);
        profiles.Sync(Sync(1, 9), Controls(api), CancellationToken.None);
        api.FailSave = true;
        Assert.Single(profiles.Sync(Empty(2), Controls(api), CancellationToken.None).Failures);
        api.FailSave = false;
        Assert.Single(profiles.Sync(Empty(3), Controls(api), CancellationToken.None).Failures);
        Assert.Equal(2, api.Saves);
        Assert.Equal(9u, api.Values[Setting]);
    }

    [Fact]
    public void GamesSharingOneNativeProfileCannotRequestConflictingValues()
    {
        using var directory = new TemporaryDirectory();
        var api = new Drs();
        var profiles = new NvProfiles(api, directory.Root);
        var sync = new ApplicationProfileSync(1, 1,
            [Game("one", "one.exe", 9), Game("two", "two.exe", 10)]);
        Assert.Equal(2, profiles.Sync(sync, Controls(api), CancellationToken.None).Failures.Count);
        Assert.Equal(0, api.Saves);
    }

    [Fact]
    public void SameValueInSharedNativeProfileIsWrittenOnce()
    {
        using var directory = new TemporaryDirectory();
        var api = new Drs();
        var profiles = new NvProfiles(api, directory.Root);
        var sync = new ApplicationProfileSync(1, 1,
            [Game("one", "one.exe", 9), Game("two", "two.exe", 9)]);
        Assert.Equal(2, profiles.Sync(sync, Controls(api), CancellationToken.None).Written);
        Assert.Equal(1, api.Saves);
    }

    [Fact]
    public void OlderSyncCannotRemoveNewerOverrides()
    {
        using var directory = new TemporaryDirectory();
        var api = new Drs();
        var profiles = new NvProfiles(api, directory.Root);
        profiles.Sync(Sync(2, 9), Controls(api), CancellationToken.None);
        profiles.Sync(Empty(1), Controls(api), CancellationToken.None);
        Assert.Equal(9u, api.Values[Setting]);
    }

    [Fact]
    public void CorruptJournalAbortsWithoutReplacingIt()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Root, "nvidia-profiles.v1.json");
        File.WriteAllText(path, "broken");
        Assert.Throws<JsonException>(() => new NvProfiles(new Drs(), directory.Root));
        Assert.Equal("broken", File.ReadAllText(path));
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
        internal int Saves { get; private set; }
        internal bool FailSave { get; set; }
        internal bool FailAfterSave { get; set; }

        public void Load()
        {
            _pending = new Dictionary<uint, uint>(Values);
        }

        public void Save()
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
            return profile != 1 && _pending.TryGetValue(setting, out var value) ? (value, true) : (Global, false);
        }

        public void Set(nint profile, uint setting, uint value)
        {
            _pending[setting] = value;
        }

        public void Inherit(nint profile, uint setting)
        {
            _pending.Remove(setting);
        }
    }
}
