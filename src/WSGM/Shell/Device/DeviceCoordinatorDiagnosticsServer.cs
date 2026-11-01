using System;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Shell;

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
