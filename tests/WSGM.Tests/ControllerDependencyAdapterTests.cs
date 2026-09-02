using WSGM.Core;
using WSGM.Input;
using WSGM.Shell;

namespace WSGM.Tests;

public sealed class ControllerDependencyAdapterTests
{
    [Fact]
    public void ProductionBackendAdvertisesExactlyTheTargetsWithWireEncoders()
    {
        Assert.Equal(
            [
                ManagedControllerTarget.SteamDeckComposite,
                ManagedControllerTarget.Xbox360,
                ManagedControllerTarget.DualShock4,
            ],
            ViiperControllerBackend.SupportedTargets);
    }

    [Theory]
    [InlineData(ManagedControllerTarget.Xbox360, new byte[] { 128, 255 }, 128 / 255f, 1f)]
    [InlineData(
        ManagedControllerTarget.DualShock4,
        new byte[] { 64, 192, 0, 0, 0, 0, 0 },
        192 / 255f,
        64 / 255f)]
    public void TargetFeedbackUsesTheCorrectMotorOrder(
        ManagedControllerTarget target,
        byte[] report,
        float expectedLow,
        float expectedHigh)
    {
        DecodedHapticFeedback feedback = Assert.IsType<DecodedHapticFeedback>(
            ViiperControllerBackend.DecodeFeedback(target, report));

        Assert.Equal(expectedLow, feedback.LowFrequency, 5);
        Assert.Equal(expectedHigh, feedback.HighFrequency, 5);
        Assert.Null(feedback.StopAfter);
    }

    [Fact]
    public void SteamDeckFeedbackKeepsItsSixteenBitMotorScale()
    {
        byte[] report = [0xEB, 0, 0, 0, 0, 0, 0x80, 0xFF, 0xFF];

        DecodedHapticFeedback feedback = Assert.IsType<DecodedHapticFeedback>(
            ViiperControllerBackend.DecodeFeedback(
                ManagedControllerTarget.SteamDeckComposite,
                report));

        Assert.Equal(32768 / (float)ushort.MaxValue, feedback.LowFrequency, 5);
        Assert.Equal(1f, feedback.HighFrequency);
        Assert.Null(feedback.StopAfter);
    }

    [Theory]
    [InlineData(64, 0, 64 / 255f)]
    [InlineData(64, 4, 96 / 255f)]
    [InlineData(64, -16, 0f)]
    public void SteamDeckContinuousHapticBecomesSymmetricPhysicalOutput(
        byte intensity,
        sbyte gain,
        float expected)
    {
        byte[] report = [0xEA, 0, 0, 0, intensity, unchecked((byte)gain)];

        DecodedHapticFeedback feedback = Assert.IsType<DecodedHapticFeedback>(
            ViiperControllerBackend.DecodeFeedback(
                ManagedControllerTarget.SteamDeckComposite,
                report));

        Assert.Equal(expected, feedback.LowFrequency, 5);
        Assert.Equal(expected, feedback.HighFrequency, 5);
        Assert.Null(feedback.StopAfter);
    }

    [Fact]
    public void SteamDeckHapticPulseCarriesBoundedMotorStopTime()
    {
        byte[] report = [0x8F, 0, 0, 0, 0, 0xE8, 0x03, 2, 0, 3];

        DecodedHapticFeedback feedback = Assert.IsType<DecodedHapticFeedback>(
            ViiperControllerBackend.DecodeFeedback(
                ManagedControllerTarget.SteamDeckComposite,
                report));

        Assert.Equal(35 / 255f, feedback.LowFrequency, 5);
        Assert.Equal(feedback.LowFrequency, feedback.HighFrequency);
        Assert.Equal(TimeSpan.FromMilliseconds(2), feedback.StopAfter);
    }

    [Fact]
    public void SafeNativeRunsTheCallExactlyOnce()
    {
        // Regression: a self-forwarding overload once made this call recurse until the stack was
        // exhausted, which killed every live target replacement and shutdown.
        int calls = 0;

        ViiperControllerBackend.SafeNative(() => ++calls, "count");

        Assert.Equal(1, calls);
    }

    [Fact]
    public void SafeNativeSwallowsOnlyNativeBindingFailures()
    {
        ViiperControllerBackend.SafeNative(
            () => throw new System.Runtime.InteropServices.SEHException(),
            "fail natively");

        Assert.Throws<InvalidOperationException>(() => ViiperControllerBackend.SafeNative(
            () => throw new InvalidOperationException("managed"),
            "fail in managed code"));
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
