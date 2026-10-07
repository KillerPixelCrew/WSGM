using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Input;

namespace WSGM.Input;

/// <summary>Backend discovery result; availability does not imply a virtual target has been created.</summary>
/// <param name="Ready">Whether this backend can accept target creation.</param>
/// <param name="Detail">Availability or refusal explanation suitable for the owning service.</param>
/// <param name="Targets">Target kinds with supported wire encoders in this backend.</param>
internal sealed record ControllerBackendHealth(
    bool Ready,
    string Detail,
    IReadOnlyList<ManagedControllerTarget> Targets);

/// <summary>Logical target identity used to reject operations against a replaced native device.</summary>
/// <param name="Kind">Emulated controller protocol.</param>
/// <param name="Generation">Backend-issued generation for one target lifetime; this is not a native handle.</param>
internal sealed record ControllerTargetHandle(
    ManagedControllerTarget Kind,
    long Generation);

/// <summary>Decoded virtual-controller feedback for the physical output router.</summary>
/// <param name="Frame">Canonical motor intensities and observation timestamp.</param>
/// <param name="SourceKind">Virtual protocol that produced the feedback.</param>
/// <param name="StopAfter">Bounded pulse duration, or null for output held until another frame or explicit stop.</param>
internal readonly record struct ControllerTargetOutput(
    HapticOutputFrame Frame,
    ManagedControllerTarget SourceKind,
    TimeSpan? StopAfter = null);

/// <summary>Presents the virtual controller target WSGM forwards the physical pad to.</summary>
internal interface IControllerTargetBackend : IAsyncDisposable
{
    /// <summary>Decoded feedback raised from the backend callback thread; handlers must not block native callbacks.</summary>
    event EventHandler<ControllerTargetOutput>? OutputReceived;

    /// <summary>Reports the lost target generation after releasing the backend gate; handlers must not assume a UI thread.</summary>
    event EventHandler<long>? TargetLost;

    /// <summary>Initializes or inspects backend availability without creating a target.</summary>
    /// <param name="cancellationToken">Cancels waiting for backend serialization.</param>
    /// <returns>Readiness, supported target kinds and a diagnostic explanation.</returns>
    Task<ControllerBackendHealth> DiscoverAsync(CancellationToken cancellationToken);

    /// <summary>Creates one target; a returned handle means the host accepted the device.</summary>
    /// <param name="kind">One of the backend's supported emulated protocols.</param>
    /// <param name="initialNeutralState">Neutral first report installed before host attachment.</param>
    /// <param name="cancellationToken">Cancels gate waits and attach-retry delays; synchronous native calls may finish first.</param>
    /// <returns>A new generation owned until removal or backend disposal.</returns>
    /// <exception cref="InvalidOperationException">The kind is unsupported, a target exists, or attachment fails.</exception>
    Task<ControllerTargetHandle> CreateTargetAsync(
        ManagedControllerTarget kind,
        CanonicalControllerSample initialNeutralState,
        CancellationToken cancellationToken);

    /// <summary>Publishes one canonical sample to the still-current target.</summary>
    /// <param name="target">Expected target generation.</param>
    /// <param name="sample">Validated canonical input; the backend encodes its selected wire protocol.</param>
    /// <param name="cancellationToken">Cancels waiting for the backend gate.</param>
    /// <returns>True when accepted or already current; false for a stale/disposed target or failed submission.</returns>
    ValueTask<bool> PublishAsync(
        ControllerTargetHandle target,
        CanonicalControllerSample sample,
        CancellationToken cancellationToken);

    /// <summary>Removes the target and reports whether the removal was confirmed.</summary>
    /// <param name="target">Target generation to remove; an already absent generation succeeds.</param>
    /// <param name="cancellationToken">Cancels waiting for backend serialization.</param>
    /// <returns>
    ///     <see langword="false" /> when the device may still be enumerated; the handle is dropped either way.
    /// </returns>
    Task<bool> RemoveTargetAsync(ControllerTargetHandle target, CancellationToken cancellationToken);
}
