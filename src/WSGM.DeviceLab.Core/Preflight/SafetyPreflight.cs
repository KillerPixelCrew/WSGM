using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using WSGM.Device.Contracts.Lifecycle;

namespace WSGM.DeviceLab.Core.Preflight;

/// <summary>Device access requested after environment preflight.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DeviceLabOperationAccess>))]
public enum DeviceLabOperationAccess
{
    /// <summary>Passive enumeration or observation without a catalog probe.</summary>
    PassiveObservation,

    /// <summary>A named, reviewed, bounded read probe.</summary>
    ReviewedReadProbe,

    /// <summary>The single named mutation-trial pathway.</summary>
    MutationTrial,
}

/// <summary>Where an operation description came from.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DeviceLabOperationOrigin>))]
public enum DeviceLabOperationOrigin
{
    /// <summary>Reviewed code and metadata built into this Device Lab build.</summary>
    ReviewedBuiltInCatalog,

    /// <summary>A signed-external package installed for developer work.</summary>
    SignedExternalPackage,

    /// <summary>A sideloaded package installed for developer work.</summary>
    SideloadedPackage,

    /// <summary>A developer trial compiled from this source checkout.</summary>
    DeveloperSourceBuild,

    /// <summary>An imported capture bundle.</summary>
    ImportedCapture,

    /// <summary>An imported inert recipe.</summary>
    ImportedRecipe,

    /// <summary>An imported package or device manifest.</summary>
    ImportedManifest,

    /// <summary>An imported plugin package.</summary>
    ImportedPluginPackage,

    /// <summary>An imported evidence lock.</summary>
    ImportedEvidenceLock,

    /// <summary>An imported acceptance manifest.</summary>
    ImportedAcceptanceManifest,
}

/// <summary>How Device Lab may reach one resource after preflight.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DeviceLabAccessRoute>))]
public enum DeviceLabAccessRoute
{
    /// <summary>No access is permitted.</summary>
    None,

    /// <summary>Device Lab may open only a direct read-only observation path.</summary>
    DirectReadOnly,

    /// <summary>The production plugin serves a bounded read-only diagnostic session.</summary>
    ProductionDiagnosticSession,

    /// <summary>A direct trial may proceed only after an exclusive experiment lease is granted.</summary>
    ExperimentLease,
}

/// <summary>Whether a production device owner was discovered and inspectable.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DeviceOwnerDiscoveryState>))]
public enum DeviceOwnerDiscoveryState
{
    /// <summary>No session owner object was found.</summary>
    Absent,

    /// <summary>An owner supplied a versioned diagnostics snapshot.</summary>
    PresentWithDiagnostics,

    /// <summary>An owner exists but could not supply its diagnostics snapshot.</summary>
    PresentWithoutDiagnostics,

    /// <summary>Discovery produced contradictory owner evidence.</summary>
    Ambiguous,
}

/// <summary>Evidence that another component owns a resource.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DeviceLabOwnershipEvidence>))]
public enum DeviceLabOwnershipEvidence
{
    /// <summary>The component is installed or present, with no ownership evidence.</summary>
    PresenceOnly,

    /// <summary>Only a matching process, service, task, provider, or resource name was observed.</summary>
    NameOnly,

    /// <summary>An exclusive-access failure demonstrated another current holder.</summary>
    ExclusiveAccessFailure,

    /// <summary>A versioned lease or diagnostics snapshot names the holder.</summary>
    DeclaredLease,

    /// <summary>Competing operations were directly observed.</summary>
    ObservedConflict,
}

/// <summary>One relevant installed component, event source, or ownership conflict.</summary>
public sealed record DeviceLabExternalComponent
{
    /// <summary>Stable component identifier.</summary>
    public required string ComponentId { get; init; }

    /// <summary>Process, service, driver, provider, DLL, helper, task, or event-source category.</summary>
    public required string Kind { get; init; }

    /// <summary>Whether the component is installed or currently present.</summary>
    public required bool Present { get; init; }

    /// <summary>Whether the current process can inspect the component.</summary>
    public required bool Accessible { get; init; }

    /// <summary>Resource affected when actual ownership evidence exists.</summary>
    public string? ResourceId { get; init; }

    /// <summary>Strength of current ownership evidence.</summary>
    public DeviceLabOwnershipEvidence OwnershipEvidence { get; init; }

    /// <summary>Bounded diagnostic detail.</summary>
    public string? Detail { get; init; }
}

/// <summary>Power and thermal values checked before opening a resource.</summary>
public sealed record DeviceLabPowerThermalSnapshot
{
    /// <summary>Whether external power is connected, or unknown.</summary>
    public bool? ExternalPowerConnected { get; init; }

    /// <summary>Battery percentage, or unknown.</summary>
    public int? BatteryPercent { get; init; }

    /// <summary>Relevant device or package temperature in Celsius, or unknown.</summary>
    public double? TemperatureCelsius { get; init; }
}

/// <summary>Requirements declared by one locally resolved observation, probe, or trial.</summary>
public sealed record DeviceLabOperationRequirements
{
    /// <summary>Stable operation identifier.</summary>
    public required string OperationId { get; init; }

    /// <summary>Single resource the operation needs.</summary>
    public required string ResourceId { get; init; }

    /// <summary>Requested access class.</summary>
    public required DeviceLabOperationAccess Access { get; init; }

    /// <summary>Origin of the operation description.</summary>
    public required DeviceLabOperationOrigin Origin { get; init; }

    /// <summary>Whether the operation was resolved from a local installation.</summary>
    public required bool IsLocallyInstalled { get; init; }

    /// <summary>Whether local content matches the reviewed catalog hash.</summary>
    public required bool IsHashPinned { get; init; }

    /// <summary>Whether the exact device-family gate passed.</summary>
    public required bool ExactFamilyMatched { get; init; }

    /// <summary>Whether the exact endpoint gate passed.</summary>
    public required bool ExactEndpointMatched { get; init; }

    /// <summary>Whether an explicit Developer Mode action approved this developer read.</summary>
    public bool DeveloperModeApproved { get; init; }

    /// <summary>Whether this operation requires an elevated token.</summary>
    public bool RequiresElevation { get; init; }

    /// <summary>Reviewed helper required before the resource is opened.</summary>
    public string? RequiredReviewedHelperId { get; init; }

    /// <summary>Whether external power is mandatory.</summary>
    public bool RequiresExternalPower { get; init; }

    /// <summary>Minimum acceptable battery percentage.</summary>
    public int? MinimumBatteryPercent { get; init; }

    /// <summary>Maximum acceptable starting temperature in Celsius.</summary>
    public double? MaximumTemperatureCelsius { get; init; }

    /// <summary>Catalog-specific prerequisite failures already established.</summary>
    public IReadOnlyList<string> CatalogBlockReasons { get; init; } = [];
}

/// <summary>All state inspected before a read session or mutation trial.</summary>
public sealed record DeviceLabSafetySnapshot
{
    /// <summary>Environment and output doctor result.</summary>
    public required DeviceLabDoctorReport Doctor { get; init; }

    /// <summary>Whether Device Integration is enabled, or unknown without an owner snapshot.</summary>
    public required bool? DeviceIntegrationEnabled { get; init; }

    /// <summary>Owner discovery outcome for the current session.</summary>
    public required DeviceOwnerDiscoveryState OwnerDiscovery { get; init; }

    /// <summary>Versioned owner snapshot when it was available.</summary>
    public DeviceDiagnosticsSnapshot? ActiveDevice { get; init; }

    /// <summary>Current power and thermal observation.</summary>
    public required DeviceLabPowerThermalSnapshot PowerThermal { get; init; }

    /// <summary>Whether the current process has the required elevation.</summary>
    public required bool IsElevated { get; init; }

    /// <summary>Whether a local interactive user session is available.</summary>
    public required bool IsUserInteractive { get; init; }

    /// <summary>Whether this process is running under CI.</summary>
    public required bool IsContinuousIntegration { get; init; }

    /// <summary>Reviewed helpers installed with their expected identity.</summary>
    public IReadOnlySet<string> AvailableReviewedHelperIds { get; init; } =
        new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Relevant tools, providers, drivers, helpers, event sources, and conflicts.</summary>
    public IReadOnlyList<DeviceLabExternalComponent> ExternalComponents { get; init; } = [];
}

/// <summary>One stable preflight reason.</summary>
public sealed record DeviceLabPreflightCheck
{
    /// <summary>Stable machine-readable reason code.</summary>
    public required string Code { get; init; }

    /// <summary>Pass, warning, or blocked consequence.</summary>
    public required DeviceLabDoctorStatus Status { get; init; }

    /// <summary>Operator-facing explanation.</summary>
    public required string Message { get; init; }
}

/// <summary>Complete access decision produced before any device resource is opened.</summary>
public sealed record DeviceLabPreflightDecision
{
    /// <summary>Single resource pinned by this preflight.</summary>
    public required string ResourceId { get; init; }

    /// <summary>Worst outcome across preflight checks.</summary>
    public required DeviceLabDoctorStatus Status { get; init; }

    /// <summary>Only route the operation may use.</summary>
    public required DeviceLabAccessRoute Route { get; init; }

    /// <summary>Lease kind that must be granted before the route begins.</summary>
    public LeaseKind? RequiredLease { get; init; }

    /// <summary>Host generation pinned by this preflight.</summary>
    public long? HostGeneration { get; init; }

    /// <summary>Device generation pinned by this preflight.</summary>
    public long? DeviceGeneration { get; init; }

    /// <summary>Inspected state of the named resource.</summary>
    public ResourceState? ResourceState { get; init; }

    /// <summary>Device Lab never receives a raw production transport.</summary>
    public bool ReceivesRawTransport => false;

    /// <summary>Device Lab preflight never activates, deactivates, or recreates DeviceHost.</summary>
    public bool MayChangeDeviceCycle => false;

    /// <summary>Device Lab never disables Device Integration to acquire a resource.</summary>
    public bool MayDisableDeviceIntegration => false;

    /// <summary>Checks in stable policy order.</summary>
    public IReadOnlyList<DeviceLabPreflightCheck> Checks { get; init; } = [];
}

/// <summary>Fail-closed safety firewall evaluated before every read session or trial.</summary>
public static class DeviceLabSafetyPreflight
{
    /// <summary>Evaluates one operation without opening its target resource.</summary>
    /// <param name="requirements">Locally resolved operation requirements.</param>
    /// <param name="snapshot">Environment, ownership, conflict, and prerequisite snapshot.</param>
    /// <returns>A route and reasons; blocked results grant no access.</returns>
    public static DeviceLabPreflightDecision Evaluate(
        DeviceLabOperationRequirements requirements,
        DeviceLabSafetySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        ArgumentNullException.ThrowIfNull(snapshot);

        List<DeviceLabPreflightCheck> checks = [];
        DeviceLabAccessRoute route = requirements.Access is DeviceLabOperationAccess.MutationTrial
            ? DeviceLabAccessRoute.ExperimentLease
            : DeviceLabAccessRoute.DirectReadOnly;
        LeaseKind? requiredLease = requirements.Access is DeviceLabOperationAccess.MutationTrial
            ? LeaseKind.Experiment
            : null;
        long? hostGeneration = null;
        long? deviceGeneration = null;
        ResourceState? resourceState = null;

        if (snapshot.Doctor.Status is DeviceLabDoctorStatus.Blocked)
        {
            Block(checks, "doctor.blocked", "Environment or output doctor checks are blocked.");
        }

        ValidateOrigin(requirements, snapshot, checks);

        if (!requirements.ExactFamilyMatched)
        {
            Block(checks, "identity.family", "The exact device-family gate did not match.");
        }

        if (!requirements.ExactEndpointMatched)
        {
            Block(checks, "identity.endpoint", "The exact endpoint gate did not match.");
        }

        if (!requirements.IsLocallyInstalled || !requirements.IsHashPinned)
        {
            Block(checks, "operation.content", "Operation code must be locally installed and hash-pinned.");
        }

        foreach (string reason in requirements.CatalogBlockReasons.Order(StringComparer.Ordinal))
        {
            Block(checks, "catalog.prerequisite", reason);
        }

        ValidatePowerThermal(requirements, snapshot.PowerThermal, checks);

        if (requirements.RequiresElevation && !snapshot.IsElevated)
        {
            Block(checks, "permission.elevation", "This operation requires elevation before opening the resource.");
        }

        if (requirements.RequiredReviewedHelperId is { Length: > 0 } helperId
            && !snapshot.AvailableReviewedHelperIds.Contains(helperId))
        {
            Block(checks, "permission.helper", $"Reviewed helper '{helperId}' is unavailable.");
        }

        foreach (DeviceLabExternalComponent component in snapshot.ExternalComponents
            .OrderBy(component => component.ComponentId, StringComparer.Ordinal))
        {
            if (!component.Present)
            {
                continue;
            }

            if (!component.Accessible)
            {
                Warn(checks, "component.access", $"{component.ComponentId} is present but not inspectable.");
            }

            bool sameResource = string.Equals(
                component.ResourceId,
                requirements.ResourceId,
                StringComparison.Ordinal);
            if (sameResource && component.OwnershipEvidence is
                DeviceLabOwnershipEvidence.ExclusiveAccessFailure
                or DeviceLabOwnershipEvidence.DeclaredLease
                or DeviceLabOwnershipEvidence.ObservedConflict)
            {
                Block(checks, "resource.external-owner",
                    $"{component.ComponentId} has demonstrated ownership of '{requirements.ResourceId}'.");
            }
            else if (component.OwnershipEvidence is DeviceLabOwnershipEvidence.NameOnly)
            {
                Warn(checks, "component.name-only",
                    $"{component.ComponentId} matches a name but does not prove resource ownership.");
            }
        }

        switch (snapshot.OwnerDiscovery)
        {
            case DeviceOwnerDiscoveryState.Ambiguous:
                Block(checks, "owner.ambiguous", "Production device ownership is ambiguous.");
                route = DeviceLabAccessRoute.None;
                requiredLease = null;
                break;
            case DeviceOwnerDiscoveryState.PresentWithoutDiagnostics:
                Block(checks, "owner.uninspectable",
                    "A production owner exists but its generation and resources cannot be inspected.");
                route = DeviceLabAccessRoute.None;
                requiredLease = null;
                break;
            case DeviceOwnerDiscoveryState.PresentWithDiagnostics:
                EvaluateActiveOwner(
                    requirements,
                    snapshot.ActiveDevice,
                    checks,
                    ref route,
                    ref requiredLease,
                    out hostGeneration,
                    out deviceGeneration,
                    out resourceState);
                break;
            case DeviceOwnerDiscoveryState.Absent:
                if (snapshot.ActiveDevice is not null)
                {
                    Block(checks, "owner.contradictory", "An owner snapshot exists while discovery says absent.");
                }

                if (snapshot.DeviceIntegrationEnabled is not false
                    && requirements.Access is DeviceLabOperationAccess.MutationTrial)
                {
                    Block(checks, "owner.startup-race",
                        "Device Integration is enabled or unknown but no owner is inspectable; a trial could race startup.");
                }
                else if (snapshot.DeviceIntegrationEnabled is null)
                {
                    Warn(checks, "owner.integration-unknown",
                        "Device Integration state is unknown without a production owner snapshot.");
                }

                break;
        }

        if (requirements.Access is DeviceLabOperationAccess.MutationTrial
            && (!snapshot.IsUserInteractive || snapshot.IsContinuousIntegration))
        {
            Block(checks, "trial.interactive", "Mutation trials require a local interactive session and refuse CI.");
        }

        DeviceLabDoctorStatus status = checks.Any(check => check.Status is DeviceLabDoctorStatus.Blocked)
            ? DeviceLabDoctorStatus.Blocked
            : checks.Any(check => check.Status is DeviceLabDoctorStatus.Warning)
                ? DeviceLabDoctorStatus.Warning
                : DeviceLabDoctorStatus.Pass;
        if (status is DeviceLabDoctorStatus.Blocked)
        {
            route = DeviceLabAccessRoute.None;
            requiredLease = null;
        }

        return new DeviceLabPreflightDecision
        {
            ResourceId = requirements.ResourceId,
            Status = status,
            Route = route,
            RequiredLease = requiredLease,
            HostGeneration = hostGeneration,
            DeviceGeneration = deviceGeneration,
            ResourceState = resourceState,
            Checks = checks,
        };
    }

    private static void ValidateOrigin(
        DeviceLabOperationRequirements requirements,
        DeviceLabSafetySnapshot snapshot,
        ICollection<DeviceLabPreflightCheck> checks)
    {
        bool imported = requirements.Origin is DeviceLabOperationOrigin.ImportedCapture
            or DeviceLabOperationOrigin.ImportedRecipe
            or DeviceLabOperationOrigin.ImportedManifest
            or DeviceLabOperationOrigin.ImportedPluginPackage
            or DeviceLabOperationOrigin.ImportedEvidenceLock
            or DeviceLabOperationOrigin.ImportedAcceptanceManifest;
        if (requirements.Access is DeviceLabOperationAccess.MutationTrial && imported)
        {
            Block(checks, "authority.imported",
                "Imported files may describe evidence but can never authorize or supply mutation.");
        }

        if (requirements.Access is DeviceLabOperationAccess.MutationTrial
            && requirements.Origin is DeviceLabOperationOrigin.SignedExternalPackage
                or DeviceLabOperationOrigin.SideloadedPackage)
        {
            Block(checks, "authority.external-trial",
                "Installed external packages cannot contribute Device Lab mutation authority.");
        }

        bool developerOrigin = requirements.Origin is DeviceLabOperationOrigin.SignedExternalPackage
            or DeviceLabOperationOrigin.SideloadedPackage
            or DeviceLabOperationOrigin.DeveloperSourceBuild;
        if (requirements.Access is DeviceLabOperationAccess.ReviewedReadProbe
            && developerOrigin
            && !requirements.DeveloperModeApproved)
        {
            Block(checks, "authority.developer-mode",
                "Developer probes require an explicit Developer Mode action.");
        }

        if (requirements.Access is DeviceLabOperationAccess.MutationTrial
            && requirements.Origin is DeviceLabOperationOrigin.DeveloperSourceBuild
            && (!requirements.DeveloperModeApproved || !snapshot.IsUserInteractive))
        {
            Block(checks, "authority.developer-trial",
                "Developer trials require a source build, Developer Mode approval, and local interaction.");
        }
    }

    private static void ValidatePowerThermal(
        DeviceLabOperationRequirements requirements,
        DeviceLabPowerThermalSnapshot snapshot,
        ICollection<DeviceLabPreflightCheck> checks)
    {
        if (requirements.RequiresExternalPower && snapshot.ExternalPowerConnected is not true)
        {
            Block(checks, "power.external", "External power is required and was not confirmed.");
        }

        if (requirements.MinimumBatteryPercent is { } minimum
            && (snapshot.BatteryPercent is not { } batteryPercent || batteryPercent < minimum))
        {
            Block(checks, "power.battery",
                $"Battery must be at least {minimum}% and the prerequisite was not met.");
        }

        if (requirements.MaximumTemperatureCelsius is { } maximum)
        {
            if (snapshot.TemperatureCelsius is not { } temperature)
            {
                Block(checks, "thermal.unknown", "A bounded starting temperature is required but unavailable.");
            }
            else if (temperature > maximum)
            {
                Block(checks, "thermal.hot",
                    $"Starting temperature {temperature:F1} C exceeds the {maximum:F1} C limit.");
            }
        }
    }

    private static void EvaluateActiveOwner(
        DeviceLabOperationRequirements requirements,
        DeviceDiagnosticsSnapshot? activeDevice,
        ICollection<DeviceLabPreflightCheck> checks,
        ref DeviceLabAccessRoute route,
        ref LeaseKind? requiredLease,
        out long? hostGeneration,
        out long? deviceGeneration,
        out ResourceState? resourceState)
    {
        hostGeneration = null;
        deviceGeneration = null;
        resourceState = null;
        if (activeDevice is null)
        {
            Block(checks, "owner.snapshot-missing", "Owner discovery promised diagnostics but supplied none.");
            route = DeviceLabAccessRoute.None;
            requiredLease = null;
            return;
        }

        hostGeneration = activeDevice.HostGeneration;
        deviceGeneration = activeDevice.DeviceGeneration;
        if (!activeDevice.Resources.TryGetValue(requirements.ResourceId, out ResourceState state))
        {
            Block(checks, "owner.resource-unknown",
                $"The production definition does not declare resource '{requirements.ResourceId}'.");
            route = DeviceLabAccessRoute.None;
            requiredLease = null;
            return;
        }

        resourceState = state;
        if (requirements.Access is not DeviceLabOperationAccess.MutationTrial)
        {
            route = DeviceLabAccessRoute.ProductionDiagnosticSession;
            requiredLease = LeaseKind.Diagnostic;
            return;
        }

        if (state is ResourceState.Idle or ResourceState.Passive)
        {
            route = DeviceLabAccessRoute.ExperimentLease;
            requiredLease = LeaseKind.Experiment;
            return;
        }

        Block(checks, "owner.release-required",
            "The production plugin must orderly release this resource before an experiment lease can be requested.");
        route = DeviceLabAccessRoute.None;
        requiredLease = null;
    }

    private static void Block(
        ICollection<DeviceLabPreflightCheck> checks,
        string code,
        string message) => checks.Add(new DeviceLabPreflightCheck
        {
            Code = code,
            Status = DeviceLabDoctorStatus.Blocked,
            Message = message,
        });

    private static void Warn(
        ICollection<DeviceLabPreflightCheck> checks,
        string code,
        string message) => checks.Add(new DeviceLabPreflightCheck
        {
            Code = code,
            Status = DeviceLabDoctorStatus.Warning,
            Message = message,
        });
}
