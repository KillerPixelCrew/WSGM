using System.Runtime.CompilerServices;
using WSGM.Device.Sdk;
using WSGM.Device.Sdk.Ipc;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Device.Sdk.Packaging;
using WSGM.DeviceHost;

namespace WSGM.Device.Tests;

public sealed class DeviceHostTests
{
    [Fact]
    public void HostArguments_ExactOwnerLaunch_ParsesAndUnknownOptionFailsClosed()
    {
        byte[] nonce = Enumerable.Range(0, ControlEndpoint.NonceBytes)
            .Select(index => (byte)index)
            .ToArray();
        string[] valid =
        [
            "--package", ".",
            "--package-id", "wsgm.device.synthetic.dock-x1",
            "--pipe", "wsgm-device-test-pipe",
            "--nonce", Convert.ToBase64String(nonce),
            "--session", "17",
            "--generation", "9",
        ];

        Assert.True(HostArguments.TryParse(valid, out HostArguments? parsed, out string error), error);
        Assert.NotNull(parsed);
        Assert.Equal(9, parsed.CycleGeneration);
        Assert.Equal(nonce, parsed.Nonce);

        string[] unknown = [.. valid, "--raw-command", "anything"];
        Assert.False(HostArguments.TryParse(unknown, out _, out _));
    }

    [Fact]
    public void HandshakeAck_OnlyExactEnvelopeApiAndPackage_AreAccepted()
    {
        DeviceHostHelloAck acknowledgment = new()
        {
            Accepted = true,
            PackageId = "wsgm.device.synthetic.dock-x1",
        };
        FrameHeader exact = AckHeader();

        Assert.True(HostHandshakeValidator.TryValidateAck(
            exact,
            acknowledgment,
            expectedRequestId: 1,
            expectedPackageId: acknowledgment.PackageId,
            out string detail), detail);
        Assert.False(HostHandshakeValidator.TryValidateAck(
            exact with { ProtocolVersion = DeviceProtocol.Version + 1 },
            acknowledgment,
            1,
            acknowledgment.PackageId,
            out _));
        Assert.False(HostHandshakeValidator.TryValidateAck(
            exact,
            acknowledgment with { PackageId = "wsgm.device.other" },
            1,
            "wsgm.device.synthetic.dock-x1",
            out _));
        Assert.False(HostHandshakeValidator.IsExpectedAckEnvelope(
            exact with { Flags = FrameFlags.None },
            1,
            out _));
    }

    [Fact]
    public void ReadMetadata_ValidPackage_IsConstrainedWithoutLoadingPluginCode()
    {
        using TemporaryDirectory temporary = new();
        string package = temporary.GetPath("package");
        Directory.CreateDirectory(package);
        File.WriteAllBytes(Path.Combine(package, "Synthetic.Dock.dll"), [0x4d, 0x5a]);
        File.WriteAllBytes(
            Path.Combine(package, "plugin.wsgm.json"),
            SdkManifestTests.Serialize(SdkManifestTests.Manifest()));

        PluginPackageMetadata metadata = PluginPackageLoader.ReadMetadata(
            package,
            "wsgm.device.synthetic.dock-x1");

        Assert.Equal(Path.Combine(package, "Synthetic.Dock.dll"), metadata.EntryPath);
        Assert.Equal(DeviceApi.Version, metadata.Manifest.ApiVersion);
        Assert.Throws<InvalidDataException>(() => PluginPackageLoader.ReadMetadata(
            package,
            "wsgm.device.other"));
    }

    [Fact]
    public void ReadMetadata_OversizedManifestIsRejectedByTheBoundedFileRead()
    {
        using TemporaryDirectory temporary = new();
        string package = temporary.GetPath("package");
        Directory.CreateDirectory(package);
        File.WriteAllBytes(
            Path.Combine(package, "plugin.wsgm.json"),
            new byte[ManifestLimits.MaxDocumentBytes + 1]);

        Assert.Throws<InvalidDataException>(() => PluginPackageLoader.ReadMetadata(
            package,
            "wsgm.device.synthetic.dock-x1"));
    }

    [Fact]
    public void LoadPlugin_ThrowingPackagePropertyStillDisposesTheInstanceAndPreservesBothFailures()
    {
        using TemporaryDirectory temporary = new();
        string package = temporary.GetPath("package");
        Directory.CreateDirectory(package);
        string entryAssembly = Path.GetFileName(typeof(DeviceHostTests).Assembly.Location);
        File.Copy(
            typeof(DeviceHostTests).Assembly.Location,
            Path.Combine(package, entryAssembly));
        var manifest = SdkManifestTests.Manifest() with
        {
            Id = ThrowingPackageIdPlugin.Id,
            EntryAssembly = entryAssembly,
            EntryType = typeof(ThrowingPackageIdPlugin).FullName!,
        };
        File.WriteAllBytes(
            Path.Combine(package, "plugin.wsgm.json"),
            SdkManifestTests.Serialize(manifest));
        PluginPackageMetadata metadata = PluginPackageLoader.ReadMetadata(
            package,
            ThrowingPackageIdPlugin.Id);

        string[] failures = LoadPluginAndCaptureFailures(metadata);

        Assert.Equal(2, failures.Length);
        Assert.Contains(
            failures,
            message => message.Contains("package ID failed", StringComparison.Ordinal));
        Assert.Contains(
            failures,
            message => message.Contains("plugin disposal failed", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(package, ThrowingPackageIdPlugin.DisposalMarker)));
        CollectPluginLoadContexts();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string[] LoadPluginAndCaptureFailures(PluginPackageMetadata metadata)
    {
        AggregateException failure = Assert.Throws<AggregateException>(() =>
            PluginPackageLoader.LoadPlugin(metadata));
        return [.. failure.InnerExceptions.Select(exception => exception.Message)];
    }

    private static void CollectPluginLoadContexts()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    [Theory]
    [InlineData(DeviceCycleState.Activating)]
    [InlineData(DeviceCycleState.Active)]
    [InlineData(DeviceCycleState.Degraded)]
    [InlineData(DeviceCycleState.Suspended)]
    [InlineData(DeviceCycleState.Deactivating)]
    public void CoordinatorDisconnect_AfterPluginStartAttempt_RequiresCleanup(
        DeviceCycleState state)
    {
        Assert.True(DeviceHostSession.NeedsDisconnectCleanup(
            pluginStartAttempted: true,
            state));
        Assert.False(DeviceHostSession.NeedsDisconnectCleanup(
            pluginStartAttempted: false,
            state));
        Assert.False(DeviceHostSession.NeedsDisconnectCleanup(
            pluginStartAttempted: true,
            DeviceCycleState.Disabled));
    }

    [Fact]
    public async Task CoordinatorDisconnect_RechecksForLateStartAfterInFlightDetectionUnwinds()
    {
        using var lifecycle = new SemaphoreSlim(1, 1);
        await lifecycle.WaitAsync();
        bool pluginStartAttempted = false;
        bool cleanupRan = false;
        Task disconnectCleanup = DeviceHostSession.RunDisconnectCleanupAfterLifecycleAsync(
            lifecycle,
            () => pluginStartAttempted,
            _ =>
            {
                cleanupRan = true;
                return Task.CompletedTask;
            },
            CancellationToken.None);
        bool lifecycleReleased = false;
        try
        {
            Assert.False(disconnectCleanup.IsCompleted);
            pluginStartAttempted = true;
            lifecycle.Release();
            lifecycleReleased = true;
            await disconnectCleanup.WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            if (!lifecycleReleased)
            {
                pluginStartAttempted = true;
                lifecycle.Release();
            }
            await disconnectCleanup.WaitAsync(TimeSpan.FromSeconds(1));
        }

        Assert.True(cleanupRan);
    }

    [Fact]
    public async Task TerminalStopOrHandoff_CancelsAndWaitsForBlockedPluginStartBeforeContinuing()
    {
        var gate = new DeviceStartCancellationGate();
        using var lifecycle = new SemaphoreSlim(1, 1);
        using var startCancellation = new CancellationTokenSource();
        var startEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        List<string> order = [];
        Task blockedStart = BlockedStartAsync();
        Task terminal = Task.CompletedTask;

        async Task BlockedStartAsync()
        {
            await lifecycle.WaitAsync();
            try
            {
                using IDisposable registration = gate.Register(startCancellation);
                order.Add("start");
                startEntered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, startCancellation.Token);
                }
                catch (OperationCanceledException) when (startCancellation.IsCancellationRequested)
                {
                }
            }
            finally
            {
                order.Add("start-unwound");
                lifecycle.Release();
            }
        }

        try
        {
            await startEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            terminal = DeviceHostSession.RunTerminalLifecycleAfterStartAsync(
                gate,
                lifecycle,
                _ =>
                {
                    order.Add("terminal");
                    return Task.CompletedTask;
                },
                CancellationToken.None);
            await Task.WhenAll(blockedStart, terminal).WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            startCancellation.Cancel();
            await Task.WhenAll(blockedStart, terminal).WaitAsync(TimeSpan.FromSeconds(1));
        }

        Assert.True(startCancellation.IsCancellationRequested);
        Assert.Equal(["start", "start-unwound", "terminal"], order);
    }

    [Fact]
    public void TerminalStop_BeforeStartRegistrationCancelsTheLateStartImmediately()
    {
        var gate = new DeviceStartCancellationGate();
        gate.RequestTerminalStop();
        using var lateStart = new CancellationTokenSource();

        using IDisposable registration = gate.Register(lateStart);

        Assert.True(lateStart.IsCancellationRequested);
    }

    [Fact]
    public async Task TerminalStop_ThrowingStartCancellationCallbackCannotSkipTerminalAction()
    {
        var gate = new DeviceStartCancellationGate();
        using var lifecycle = new SemaphoreSlim(1, 1);
        using var startCancellation = new CancellationTokenSource();
        using CancellationTokenRegistration throwingCallback = startCancellation.Token.Register(
            static () => throw new InvalidOperationException("plugin callback failed"));
        using IDisposable registration = gate.Register(startCancellation);
        bool terminalRan = false;

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DeviceHostSession.RunTerminalLifecycleAfterStartAsync(
                gate,
                lifecycle,
                _ =>
                {
                    terminalRan = true;
                    return Task.CompletedTask;
                },
                CancellationToken.None));

        Assert.True(terminalRan);
        Assert.IsType<AggregateException>(failure.InnerException);
    }

    [Fact]
    public async Task TerminalCommandQuiescence_ClosesAdmissionCancelsAndWaitsBeforeTerminalAction()
    {
        var commands = new DeviceCommandRegistry();
        Guid commandId = Guid.NewGuid();
        using var command = new DeviceCommandCancellation(
            CancellationToken.None,
            DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.True(commands.TryAdd(commandId, command, out bool admissionClosed));
        Assert.False(admissionClosed);
        bool terminalRan = false;
        Task<IReadOnlyList<Exception>> quiescence = commands.CloseAdmissionAndCancelAsync();
        Task terminal = DeviceHostSession.RunTerminalActionAfterCommandQuiescenceAsync(
            quiescence,
            _ =>
            {
                terminalRan = true;
                return Task.CompletedTask;
            },
            CancellationToken.None);
        try
        {
            Assert.True(command.Token.IsCancellationRequested);
            Assert.False(quiescence.IsCompleted);
            Assert.False(terminalRan);
            using var lateCommand = new DeviceCommandCancellation(
                CancellationToken.None,
                DateTimeOffset.UtcNow.AddMinutes(1));
            Assert.False(commands.TryAdd(Guid.NewGuid(), lateCommand, out admissionClosed));
            Assert.True(admissionClosed);

            command.Complete();
            commands.Remove(commandId);
            await terminal.WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            command.Complete();
            commands.Remove(commandId);
            await terminal.WaitAsync(TimeSpan.FromSeconds(1));
        }

        Assert.True(terminalRan);
        Assert.Empty(await quiescence);
    }

    [Fact]
    public async Task TerminalCommandCancellationFailureCannotSkipTerminalAction()
    {
        var commands = new DeviceCommandRegistry();
        Guid commandId = Guid.NewGuid();
        using var command = new DeviceCommandCancellation(
            CancellationToken.None,
            DateTimeOffset.UtcNow.AddMinutes(1));
        using CancellationTokenRegistration callback = command.Token.Register(
            static () => throw new InvalidOperationException("plugin command callback failed"));
        Assert.True(commands.TryAdd(commandId, command, out _));
        Task<IReadOnlyList<Exception>> quiescence = commands.CloseAdmissionAndCancelAsync();
        command.Complete();
        commands.Remove(commandId);
        bool terminalRan = false;

        AggregateException failure = await Assert.ThrowsAsync<AggregateException>(() =>
            DeviceHostSession.RunTerminalActionAfterCommandQuiescenceAsync(
                quiescence,
                _ =>
                {
                    terminalRan = true;
                    return Task.CompletedTask;
                },
                CancellationToken.None));

        Assert.True(terminalRan);
        Assert.Contains("plugin command callback failed", failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CoordinatorDisconnect_CancellationFailuresCannotSkipPluginStop()
    {
        var sessionFailure = new InvalidOperationException("session callback failed");
        var commandFailure = new InvalidOperationException("command callback failed");
        List<string> order = [];

        AggregateException failure = await Assert.ThrowsAsync<AggregateException>(() =>
            DeviceHostSession.RunDisconnectTeardownAsync(
                () =>
                {
                    order.Add("session-cancel");
                    throw sessionFailure;
                },
                () =>
                {
                    order.Add("command-cancel");
                    throw commandFailure;
                },
                () =>
                {
                    order.Add("wait-operations");
                    return Task.CompletedTask;
                },
                () =>
                {
                    order.Add("plugin-stop");
                    return Task.CompletedTask;
                }));

        Assert.Equal(
            ["session-cancel", "command-cancel", "wait-operations", "plugin-stop"],
            order);
        Assert.Contains(failure.InnerExceptions, exception => exception.InnerException == sessionFailure);
        Assert.Contains(failure.InnerExceptions, exception => exception.InnerException == commandFailure);
    }

    [Fact]
    public async Task CommandCancellation_DisposeRacingAnActiveCallbackCannotTouchADisposedSource()
    {
        using var callbackEntered = new ManualResetEventSlim(initialState: false);
        using var releaseCallback = new ManualResetEventSlim(initialState: false);
        var cancellation = new DeviceCommandCancellation(
            CancellationToken.None,
            DateTimeOffset.UtcNow.AddMinutes(1));
        using CancellationTokenRegistration callback = cancellation.Token.Register(() =>
        {
            callbackEntered.Set();
            releaseCallback.Wait(TimeSpan.FromSeconds(1));
        });
        Task<bool> canceling = Task.Run(cancellation.TryCancel);
        bool canceled = false;
        try
        {
            Assert.True(await Task.Run(() => callbackEntered.Wait(TimeSpan.FromSeconds(1))));
            cancellation.Dispose();
        }
        finally
        {
            releaseCallback.Set();
            canceled = await canceling.WaitAsync(TimeSpan.FromSeconds(1));
            cancellation.Dispose();
        }

        Assert.True(canceled);
        Assert.False(cancellation.TryCancel());
        Assert.Null(cancellation.TakeCancellationFailure());
    }

    private static FrameHeader AckHeader() => new()
    {
        PayloadLength = 2,
        ProtocolVersion = DeviceProtocol.Version,
        MessageType = DeviceMessageType.HelloAck,
        RequestId = 1,
        Flags = FrameFlags.IsResponse,
    };
}
