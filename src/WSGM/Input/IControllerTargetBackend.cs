using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Input;

namespace WSGM.Input;

internal sealed record ControllerBackendHealth(
    bool Ready,
    string Detail,
    IReadOnlyList<ManagedControllerTarget> Targets);

internal sealed record ControllerTargetHandle(
    ManagedControllerTarget Kind,
    long Generation);

internal readonly record struct ControllerTargetOutput(
    HapticOutputFrame Frame,
    ManagedControllerTarget SourceKind,
    TimeSpan? StopAfter = null);

/// <summary>Presents the virtual controller target WSGM forwards the physical pad to.</summary>
internal interface IControllerTargetBackend : IAsyncDisposable
{
    event EventHandler<ControllerTargetOutput>? OutputReceived;

    event EventHandler<long>? TargetLost;

    Task<ControllerBackendHealth> DiscoverAsync(CancellationToken cancellationToken);

    /// <summary>Creates the target; a returned handle means the host accepted the device.</summary>
    Task<ControllerTargetHandle> CreateTargetAsync(
        ManagedControllerTarget kind,
        CanonicalControllerSample initialNeutralState,
        CancellationToken cancellationToken);

    ValueTask<bool> PublishAsync(
        ControllerTargetHandle target,
        CanonicalControllerSample sample,
        CancellationToken cancellationToken);

    /// <summary>Removes the target and reports whether the removal was confirmed.</summary>
    /// <returns>
    ///     <see langword="false" /> when the device may still be enumerated; the handle is dropped either way.
    /// </returns>
    Task<bool> RemoveTargetAsync(ControllerTargetHandle target, CancellationToken cancellationToken);
}
