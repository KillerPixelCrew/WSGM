using WSGM.Device.Contracts.Input;
using WSGM.Input;
using WSGM.Shell;

namespace WSGM.Tests;

public sealed class ControllerDependencyAdapterTests
{
    [Fact]
    public async Task PinnedHidMaestroFailsClosedWithoutAdvertisingTargets()
    {
        await using HidMaestroProductionBackend backend = new();

        HidBackendHealth health = await backend.DiscoverAsync(CancellationToken.None);
        IReadOnlyDictionary<string, string> diagnostics = await backend.GetDiagnosticsAsync(
            CancellationToken.None);

        Assert.Equal(HidBackendHealthState.Incompatible, health.State);
        Assert.Null(health.Capabilities);
        Assert.Equal("P0-020", diagnostics["policy"]);
        Assert.Equal(bool.FalseString, diagnostics["controllerManagementApproved"]);
        Assert.Contains("four-rear-control", health.Detail);
    }

    [Fact]
    public async Task PinnedHidMaestroRefusesTargetCreationWhileReleaseGateIsClosed()
    {
        await using HidMaestroProductionBackend backend = new();
        CanonicalControllerSample neutral = CanonicalControllerSample.Neutral(
            0,
            1,
            DateTimeOffset.UnixEpoch);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            backend.CreateTargetAsync(
                VirtualTargetKind.Xbox360,
                neutral,
                CancellationToken.None));

        Assert.Contains("P0-020", error.Message);
    }

    [Fact]
    public async Task HidHideAdapterPreservesExactOrderAndVerifiesReadback()
    {
        FakeHidHideControl control = new(
            applications: ["external-b.exe", "external-a.exe"],
            devices: ["HID\\B", "HID\\A"]);
        WindowsHidHideAdapter adapter = new(control);
        HidHideExactSnapshot expected = await adapter.ReadAsync(CancellationToken.None);

        HidHideMutationResult result = await adapter.TryMutateAsync(
            expected,
            new(HidHideMutationKind.Add, HidHideEntryKind.Application, "ControllerHost.exe"),
            CancellationToken.None);

        Assert.True(result.Applied);
        Assert.Equal(
            ["external-b.exe", "external-a.exe", "ControllerHost.exe"],
            result.Current.Applications);
        Assert.Equal(["HID\\B", "HID\\A"], result.Current.Devices);
        Assert.Equal(1, control.WriteCount);
    }

    [Fact]
    public async Task HidHideAdapterRefusesMutationAfterExternalExactStateChange()
    {
        FakeHidHideControl control = new(applications: ["external.exe"]);
        WindowsHidHideAdapter adapter = new(control);
        HidHideExactSnapshot expected = await adapter.ReadAsync(CancellationToken.None);
        control.ReplaceApplications(["new-external.exe", "external.exe"]);

        HidHideMutationResult result = await adapter.TryMutateAsync(
            expected,
            new(HidHideMutationKind.Add, HidHideEntryKind.Device, "HID\\OWN"),
            CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Equal(0, control.WriteCount);
        Assert.Equal(["new-external.exe", "external.exe"], result.Current.Applications);
    }

    [Fact]
    public async Task HidHideInverseModeIsIncompatibleAndNeverWritten()
    {
        FakeHidHideControl control = new(inverse: true);
        WindowsHidHideAdapter adapter = new(control);
        HidHideExactSnapshot expected = await adapter.ReadAsync(CancellationToken.None);

        HidHideMutationResult result = await adapter.TryMutateAsync(
            expected,
            new(HidHideMutationKind.Add, HidHideEntryKind.Device, "HID\\OWN"),
            CancellationToken.None);

        Assert.Equal(HidHideHealthState.Incompatible, expected.Health);
        Assert.False(result.Applied);
        Assert.Equal(0, control.WriteCount);
    }

    [Fact]
    public async Task MissingHidHideControlDeviceIsCapabilityUnavailable()
    {
        FakeHidHideControl control = new(error: 2);
        WindowsHidHideAdapter adapter = new(control);

        HidHideExactSnapshot snapshot = await adapter.ReadAsync(CancellationToken.None);

        Assert.Equal(HidHideHealthState.Unavailable, snapshot.Health);
        Assert.False(snapshot.Active);
        Assert.Empty(snapshot.Applications);
        Assert.Empty(snapshot.Devices);
    }

    private sealed class FakeHidHideControl : IHidHideControl
    {
        private List<string> _applications;
        private List<string> _devices;
        private readonly int _error;

        internal FakeHidHideControl(
            IEnumerable<string>? applications = null,
            IEnumerable<string>? devices = null,
            bool active = true,
            bool inverse = false,
            int error = 0)
        {
            _applications = applications?.ToList() ?? [];
            _devices = devices?.ToList() ?? [];
            Active = active;
            Inverse = inverse;
            _error = error;
        }

        internal bool Active { get; set; }

        internal bool Inverse { get; set; }

        internal int WriteCount { get; private set; }

        public HidHideControlState Read() => _error == 0
            ? new(true, 0, Active, Inverse, _applications.ToArray(), _devices.ToArray())
            : new(false, _error, false, false, [], []);

        public int Write(HidHideEntryKind entryKind, IReadOnlyList<string> entries)
        {
            WriteCount++;
            if (entryKind is HidHideEntryKind.Application)
            {
                _applications = entries.ToList();
            }
            else
            {
                _devices = entries.ToList();
            }

            return 0;
        }

        internal void ReplaceApplications(IEnumerable<string> entries) =>
            _applications = entries.ToList();
    }
}
