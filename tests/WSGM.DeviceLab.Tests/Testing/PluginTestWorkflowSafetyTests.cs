using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using WSGM.Device.Sdk;
using WSGM.Device.Sdk.Identity;
using WSGM.Device.Sdk.Packaging;
using WSGM.Device.Sdk.Plugin;
using WSGM.DeviceLab.Application;
using WSGM.DeviceLab.Packaging;
using WSGM.DeviceLab.Preflight;
using WSGM.DeviceLab.Probes;
using WSGM.DeviceLab.Testing;

namespace WSGM.Device.Tests;

public sealed class PluginTestWorkflowSafetyTests
{
    private static string WorkerExecutablePath()
    {
        var repositoryRoot = Assert.IsType<string>(
            DeviceLabRepositoryLocator.Find(AppContext.BaseDirectory));
        return Path.Combine(
            repositoryRoot,
            "src",
            "WSGM.DeviceLab",
            "bin",
            typeof(PluginTestWorkflowSafetyTests).Assembly
                .GetCustomAttributes<AssemblyConfigurationAttribute>().Single().Configuration,
            "net10.0-windows",
            "win-x64",
            "wsgm-device.exe");
    }

    [Fact]
    public async Task Detection_RunsThroughTheAuthorizedDisposableWorker()
    {
        using TemporaryDirectory temporary = new();
        var package = CreatePackage(
            temporary,
            OwnerReservationLifetimePlugin.Id,
            typeof(OwnerReservationLifetimePlugin).FullName!);

        var executablePath = WorkerExecutablePath();
        var report = await PluginTestWorkerSupervisor.TestDetectionAsync(
            package,
            new DeviceIdentitySnapshot(),
            executablePath,
            CancellationToken.None);

        Assert.True(report.Passed, report.Error);
        Assert.False(report.Detection!.Matched);
        Assert.True(File.Exists(Path.Combine(package, OwnerReservationLifetimePlugin.DisposalMarker)));
    }

    [Fact]
    public async Task Detection_HardDeadlineKillsAnUncooperativePluginProcess()
    {
        using TemporaryDirectory temporary = new();
        var package = CreatePackage(
            temporary,
            HangingDetectionPlugin.Id,
            typeof(HangingDetectionPlugin).FullName!);
        var executablePath = WorkerExecutablePath();
        var elapsed = Stopwatch.StartNew();
        var descendantMarker = Path.Combine(package, HangingDetectionPlugin.DescendantMarker);

        int? descendantPid = null;
        try
        {
            var report = await PluginTestWorkerSupervisor.TestDetectionAsync(
                package,
                new DeviceIdentitySnapshot(),
                executablePath,
                TimeSpan.FromSeconds(1),
                CancellationToken.None);

            Assert.False(report.Passed);
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(8), elapsed.Elapsed.ToString());
            Assert.Contains("deadline", report.Error, StringComparison.OrdinalIgnoreCase);
            descendantPid = int.Parse(
                File.ReadAllText(descendantMarker),
                CultureInfo.InvariantCulture);
            Assert.True(
                SpinWait.SpinUntil(() => !IsProcessRunning(descendantPid.Value), TimeSpan.FromSeconds(3)),
                "The worker job did not terminate the plugin's descendant process.");
        }
        finally
        {
            if (descendantPid is { } pid && IsProcessRunning(pid))
            {
                using var descendant = Process.GetProcessById(pid);
                descendant.Kill(entireProcessTree: true);
                descendant.WaitForExit(2_000);
            }
        }
    }

    [Fact]
    public void HiddenWorkersRejectDirectInvocationWithoutInheritedAuthorization()
    {
        Assert.Equal(64, PluginTestWorker.Run([]));
        Assert.Equal(64, ReadProbeWorker.Run([]));
    }

    [Fact]
    public void WorkerSessionFilesMustUseExactNamesInOneNonLinkedDirectory()
    {
        using TemporaryDirectory temporary = new();
        var session = temporary.GetPath("session");
        var escaped = temporary.GetPath("other");
        Directory.CreateDirectory(session);
        Directory.CreateDirectory(escaped);
        var request = Path.Combine(session, "probe-request.json");
        File.WriteAllText(request, "{}");

        var accepted = SelfWorkerAuthorization.TryConstrainSessionFiles(
            request,
            Path.Combine(session, "probe-result.json"),
            "probe-request.json",
            "probe-result.json",
            out var constrainedRequest,
            out var constrainedResult);
        var escapedResult = SelfWorkerAuthorization.TryConstrainSessionFiles(
            request,
            Path.Combine(escaped, "probe-result.json"),
            "probe-request.json",
            "probe-result.json",
            out _,
            out _);

        Assert.True(accepted);
        Assert.Equal(Path.GetFullPath(request), constrainedRequest);
        Assert.Equal(Path.Combine(Path.GetFullPath(session), "probe-result.json"), constrainedResult);
        Assert.False(escapedResult);
    }

    [Fact]
    public async Task RunAttended_UnconfirmedActionRefusesBeforeLoadingTheDeclaredAssembly()
    {
        using TemporaryDirectory temporary = new();
        var package = CreatePackageWithUnresolvableEntryType(temporary);
        var stateDirectory = temporary.GetPath("new-state");
        var reservationAttempts = 0;

        var report = await PluginTestWorkflow.RunAttendedAsync(
            package,
            new DeviceIdentitySnapshot(),
            stateDirectory,
            Action(),
            confirmed: false,
            DeviceLabPackages.Boundaries(temporary),
            SafetyEnvironment(() =>
            {
                reservationAttempts++;
                return Reserved(new CallbackDisposable(static () => { }));
            }),
            CancellationToken.None);

        Assert.False(report.Passed);
        Assert.Null(report.Detection);
        Assert.False(report.Started);
        Assert.Contains(report.Preflight!.Checks, check => check.Code == "attended.confirmation");
        Assert.Contains("before plugin loading", report.Error, StringComparison.Ordinal);
        Assert.False(Directory.Exists(stateDirectory));
        Assert.Equal(0, reservationAttempts);
    }

    [Fact]
    public async Task RunAttended_ExistingStateDirectoryRefusesBeforeLoadingTheDeclaredAssembly()
    {
        using TemporaryDirectory temporary = new();
        var package = CreatePackageWithUnresolvableEntryType(temporary);
        var stateDirectory = temporary.GetPath("existing-state");
        Directory.CreateDirectory(stateDirectory);
        var reservationAttempts = 0;

        var report = await PluginTestWorkflow.RunAttendedAsync(
            package,
            new DeviceIdentitySnapshot(),
            stateDirectory,
            Action(),
            confirmed: true,
            DeviceLabPackages.Boundaries(temporary),
            SafetyEnvironment(() =>
            {
                reservationAttempts++;
                return Reserved(new CallbackDisposable(static () => { }));
            }),
            CancellationToken.None);

        Assert.False(report.Passed);
        Assert.Null(report.Detection);
        Assert.False(report.Started);
        Assert.Equal("The plugin state directory must be new.", report.Error);
        Assert.Equal(0, reservationAttempts);
    }

    [Fact]
    public async Task RunAttended_ExistingProductionOwnerRefusesBeforeLoadingTheDeclaredAssembly()
    {
        using TemporaryDirectory temporary = new();
        var package = CreatePackageWithUnresolvableEntryType(temporary);
        var stateDirectory = temporary.GetPath("new-state");
        var reservationAttempts = 0;

        var report = await PluginTestWorkflow.RunAttendedAsync(
            package,
            new DeviceIdentitySnapshot(),
            stateDirectory,
            Action(),
            confirmed: true,
            DeviceLabPackages.Boundaries(temporary),
            SafetyEnvironment(() =>
            {
                reservationAttempts++;
                return OwnerPresent();
            }),
            CancellationToken.None);

        Assert.Equal(1, reservationAttempts);
        Assert.False(report.Passed);
        Assert.Null(report.Detection);
        Assert.False(report.Started);
        Assert.Contains(report.Preflight!.Checks, check => check.Code == "owner.active");
        Assert.Contains("before plugin loading", report.Error, StringComparison.Ordinal);
        Assert.False(Directory.Exists(stateDirectory));
    }

    [Fact]
    public async Task RunAttended_OwnerReservationOutlivesPluginDisposal()
    {
        using TemporaryDirectory temporary = new();
        var package = CreatePackage(
            temporary,
            OwnerReservationLifetimePlugin.Id,
            typeof(OwnerReservationLifetimePlugin).FullName!);
        var marker = Path.Combine(package, OwnerReservationLifetimePlugin.DisposalMarker);
        var stateDirectory = temporary.GetPath("new-state");
        var reservationDisposed = false;
        var handle = new CallbackDisposable(() =>
        {
            Assert.True(
                File.Exists(marker),
                "LocalPluginPackage must dispose the plugin before the owner reservation closes.");
            reservationDisposed = true;
        });

        var report = await PluginTestWorkflow.RunAttendedAsync(
            package,
            new DeviceIdentitySnapshot(),
            stateDirectory,
            Action(),
            confirmed: true,
            DeviceLabPackages.Boundaries(temporary),
            SafetyEnvironment(() => Reserved(handle)),
            CancellationToken.None);

        Assert.False(report.Passed);
        var detection = Assert.IsType<PluginDetectionResult>(report.Detection);
        Assert.False(detection.Matched);
        Assert.False(report.Started);
        Assert.Contains(report.Preflight!.Checks, check => check.Code == "identity.mismatch");
        Assert.True(File.Exists(marker));
        Assert.True(reservationDisposed);
        Assert.False(Directory.Exists(stateDirectory));
    }

    [Fact]
    public async Task LocalPluginPackage_ThrowingDisposalUnloadsAndRetainsOwnerForProcessLifetime()
    {
        var ownerName = $@"Local\WSGM.DeviceOwner.DisposeFailure.{Guid.NewGuid():N}";
        var owner = DeviceLabOwnerInspector.Reserve(ownerName);
        Assert.Equal(DeviceOwnerDiscoveryState.Absent, owner.Inspection.State);
        var reservation =
            Assert.IsType<DeviceLabOwnerReservation>(owner.Reservation);
        using (reservation)
        {
            var disposalFailure = new InvalidOperationException("plugin disposal failed");
            var unloaded = false;

            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                LocalPluginPackage.DisposePluginAndUnloadAsync(
                    () => ValueTask.FromException(disposalFailure),
                    () => unloaded = true,
                    reservation).AsTask());

            Assert.Same(disposalFailure, thrown);
            Assert.True(unloaded);
        }

        var competing = DeviceLabOwnerInspector.Reserve(ownerName);
        Assert.Equal(DeviceOwnerDiscoveryState.Present, competing.Inspection.State);
        Assert.Null(competing.Reservation);
    }

    [Fact]
    public async Task RunAttended_ThrowingPluginDisposalKeepsTheAtomicOwnerReservationUnavailable()
    {
        using TemporaryDirectory temporary = new();
        var package = CreatePackage(
            temporary,
            ThrowingDisposePlugin.Id,
            typeof(ThrowingDisposePlugin).FullName!);
        var ownerName = $@"Local\WSGM.DeviceOwner.WorkflowDisposeFailure.{Guid.NewGuid():N}";

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PluginTestWorkflow.RunAttendedAsync(
                package,
                new DeviceIdentitySnapshot(),
                temporary.GetPath("new-state"),
                Action(),
                confirmed: true,
                DeviceLabPackages.Boundaries(temporary),
                SafetyEnvironment(() => DeviceLabOwnerInspector.Reserve(ownerName)),
                CancellationToken.None));

        Assert.Contains("plugin disposal failed", failure.Message, StringComparison.Ordinal);
        var competing = DeviceLabOwnerInspector.Reserve(ownerName);
        Assert.Equal(DeviceOwnerDiscoveryState.Present, competing.Inspection.State);
        Assert.Null(competing.Reservation);
    }

    [Fact]
    public async Task RunAttended_ThrowingPluginConstructorKeepsTheAtomicOwnerReservationUnavailable()
    {
        using TemporaryDirectory temporary = new();
        var package = CreatePackage(
            temporary,
            ThrowingConstructorPlugin.Id,
            typeof(ThrowingConstructorPlugin).FullName!);
        var ownerName = $@"Local\WSGM.DeviceOwner.ConstructorFailure.{Guid.NewGuid():N}";

        var failure = await CaptureFailureAsync(() => PluginTestWorkflow.RunAttendedAsync(
            package,
            new DeviceIdentitySnapshot(),
            temporary.GetPath("new-state"),
            Action(),
            confirmed: true,
            DeviceLabPackages.Boundaries(temporary),
            SafetyEnvironment(() => DeviceLabOwnerInspector.Reserve(ownerName)),
            CancellationToken.None));
        Assert.NotNull(failure);
        CollectPluginLoadContexts();

        Assert.True(File.Exists(Path.Combine(package, ThrowingConstructorPlugin.ConstructorMarker)));
        var competing = DeviceLabOwnerInspector.Reserve(ownerName);
        Assert.Equal(DeviceOwnerDiscoveryState.Present, competing.Inspection.State);
        Assert.Null(competing.Reservation);
    }

    [Fact]
    public async Task RunAttended_ThrowingPackageIdAndDisposalKeepsOwnerReservationUnavailable()
    {
        using TemporaryDirectory temporary = new();
        var package = CreatePackage(
            temporary,
            ThrowingPackageIdPlugin.Id,
            typeof(ThrowingPackageIdPlugin).FullName!);
        var ownerName = $@"Local\WSGM.DeviceOwner.PackageIdFailure.{Guid.NewGuid():N}";

        var failure = await CaptureFailureAsync(() => PluginTestWorkflow.RunAttendedAsync(
            package,
            new DeviceIdentitySnapshot(),
            temporary.GetPath("new-state"),
            Action(),
            confirmed: true,
            DeviceLabPackages.Boundaries(temporary),
            SafetyEnvironment(() => DeviceLabOwnerInspector.Reserve(ownerName)),
            CancellationToken.None));
        Assert.NotNull(failure);
        CollectPluginLoadContexts();

        Assert.True(File.Exists(Path.Combine(package, ThrowingPackageIdPlugin.DisposalMarker)));
        var competing = DeviceLabOwnerInspector.Reserve(ownerName);
        Assert.Equal(DeviceOwnerDiscoveryState.Present, competing.Inspection.State);
        Assert.Null(competing.Reservation);
    }

    [Fact]
    public async Task RunAttended_ThrowingPackageIdWithCleanDisposalKeepsOwnerReservationUnavailable()
    {
        using TemporaryDirectory temporary = new();
        var package = CreatePackage(
            temporary,
            ThrowingPackageIdCleanDisposePlugin.Id,
            typeof(ThrowingPackageIdCleanDisposePlugin).FullName!);
        var ownerName = $@"Local\WSGM.DeviceOwner.CleanDisposePackageIdFailure.{Guid.NewGuid():N}";

        var failure = await CaptureFailureAsync(() => PluginTestWorkflow.RunAttendedAsync(
            package,
            new DeviceIdentitySnapshot(),
            temporary.GetPath("new-state"),
            Action(),
            confirmed: true,
            DeviceLabPackages.Boundaries(temporary),
            SafetyEnvironment(() => DeviceLabOwnerInspector.Reserve(ownerName)),
            CancellationToken.None));
        Assert.NotNull(failure);
        CollectPluginLoadContexts();

        Assert.True(File.Exists(Path.Combine(package, OwnerReservationLifetimePlugin.DisposalMarker)));
        var competing = DeviceLabOwnerInspector.Reserve(ownerName);
        Assert.Equal(DeviceOwnerDiscoveryState.Present, competing.Inspection.State);
        Assert.Null(competing.Reservation);
    }

    [Theory]
    [InlineData(
        UnverifiedStopPlugin.Id,
        typeof(UnverifiedStopPlugin),
        "synthetic restoration was unverified")]
    [InlineData(
        FailedStopPlugin.Id,
        typeof(FailedStopPlugin),
        "synthetic restoration failed")]
    [InlineData(
        ThrowingStopPlugin.Id,
        typeof(ThrowingStopPlugin),
        "synthetic Stop threw")]

    public async Task RunAttended_PostStartUnverifiedOutcomeWithCleanDisposalKeepsOwnerUnavailable(
        string packageId,
        Type pluginType,
        string expectedError)
    {
        using TemporaryDirectory temporary = new();
        var package = CreatePackage(
            temporary,
            packageId,
            pluginType.FullName!);
        var ownerName = $@"Local\WSGM.DeviceOwner.UnverifiedAttendedStop.{Guid.NewGuid():N}";

        var report = await PluginTestWorkflow.RunAttendedAsync(
            package,
            new DeviceIdentitySnapshot(),
            temporary.GetPath("new-state"),
            Action(),
            confirmed: true,
            DeviceLabPackages.Boundaries(temporary),
            SafetyEnvironment(() => DeviceLabOwnerInspector.Reserve(ownerName)),
            CancellationToken.None);

        Assert.True(report.Started);
        Assert.False(report.CleanedUp);
        Assert.Contains(expectedError, report.Error, StringComparison.Ordinal);
        var competing = DeviceLabOwnerInspector.Reserve(ownerName);
        Assert.Equal(DeviceOwnerDiscoveryState.Present, competing.Inspection.State);
        Assert.Null(competing.Reservation);
    }

    [Fact]
    public async Task OwnerReservation_UsesAtomicNamedObjectLifetimeWithoutThreadOwnership()
    {
        var ownerName = $@"Local\WSGM.DeviceOwner.Test.{Guid.NewGuid():N}";
        var first = DeviceLabOwnerInspector.Reserve(ownerName);
        Assert.Equal(DeviceOwnerDiscoveryState.Absent, first.Inspection.State);
        var firstReservation = Assert.IsType<DeviceLabOwnerReservation>(first.Reservation);
        try
        {
            var concurrent = DeviceLabOwnerInspector.Reserve(ownerName);
            Assert.Equal(DeviceOwnerDiscoveryState.Present, concurrent.Inspection.State);
            Assert.Null(concurrent.Reservation);

            await Task.Run(firstReservation.Dispose);
            var afterRelease = DeviceLabOwnerInspector.Reserve(ownerName);
            Assert.Equal(DeviceOwnerDiscoveryState.Absent, afterRelease.Inspection.State);
            var afterReleaseReservation =
                Assert.IsType<DeviceLabOwnerReservation>(afterRelease.Reservation);
            afterReleaseReservation.Dispose();
        }
        finally
        {
            firstReservation.Dispose();
        }
    }

    [Fact]
    public void OwnerReservation_UsesTheExactMachineWideProductionMarker()
    {
        Assert.Equal(@"Global\WSGM.DeviceOwner", DeviceLabOwnerInspector.OwnerObjectName());
    }

    private static AttendedPluginSafetyEnvironment SafetyEnvironment(
        Func<DeviceLabOwnerReservationResult> reserveOwner) => new()
        {
            ReserveOwner = reserveOwner,
            IsElevated = true,
            IsUserInteractive = true,
            IsContinuousIntegration = false
        };

    private static DeviceLabOwnerReservationResult Reserved(IDisposable handle) => new()
    {
        Inspection = new DeviceLabOwnerInspection
        {
            State = DeviceOwnerDiscoveryState.Absent
        },
        Reservation = new DeviceLabOwnerReservation(handle)
    };

    private static DeviceLabOwnerReservationResult OwnerPresent() => new()
    {
        Inspection = new DeviceLabOwnerInspection
        {
            State = DeviceOwnerDiscoveryState.Present
        }
    };

    private sealed class CallbackDisposable(Action callback) : IDisposable
    {
        private Action? _callback = callback;

        public void Dispose()
        {
            var callback = Interlocked.Exchange(ref _callback, null);
            callback?.Invoke();
        }
    }

    private static string CreatePackageWithUnresolvableEntryType(TemporaryDirectory temporary)
        => CreatePackage(
            temporary,
            "wsgm.device.synthetic.preflight-order",
            "WSGM.Device.Tests.ThisTypeMustNeverBeResolved");

    private static string CreatePackage(
        TemporaryDirectory temporary,
        string packageId,
        string entryType)
    {
        var package = temporary.GetPath("package");
        Directory.CreateDirectory(package);
        var assemblyName = Path.GetFileName(typeof(PluginTestWorkflowSafetyTests).Assembly.Location);
        File.Copy(
            typeof(PluginTestWorkflowSafetyTests).Assembly.Location,
            Path.Combine(package, assemblyName));
        var manifest = new PluginManifest
        {
            Id = packageId,
            Name = "Synthetic Preflight Order",
            Version = "1.0.0",
            ApiVersion = DeviceApi.Version,
            EntryAssembly = assemblyName,
            EntryType = entryType
        };
        File.WriteAllBytes(
            Path.Combine(package, PluginPackageWorkflow.ManifestPath),
            PluginManifestFixture.Serialize(manifest));
        return package;
    }

    private static AttendedPluginActionRequest Action() => new()
    {
        Kind = AttendedPluginActionKind.ControllerManagement
    };

    private static async Task<string?> CaptureFailureAsync(Func<Task> operation)
    {
        try
        {
            await operation();
            return null;
        }
        catch (Exception exception)
        {
            return exception.ToString();
        }
    }

    private static void CollectPluginLoadContexts()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
