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
    private readonly string _pipeName;
    private readonly Func<DeviceCoordinatorDiagnosticsSnapshot> _snapshot;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _worker;

    internal DeviceCoordinatorDiagnosticsServer(
        uint sessionId,
        Func<DeviceCoordinatorDiagnosticsSnapshot> snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _pipeName = DeviceCoordinatorDiagnosticsContract.PipeName(sessionId);
        _snapshot = snapshot;
        _worker = RunAsync(_lifetime.Token);
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
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
            try
            {
                await using NamedPipeServerStream pipe = new(
                    _pipeName,
                    PipeDirection.Out,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                    inBufferSize: 4096,
                    outBufferSize: 64 * 1024);
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                await JsonSerializer.SerializeAsync(
                    pipe,
                    _snapshot(),
                    ConfigJsonContext.Default.DeviceCoordinatorDiagnosticsSnapshot,
                    cancellationToken).ConfigureAwait(false);
                await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or JsonException)
            {
                Log.Warn($"Device diagnostics pipe recovered after failure: {ex.Message}");
            }
        }
    }
}
