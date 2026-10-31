using WSGM.Core;

namespace WSGM.Tests.Core;

public sealed class RtssDiscoveryTests
{
    [Fact]
    public void InstalledFilesAndRunningProcess_AreAcceptedWithoutLoadingRtssCode()
    {
        var environment = FakeDiscoveryEnvironment.Valid();

        var probe = new RtssDiscovery(environment).Probe();

        Assert.Equal(RtssAvailability.AdapterUnavailable, probe.Availability);
        Assert.Equal("7.3.7", probe.Version);
        Assert.NotEqual(0, probe.Generation);
        Assert.Equal(environment.Processes.Single().ProcessId, probe.ProcessId);
    }

    [Fact]
    public void SimilarlyNamedProcessOutsideInstallFolder_IsIgnored()
    {
        var environment = FakeDiscoveryEnvironment.Valid();
        environment.Processes =
        [
            new RtssProcessIdentity(
                999,
                @"C:\Users\player\Downloads\RTSS.exe",
                DateTimeOffset.UnixEpoch)
        ];

        var probe = new RtssDiscovery(environment).Probe();

        Assert.Equal(RtssAvailability.NotRunning, probe.Availability);
        Assert.Equal(0L, probe.Generation);
    }

    [Fact]
    public void VersionOlderThan73_IsIncompatible()
    {
        var environment = FakeDiscoveryEnvironment.Valid();
        environment.Version = new Version(7, 2, 3);

        var probe = new RtssDiscovery(environment).Probe();

        Assert.Equal(RtssAvailability.Incompatible, probe.Availability);
    }

    [Fact]
    public void MissingProfileApi_IsIncompatible()
    {
        var environment = FakeDiscoveryEnvironment.Valid();
        environment.Files.Remove($@"{FakeDiscoveryEnvironment.InstallRoot}\RTSSHooks64.dll");

        var probe = new RtssDiscovery(environment).Probe();

        Assert.Equal(RtssAvailability.Incompatible, probe.Availability);
    }

    [Fact]
    public void NoRegisteredFolder_IsNotInstalled()
    {
        var environment = FakeDiscoveryEnvironment.Valid();
        environment.InstallLocation = null;

        var probe = new RtssDiscovery(environment).Probe();

        Assert.Equal(RtssAvailability.NotInstalled, probe.Availability);
    }

    private sealed class FakeDiscoveryEnvironment : IRtssDiscoveryEnvironment
    {
        public const string InstallRoot = @"C:\Program Files (x86)\RivaTuner Statistics Server";

        public string? InstallLocation { get; set; } = InstallRoot;

        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase)
        {
            $@"{InstallRoot}\RTSS.exe",
            $@"{InstallRoot}\RTSSHooks64.dll"
        };

        public Version Version { get; set; } = new(7, 3, 7);

        public IReadOnlyList<RtssProcessIdentity> Processes { get; set; } = [];

        public string? ReadInstallLocation()
        {
            return InstallLocation;
        }

        public bool FileExists(string path)
        {
            return Files.Contains(path);
        }

        public Version ReadProductVersion(string path)
        {
            return Version;
        }

        public IReadOnlyList<RtssProcessIdentity> ReadProcesses()
        {
            return Processes;
        }

        public static FakeDiscoveryEnvironment Valid()
        {
            return new FakeDiscoveryEnvironment
            {
                Processes =
                [
                    new RtssProcessIdentity(
                        321,
                        $@"{InstallRoot}\RTSS.exe",
                        DateTimeOffset.UnixEpoch)
                ]
            };
        }
    }
}
