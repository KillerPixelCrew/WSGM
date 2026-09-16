using WSGM.Core;

namespace WSGM.Tests.Core;

public sealed class RtssDiscoveryTests
{
    [Fact]
    public void VerifiedRegistrationApiAndProcess_AreAcceptedWithoutLoadingRtssCode()
    {
        var environment = FakeDiscoveryEnvironment.Valid();

        var probe = new RtssDiscovery(environment).Probe();

        Assert.Equal(RtssAvailability.AdapterUnavailable, probe.Availability);
        Assert.Equal("7.3.7", probe.Version);
        Assert.NotEqual(0, probe.Generation);
    }

    [Fact]
    public void SimilarlyNamedProcessOutsideVerifiedInstall_IsIgnored()
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
    public void OldRegistration_IsIncompatible()
    {
        var environment = FakeDiscoveryEnvironment.Valid();
        environment.Records =
        [
            environment.Records[0] with { DisplayVersion = "7.2.3" }
        ];

        var probe = new RtssDiscovery(environment).Probe();

        Assert.Equal(RtssAvailability.Incompatible, probe.Availability);
    }

    [Fact]
    public void MissingProfileApiExport_IsIncompatible()
    {
        var environment = FakeDiscoveryEnvironment.Valid();
        environment.ApiIdentity = environment.ApiIdentity with
        {
            Exports = new HashSet<string>(StringComparer.Ordinal)
            {
                "LoadProfile",
                "SaveProfile",
                "GetProfileProperty",
                "SetProfileProperty"
            }
        };

        var probe = new RtssDiscovery(environment).Probe();

        Assert.Equal(RtssAvailability.Incompatible, probe.Availability);
    }

    [Fact]
    public void UnprotectedRegistrationPath_IsIncompatible()
    {
        var environment = FakeDiscoveryEnvironment.Valid();
        environment.Records =
        [
            environment.Records[0] with
            {
                InstallLocation = @"C:\Users\player\AppData\Local\RTSS",
                UninstallString = null,
                DisplayIcon = null
            }
        ];

        var probe = new RtssDiscovery(environment).Probe();

        Assert.Equal(RtssAvailability.Incompatible, probe.Availability);
    }

    private sealed class FakeDiscoveryEnvironment : IRtssDiscoveryEnvironment
    {
        private const string InstallRoot = @"C:\Program Files (x86)\RivaTuner Statistics Server";

        public IReadOnlyList<RtssInstallRecord> Records { get; set; } = [];

        private RtssFileIdentity ExecutableIdentity { get; } = new(
            true,
            500_000,
            "RTSS",
            "7.3.5.28314",
            false,
            new HashSet<string>(StringComparer.Ordinal));

        public RtssFileIdentity ApiIdentity { get; set; } = new(
            true,
            1_400_000,
            null,
            null,
            Environment.Is64BitProcess,
            new HashSet<string>(StringComparer.Ordinal)
            {
                "LoadProfile",
                "SaveProfile",
                "GetProfileProperty",
                "SetProfileProperty",
                "UpdateProfiles"
            });

        public IReadOnlyList<RtssProcessIdentity> Processes { get; set; } = [];

        public IReadOnlyList<string> ProtectedInstallRoots { get; } =
        [
            @"C:\Program Files",
            @"C:\Program Files (x86)"
        ];

        public IReadOnlyList<RtssInstallRecord> ReadInstallRecords() => Records;

        public RtssFileIdentity ReadFileIdentity(string path) => path.EndsWith(
            Environment.Is64BitProcess ? "RTSSHooks64.dll" : "RTSSHooks.dll",
            StringComparison.OrdinalIgnoreCase)
            ? ApiIdentity
            : ExecutableIdentity;

        public IReadOnlyList<RtssProcessIdentity> ReadProcesses() => Processes;

        public static FakeDiscoveryEnvironment Valid() => new()
        {
            Records =
            [
                new RtssInstallRecord(
                    "RivaTuner Statistics Server 7.3.7",
                    "7.3.7",
                    "Unwinder",
                    string.Empty,
                    $"\"{InstallRoot}\\uninstall.exe\"",
                    $"\"{InstallRoot}\\uninstall.exe\"")
            ],
            Processes =
            [
                new RtssProcessIdentity(
                    321,
                    $"{InstallRoot}\\RTSS.exe",
                    DateTimeOffset.UnixEpoch)
            ]
        };
    }
}
