using System;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Sdk.Lifecycle;

namespace WSGM.Core;

/// <summary>Bounded read-only view exposed by the resident device coordinator.</summary>
internal sealed record DeviceCoordinatorDiagnosticsSnapshot
{
    public required DeviceCycleState State { get; init; }

    public DeviceInstalledPackageDiagnostic? InstalledPackage { get; init; }

    public required long CycleGeneration { get; init; }

    public required int CapabilityCount { get; init; }

    public required int HealthyCapabilityCount { get; init; }

    public required int FaultedCapabilityCount { get; init; }

    public required DateTimeOffset CapturedAt { get; init; }
}

/// <summary>Sanitized sole installed-package information for standalone Settings.</summary>
internal sealed record DeviceInstalledPackageDiagnostic(
    string PackageId,
    string Version);

/// <summary>Shared one-shot diagnostics pipe identity.</summary>
internal static class DeviceCoordinatorDiagnosticsContract
{
    internal static string PipeName(uint sessionId) => $"WSGM.DeviceCoordinator.{sessionId}";
}

/// <summary>Read-only client used by standalone Settings; it cannot own or command hardware.</summary>
internal static class DeviceCoordinatorDiagnosticsClient
{
    internal static async Task<DeviceCoordinatorDiagnosticsSnapshot?> TryReadAsync(
        uint sessionId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource bounded = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        bounded.CancelAfter(timeout);
        await using NamedPipeClientStream pipe = new(
            ".",
            DeviceCoordinatorDiagnosticsContract.PipeName(sessionId),
            PipeDirection.In,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(bounded.Token).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync(
                pipe,
                ConfigJsonContext.Default.DeviceCoordinatorDiagnosticsSnapshot,
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
