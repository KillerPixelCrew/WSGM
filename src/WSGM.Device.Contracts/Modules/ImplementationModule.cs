using System.Collections.Generic;
using System.Text.Json.Serialization;
using WSGM.Device.Contracts.Packaging;

namespace WSGM.Device.Contracts.Modules;

/// <summary>
/// A reusable implementation unit that a device definition may compose.
/// </summary>
/// <remarks>
/// The four layers exist to make one specific mistake impossible rather than merely discouraged:
/// reusing a vendor transport or protocol must never drag along the donor model's power limits, fan
/// tables, register offsets, persistence assumptions, or recovery policy.
/// <para>
/// That is enforced through <see cref="VerifiedDeviceIds"/>. Transport and protocol modules carry no
/// device scope, because moving bytes and framing commands genuinely are model-agnostic. Layout and
/// policy modules — the layers that hold addresses, ranges, and firmware behaviour — must name every
/// device they were verified on, and a definition may only compose one that names it. A definition
/// therefore cannot inherit another board's limits even by copying its composition, because the
/// composition itself will not validate.
/// </para>
/// </remarks>
public sealed record ImplementationModule
{
    /// <summary>Module identifier, for example <c>MsiClawMcu</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Module version. Compositions pin an exact version.</summary>
    public required int Version { get; init; }

    /// <summary>Which of the four layers this module implements.</summary>
    public required ModuleLayer Layer { get; init; }

    /// <summary>Human-readable name shown in Device Lab candidate comparison.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Modules this one requires to be present in the same composition.</summary>
    public IReadOnlyList<ModuleDependency> Dependencies { get; init; } = [];

    /// <summary>
    /// Modules that must never appear in the same composition, for example two drivers that would
    /// both claim the same endpoint.
    /// </summary>
    public IReadOnlyList<string> Conflicts { get; init; } = [];

    /// <summary>
    /// Device definition IDs this module has been verified on.
    /// </summary>
    /// <remarks>
    /// Required for <see cref="ModuleLayer.Layout"/> and <see cref="ModuleLayer.Policy"/>, and
    /// forbidden for <see cref="ModuleLayer.Transport"/> and <see cref="ModuleLayer.Protocol"/>.
    /// This asymmetry is the whole mechanism: it is what stops a transport from being a smuggling
    /// route for a model-specific constant.
    /// </remarks>
    public IReadOnlyList<string> VerifiedDeviceIds { get; init; } = [];

    /// <summary>Semantic capabilities this module can implement.</summary>
    public IReadOnlyList<string> Capabilities { get; init; } = [];

    /// <summary>Bounds and hazards that govern how this module may be exercised.</summary>
    public required ModuleSafety Safety { get; init; }

    /// <summary>How state this module changes is captured and restored.</summary>
    public required ModuleRecovery Recovery { get; init; }

    /// <summary>Evidence claim IDs supporting every constant this module contains.</summary>
    public IReadOnlyList<string> EvidenceIds { get; init; } = [];

    /// <summary>Where this implementation came from and under what licence.</summary>
    public required PackageProvenance Provenance { get; init; }
}

/// <summary>A pinned dependency on another module.</summary>
/// <param name="Id">Identifier of the required module.</param>
/// <param name="MinVersion">Lowest acceptable version.</param>
/// <param name="MaxVersion">Highest acceptable version.</param>
public sealed record ModuleDependency(string Id, int MinVersion, int MaxVersion);

/// <summary>
/// Bounds and hazards that govern how a module may be exercised.
/// </summary>
public sealed record ModuleSafety
{
    /// <summary>Whether this module writes to hardware at all.</summary>
    public bool Writes { get; init; }

    /// <summary>How long a write survives.</summary>
    public required PersistenceClass Persistence { get; init; }

    /// <summary>Whether the module needs an elevated host to function.</summary>
    public bool RequiresElevation { get; init; }

    /// <summary>Whether operations require AC power rather than battery.</summary>
    public bool RequiresAcPower { get; init; }

    /// <summary>Whether an operation causes the device to re-enumerate.</summary>
    public bool CausesReenumeration { get; init; }

    /// <summary>Named hazards a reviewer must weigh, in plain language.</summary>
    public IReadOnlyList<string> Hazards { get; init; } = [];
}

/// <summary>How long a hardware write made by a module survives.</summary>
/// <remarks>
/// <see cref="Unknown"/> is the honest default and is treated as persistent by every safety rule:
/// assuming a setter is volatile because it looks like one is how a probe leaves a device changed.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<PersistenceClass>))]
public enum PersistenceClass
{
    /// <summary>Persistence has not been established. Treated as persistent.</summary>
    Unknown,

    /// <summary>Lost on power cycle.</summary>
    Volatile,

    /// <summary>Survives reboot, stored on the device.</summary>
    DevicePersistent,
}

/// <summary>
/// How state a module changes is captured and put back.
/// </summary>
public sealed record ModuleRecovery
{
    /// <summary>Whether original state must be journalled before any write.</summary>
    public bool SnapshotRequired { get; init; }

    /// <summary>Whether rollback has been exercised on the target hardware, not merely written.</summary>
    public bool RollbackVerifiedOnHardware { get; init; }

    /// <summary>
    /// The action that returns the device to a safe state when the plugin disappears mid-operation,
    /// for example releasing fan control back to firmware.
    /// </summary>
    public string? EmergencyAction { get; init; }
}
