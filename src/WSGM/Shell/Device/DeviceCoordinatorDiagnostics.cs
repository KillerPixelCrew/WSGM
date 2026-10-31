using System;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Lifecycle;

namespace WSGM.Shell;

/// <summary>Bounded read-only view exposed by the resident device coordinator.</summary>
internal sealed record DeviceCoordinatorDiagnosticsSnapshot
{
    public required DeviceCycleState State { get; init; }

    public HandheldDiagnostic? Handheld { get; init; }

    public required int CapabilityCount { get; init; }

    public required int AvailableCapabilityCount { get; init; }

    public required int FaultedCapabilityCount { get; init; }

    public required DateTimeOffset CapturedAt { get; init; }
}

/// <summary>Sanitized direct-library family information for standalone Settings.</summary>
/// <param name="FamilyId">Stable handheld family identifier; no native path or private state is exposed.</param>
/// <param name="Version">Direct library assembly version.</param>
internal sealed record HandheldDiagnostic(
    string FamilyId,
    string Version);

/// <summary>The snapshot's wire format, shared by the server and the client.</summary>
[JsonSerializable(typeof(DeviceCoordinatorDiagnosticsSnapshot))]
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
internal partial class DeviceCoordinatorDiagnosticsJsonContext : JsonSerializerContext;

/// <summary>Current-user-only one-shot diagnostics server owned by the shell process.</summary>
internal sealed class DeviceCoordinatorDiagnosticsServer : IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string _pipeName;
    private readonly Func<DeviceCoordinatorDiagnosticsSnapshot> _snapshot;
    private readonly Task _worker;

    internal DeviceCoordinatorDiagnosticsServer(
        uint sessionId,
        Func<DeviceCoordinatorDiagnosticsSnapshot> snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _pipeName = $"WSGM.DeviceCoordinator.{sessionId}";
        _snapshot = snapshot;
        _worker = RunAsync(_lifetime.Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        try
        {
            await _worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _lifetime.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var connected = false;
            try
            {
                await using NamedPipeServerStream pipe = new(
                    _pipeName,
                    PipeDirection.Out,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                    4096,
                    64 * 1024);
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                connected = true;
                await JsonSerializer.SerializeAsync(
                    pipe,
                    _snapshot(),
                    DeviceCoordinatorDiagnosticsJsonContext.Default.DeviceCoordinatorDiagnosticsSnapshot,
                    cancellationToken).ConfigureAwait(false);
                await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
                Log.Change("device-diagnostics-pipe", "Device diagnostics pipe serving normally.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                           or JsonException)
            {
                Log.Change("device-diagnostics-pipe", $"Device diagnostics pipe failed: {ex.Message}", LogLevel.Warn);
                if (!connected)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }
}

/// <summary>Read-only client used by standalone Settings; it cannot own or command hardware.</summary>
internal static class DeviceCoordinatorDiagnosticsClient
{
    internal static async Task<DeviceCoordinatorDiagnosticsSnapshot?> TryReadAsync(
        uint sessionId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        bounded.CancelAfter(timeout);
        await using NamedPipeClientStream pipe = new(
            ".",
            $"WSGM.DeviceCoordinator.{sessionId}",
            PipeDirection.In,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(bounded.Token).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync(
                pipe,
                DeviceCoordinatorDiagnosticsJsonContext.Default.DeviceCoordinatorDiagnosticsSnapshot,
                bounded.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException
                                       or JsonException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return null;
        }
    }
}
