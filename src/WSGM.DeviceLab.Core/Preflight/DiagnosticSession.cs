using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using WSGM.Device.Contracts.Lifecycle;

namespace WSGM.DeviceLab.Core.Preflight;

/// <summary>Closed authorization outcomes for a production-plugin diagnostic session.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DeviceLabDiagnosticAuthorizationStatus>))]
public enum DeviceLabDiagnosticAuthorizationStatus
{
    /// <summary>The bounded semantic session may be requested from the owner.</summary>
    Authorized,

    /// <summary>Preflight did not select the production diagnostic route.</summary>
    WrongRoute,

    /// <summary>The request deadline elapsed or exceeds the bounded-session window.</summary>
    InvalidDeadline,

    /// <summary>The requested observation count is outside the hard bound.</summary>
    InvalidObservationLimit,

    /// <summary>The owner or device generation changed after preflight.</summary>
    StaleGeneration,
}

/// <summary>One bounded request sent to the existing production owner.</summary>
public sealed record DeviceLabDiagnosticSessionRequest
{
    /// <summary>Single semantic resource to observe.</summary>
    public required string ResourceId { get; init; }

    /// <summary>Host generation pinned by preflight.</summary>
    public required long HostGeneration { get; init; }

    /// <summary>Device generation pinned by preflight.</summary>
    public required long DeviceGeneration { get; init; }

    /// <summary>Maximum number of semantic observations requested.</summary>
    public required int MaximumObservations { get; init; }

    /// <summary>UTC deadline after which the owner must stop serving the request.</summary>
    public required DateTimeOffset Deadline { get; init; }

    /// <summary>The only lease kind this request can carry.</summary>
    public LeaseKind LeaseKind => LeaseKind.Diagnostic;

    /// <summary>The request cannot ask DeviceHost to activate, deactivate, or restart.</summary>
    public bool MayChangeDeviceCycle => false;

    /// <summary>The request cannot receive a raw HID, WMI, serial, or other transport.</summary>
    public bool ReceivesRawTransport => false;
}

/// <summary>Authorization result passed to the production-owner diagnostics client.</summary>
public sealed record DeviceLabDiagnosticSessionAuthorization
{
    /// <summary>Closed authorization outcome.</summary>
    public required DeviceLabDiagnosticAuthorizationStatus Status { get; init; }

    /// <summary>Validated request when authorization succeeded.</summary>
    public DeviceLabDiagnosticSessionRequest? Request { get; init; }

    /// <summary>Operator-facing refusal reason.</summary>
    public string? Reason { get; init; }
}

/// <summary>One semantic observation produced by the already-running plugin.</summary>
public sealed record DeviceLabDiagnosticObservation
{
    /// <summary>Strictly increasing sequence within this session.</summary>
    public required long Sequence { get; init; }

    /// <summary>UTC observation time supplied by the production owner.</summary>
    public required DateTimeOffset ObservedAt { get; init; }

    /// <summary>Semantic observation kind.</summary>
    public required string Kind { get; init; }

    /// <summary>Bounded semantic values; never raw transport payloads.</summary>
    public IReadOnlyDictionary<string, string> Values { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>Authorizes bounded plugin-owned diagnostics without touching the device cycle.</summary>
public static class DeviceLabDiagnosticSessionPolicy
{
    /// <summary>Largest number of observations in one diagnostic session.</summary>
    public const int MaximumObservations = 4096;

    /// <summary>Longest diagnostic session accepted by preflight.</summary>
    public static readonly TimeSpan MaximumDuration = TimeSpan.FromSeconds(30);

    /// <summary>Validates a diagnostic request against the pinned preflight decision.</summary>
    /// <param name="request">Requested resource, generations, count, and deadline.</param>
    /// <param name="preflight">Decision made before the resource route was selected.</param>
    /// <param name="currentHostGeneration">Owner generation at request time.</param>
    /// <param name="currentDeviceGeneration">Device generation at request time.</param>
    /// <param name="now">Current UTC time.</param>
    /// <returns>An authorization value; it never opens or returns a transport.</returns>
    public static DeviceLabDiagnosticSessionAuthorization Authorize(
        DeviceLabDiagnosticSessionRequest request,
        DeviceLabPreflightDecision preflight,
        long currentHostGeneration,
        long currentDeviceGeneration,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(preflight);

        if (preflight.Route is not DeviceLabAccessRoute.ProductionDiagnosticSession
            || preflight.RequiredLease is not LeaseKind.Diagnostic
            || preflight.Status is DeviceLabDoctorStatus.Blocked)
        {
            return Refuse(
                DeviceLabDiagnosticAuthorizationStatus.WrongRoute,
                "Preflight did not authorize a production diagnostic session.");
        }

        if (request.MaximumObservations <= 0
            || request.MaximumObservations > MaximumObservations)
        {
            return Refuse(
                DeviceLabDiagnosticAuthorizationStatus.InvalidObservationLimit,
                $"Maximum observations must be between 1 and {MaximumObservations}.");
        }

        if (request.Deadline <= now || request.Deadline > now + MaximumDuration)
        {
            return Refuse(
                DeviceLabDiagnosticAuthorizationStatus.InvalidDeadline,
                $"The deadline must be within {MaximumDuration.TotalSeconds:F0} seconds.");
        }

        if (preflight.HostGeneration != request.HostGeneration
            || preflight.DeviceGeneration != request.DeviceGeneration
            || !string.Equals(preflight.ResourceId, request.ResourceId, StringComparison.Ordinal)
            || currentHostGeneration != request.HostGeneration
            || currentDeviceGeneration != request.DeviceGeneration)
        {
            return Refuse(
                DeviceLabDiagnosticAuthorizationStatus.StaleGeneration,
                "Host or device generation changed after preflight.");
        }

        return new DeviceLabDiagnosticSessionAuthorization
        {
            Status = DeviceLabDiagnosticAuthorizationStatus.Authorized,
            Request = request,
        };
    }

    private static DeviceLabDiagnosticSessionAuthorization Refuse(
        DeviceLabDiagnosticAuthorizationStatus status,
        string reason) => new()
        {
            Status = status,
            Reason = reason,
        };
}
