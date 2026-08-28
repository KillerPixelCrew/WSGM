using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Contracts.Lifecycle;

namespace WSGM.Core;

/// <summary>Bounded read-only view exposed by the resident device coordinator.</summary>
internal sealed record DeviceCoordinatorDiagnosticsSnapshot
{
    internal const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public required DeviceCycleState State { get; init; }

    public string? PackageId { get; init; }

    public string? PackageVersion { get; init; }

    public DevicePluginTrustTier? TrustTier { get; init; }

    public required long HostGeneration { get; init; }

    public required long DeviceGeneration { get; init; }

    public required int CapabilityCount { get; init; }

    public required int HealthyCapabilityCount { get; init; }

    public required int FaultedCapabilityCount { get; init; }

    public IReadOnlyList<DevicePackageDiagnostic> Packages { get; init; } = [];

    public required DateTimeOffset CapturedAt { get; init; }
}

/// <summary>Sanitized package candidate information for standalone Settings.</summary>
internal sealed record DevicePackageDiagnostic(
    string? PackageId,
    string? Version,
    DevicePluginTrustTier TrustTier,
    bool Eligible,
    string? RejectionCode);

/// <summary>Shared one-shot diagnostics pipe identity.</summary>
internal static class DeviceCoordinatorDiagnosticsContract
{
    internal const int MaxPayloadBytes = 1024 * 1024;

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
            byte[] header = new byte[sizeof(int)];
            await pipe.ReadExactlyAsync(header, bounded.Token).ConfigureAwait(false);
            int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (payloadLength is <= 0 or > DeviceCoordinatorDiagnosticsContract.MaxPayloadBytes)
            {
                throw new InvalidDataException("Device diagnostics frame length is invalid.");
            }

            byte[] payload = new byte[payloadLength];
            await pipe.ReadExactlyAsync(payload, bounded.Token).ConfigureAwait(false);
            DeviceCoordinatorDiagnosticsSnapshot? snapshot = JsonSerializer.Deserialize(
                payload,
                DeviceCoordinatorDiagnosticsJsonContext.Default.DeviceCoordinatorDiagnosticsSnapshot);
            return snapshot?.SchemaVersion == DeviceCoordinatorDiagnosticsSnapshot.CurrentSchemaVersion
                ? snapshot
                : null;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException
            or JsonException or InvalidDataException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return null;
        }
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(DeviceCoordinatorDiagnosticsSnapshot))]
internal sealed partial class DeviceCoordinatorDiagnosticsJsonContext : JsonSerializerContext;
