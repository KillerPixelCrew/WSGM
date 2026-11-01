using System;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Shell;

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
