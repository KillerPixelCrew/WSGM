using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>Observable lifecycle state of one independently recoverable Steam UI patch.</summary>
public enum SteamUiPatchState
{
    /// <summary>The patch has not yet been probed.</summary>
    Unknown,

    /// <summary>The required Steam target is absent.</summary>
    AbsentTarget,

    /// <summary>The live target does not match the patch's unique structural fingerprint.</summary>
    Incompatible,

    /// <summary>The patch passed its probe and is being applied.</summary>
    Applying,

    /// <summary>The patch reported application but has not passed functional verification.</summary>
    Applied,

    /// <summary>The patch is applied and positively verified.</summary>
    Verified,

    /// <summary>The patch is independently impaired while other patches remain available.</summary>
    Degraded,

    /// <summary>The patch was disabled and its owned resources were removed.</summary>
    Disabled,

    /// <summary>Owned-resource removal failed and remains observable.</summary>
    RemoveFailed,

    /// <summary>The patch is waiting for its target or generation to recover.</summary>
    Retrying,
}

/// <summary>Hard bounds declared by one Steam UI patch.</summary>
/// <param name="OperationTimeout">Maximum duration of one patch phase.</param>
/// <param name="MaximumExpressionCharacters">Maximum repository-owned expression size.</param>
/// <param name="MaximumDiagnosticCharacters">Maximum retained diagnostic size.</param>
public sealed record SteamUiPatchBounds(
    TimeSpan OperationTimeout,
    int MaximumExpressionCharacters,
    int MaximumDiagnosticCharacters)
{
    /// <summary>Conservative defaults for small bootstrap and store patches.</summary>
    public static SteamUiPatchBounds Default { get; } =
        new(TimeSpan.FromSeconds(8), 96 * 1024, 2048);
}

/// <summary>Positive structural probe result required before patch application.</summary>
/// <param name="TargetPresent">Whether the target could be evaluated.</param>
/// <param name="Compatible">Whether the expected structure was found.</param>
/// <param name="Unique">Whether the structural match was uniquely identified.</param>
/// <param name="Fingerprint">Stable semantic fingerprint, never a module id alone.</param>
/// <param name="Diagnostic">Bounded probe details.</param>
public sealed record SteamUiPatchProbeResult(
    bool TargetPresent,
    bool Compatible,
    bool Unique,
    string? Fingerprint,
    string? Diagnostic);

/// <summary>Result of applying, verifying, or removing a Steam UI patch.</summary>
/// <param name="Succeeded">Whether the phase completed positively.</param>
/// <param name="Diagnostic">Bounded phase details.</param>
public readonly record struct SteamUiPatchOperationResult(bool Succeeded, string? Diagnostic);

/// <summary>Context that applies one patch's declared evaluation bounds.</summary>
public sealed class SteamUiPatchContext
{
    private readonly ISteamUiTransport _transport;
    private readonly SteamUiPatchBounds _bounds;

    internal SteamUiPatchContext(ISteamUiTransport transport, SteamUiPatchBounds bounds)
    {
        _transport = transport;
        _bounds = bounds;
    }

    /// <summary>Evaluates repository-owned code on the patch's allowlisted target.</summary>
    /// <param name="role">The patch target.</param>
    /// <param name="expression">Repository-owned JavaScript.</param>
    /// <param name="cancellationToken">Cancels the phase.</param>
    /// <returns>The bounded evaluation result.</returns>
    public Task<SteamUiEvaluationResult> EvaluateAsync(
        SteamUiTargetRole role,
        string expression,
        CancellationToken cancellationToken = default)
    {
        if (expression.Length > _bounds.MaximumExpressionCharacters)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expression), "Patch expression exceeded its declared bound.");
        }
        return _transport.EvaluateAsync(
            role, expression, _bounds.OperationTimeout, cancellationToken);
    }
}

/// <summary>One independently versioned, probed, verified, removable Steam UI patch.</summary>
public interface ISteamUiPatch
{
    /// <summary>Stable collision-resistant patch id.</summary>
    string Id { get; }

    /// <summary>Patch implementation version.</summary>
    int Version { get; }

    /// <summary>Allowlisted target required by this patch.</summary>
    SteamUiTargetRole TargetRole { get; }

    /// <summary>Serialization key for DOM/store resources that may conflict.</summary>
    string ResourceKey { get; }

    /// <summary>Hard phase and payload bounds.</summary>
    SteamUiPatchBounds Bounds { get; }

    /// <summary>Probes for a positive unique live fingerprint without mutation.</summary>
    Task<SteamUiPatchProbeResult> ProbeAsync(
        SteamUiPatchContext context, CancellationToken cancellationToken);

    /// <summary>Applies only resources owned by this patch.</summary>
    Task<SteamUiPatchOperationResult> ApplyAsync(
        SteamUiPatchContext context, CancellationToken cancellationToken);

    /// <summary>Functionally verifies the resulting patch state.</summary>
    Task<SteamUiPatchOperationResult> VerifyAsync(
        SteamUiPatchContext context, CancellationToken cancellationToken);

    /// <summary>Removes and verifies removal of only this patch's owned resources.</summary>
    Task<SteamUiPatchOperationResult> RemoveAsync(
        SteamUiPatchContext context, CancellationToken cancellationToken);
}

/// <summary>Sanitized health and compatibility evidence for one registered patch.</summary>
/// <param name="Id">Stable patch id.</param>
/// <param name="Version">Patch version.</param>
/// <param name="Enabled">Whether its individual kill switch permits application.</param>
/// <param name="State">Current lifecycle state.</param>
/// <param name="Fingerprint">Latest positive live fingerprint.</param>
/// <param name="Generations">Generations under which health was assessed.</param>
/// <param name="LastFailure">Latest bounded failure.</param>
/// <param name="LastChangedUtc">Time of the latest state change.</param>
public sealed record SteamUiPatchSnapshot(
    string Id,
    int Version,
    bool Enabled,
    SteamUiPatchState State,
    string? Fingerprint,
    SteamUiGenerations Generations,
    string? LastFailure,
    DateTimeOffset LastChangedUtc);

/// <summary>Serializes patch work, isolates failures, and owns independent kill switches.</summary>
public sealed class SteamUiPatchManager : IAsyncDisposable
{
    private readonly ISteamUiTransport _transport;
    private readonly SortedDictionary<string, PatchEntry> _patches = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SemaphoreSlim> _resourceGates = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _schedulerGate = new(1, 1);
    private bool _globalEnabled = true;
    private int _disposed;

    /// <summary>Creates a registry over the single process-owned Steam UI transport.</summary>
    /// <param name="transport">Persistent Steam UI transport.</param>
    public SteamUiPatchManager(ISteamUiTransport transport)
    {
        _transport = transport;
        _transport.GenerationChanged += OnGenerationChanged;
    }

    /// <summary>Registers a patch before the first synchronization.</summary>
    /// <param name="patch">The independently versioned patch.</param>
    public void Register(ISteamUiPatch patch)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(patch);
        if (patch.Version <= 0
            || string.IsNullOrWhiteSpace(patch.Id)
            || string.IsNullOrWhiteSpace(patch.ResourceKey))
        {
            throw new ArgumentException("Steam UI patch identity, version, and resource are required.");
        }
        if (_patches.ContainsKey(patch.Id))
        {
            throw new InvalidOperationException($"Steam UI patch '{patch.Id}' is already registered.");
        }
        _patches.Add(patch.Id, new PatchEntry(patch));
        _resourceGates.TryAdd(patch.ResourceKey, new SemaphoreSlim(1, 1));
    }

    /// <summary>Enables or disables the global emergency kill switch.</summary>
    /// <param name="enabled">Whether any patch may remain applied.</param>
    public void SetGlobalEnabled(bool enabled) => _globalEnabled = enabled;

    /// <summary>Sets one patch's independent kill switch.</summary>
    /// <param name="patchId">Stable patch id.</param>
    /// <param name="enabled">Whether that patch may be applied.</param>
    public void SetPatchEnabled(string patchId, bool enabled)
    {
        if (!_patches.TryGetValue(patchId, out var entry))
        {
            throw new KeyNotFoundException($"Steam UI patch '{patchId}' is not registered.");
        }

        if (entry.Enabled == enabled)
        {
            return;
        }

        entry.Enabled = enabled;
        entry.Snapshot = entry.Snapshot with
        {
            Enabled = enabled,
            LastChangedUtc = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>Probes, applies, verifies, or retracts every patch independently.</summary>
    /// <param name="cancellationToken">Cancels the synchronization.</param>
    public async Task SynchronizeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _schedulerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var entry in _patches.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await SynchronizePatchAsync(entry, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _schedulerGate.Release();
        }
    }

    /// <summary>Returns immutable health snapshots for diagnostics and UI.</summary>
    public IReadOnlyList<SteamUiPatchSnapshot> GetSnapshots() =>
        _patches.Values.Select(static entry => entry.Snapshot).ToArray();

    private async Task SynchronizePatchAsync(
        PatchEntry entry, CancellationToken cancellationToken)
    {
        var patch = entry.Patch;
        entry.TransportSnapshot = _transport.GetSnapshots()
            .FirstOrDefault(snapshot => snapshot.Role == patch.TargetRole);
        var context = new SteamUiPatchContext(_transport, patch.Bounds);
        var resourceGate = _resourceGates[patch.ResourceKey];
        await resourceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_globalEnabled || !entry.Enabled)
            {
                await RemovePatchAsync(entry, context, cancellationToken).ConfigureAwait(false);
                return;
            }

            entry.Subscription ??= await _transport.SubscribeAsync(
                patch.TargetRole, cancellationToken).ConfigureAwait(false);
            SteamUiPatchProbeResult probe;
            // One timeout per phase, as the bound is documented. A single source spanning probe,
            // apply and verify meant a reachable but slow client that spent most of the budget
            // probing had its otherwise in-budget apply or verification cancelled underneath it,
            // and the patch dropped to Retrying with nothing actually wrong.
            using (CancellationTokenSource probePhase =
                NewPhaseTimeout(cancellationToken, patch.Bounds.OperationTimeout))
            {
                try
                {
                    probe = await patch.ProbeAsync(context, probePhase.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    SetState(entry, SteamUiPatchState.Degraded, null, ex.Message);
                    return;
                }
            }

            if (!probe.TargetPresent)
            {
                SetState(entry, SteamUiPatchState.AbsentTarget, null, probe.Diagnostic);
                return;
            }
            if (!probe.Compatible || !probe.Unique || string.IsNullOrWhiteSpace(probe.Fingerprint))
            {
                SetState(entry, SteamUiPatchState.Incompatible, null,
                    probe.Diagnostic ?? "Patch fingerprint was not a unique positive match.");
                return;
            }

            SetState(entry, SteamUiPatchState.Applying, probe.Fingerprint, null);
            SteamUiPatchOperationResult applied;
            using (CancellationTokenSource applyPhase =
                NewPhaseTimeout(cancellationToken, patch.Bounds.OperationTimeout))
            {
                applied = await patch.ApplyAsync(context, applyPhase.Token).ConfigureAwait(false);
            }

            if (!applied.Succeeded)
            {
                SetState(entry, SteamUiPatchState.Degraded, probe.Fingerprint, applied.Diagnostic);
                return;
            }

            SetState(entry, SteamUiPatchState.Applied, probe.Fingerprint, null);
            SteamUiPatchOperationResult verified;
            using (CancellationTokenSource verifyPhase =
                NewPhaseTimeout(cancellationToken, patch.Bounds.OperationTimeout))
            {
                verified = await patch.VerifyAsync(context, verifyPhase.Token).ConfigureAwait(false);
            }

            if (verified.Succeeded)
            {
                SetState(entry, SteamUiPatchState.Verified, probe.Fingerprint, null);
                return;
            }

            // An applied-but-unverified mutation is not left in the client. It cannot be shown to
            // do what it claims, and leaving it there keeps Valve's own UI replaced by something
            // unproven while later synchronization probes and reapplies over it. Removal restores
            // the native surface; if that cannot be verified either, the patch says so.
            SteamUiLog.Warn(
                $"Steam UI patch {patch.Id} applied but did not verify; removing it: "
                + $"{verified.Diagnostic ?? "no detail"}");
            SteamUiPatchOperationResult removed;
            using (CancellationTokenSource removePhase =
                NewPhaseTimeout(cancellationToken, patch.Bounds.OperationTimeout))
            {
                removed = await patch.RemoveAsync(context, removePhase.Token).ConfigureAwait(false);
            }

            SetState(
                entry,
                removed.Succeeded ? SteamUiPatchState.Degraded : SteamUiPatchState.RemoveFailed,
                probe.Fingerprint,
                removed.Succeeded
                    ? verified.Diagnostic
                    : $"{verified.Diagnostic} Removal also failed: {removed.Diagnostic}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            SetState(entry, SteamUiPatchState.Retrying, entry.Snapshot.Fingerprint,
                "Patch operation timed out.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetState(entry, SteamUiPatchState.Degraded, entry.Snapshot.Fingerprint, ex.Message);
        }
        finally
        {
            resourceGate.Release();
        }
    }

    /// <summary>Creates a cancellation source giving one patch phase its declared budget.</summary>
    /// <param name="cancellationToken">The synchronization's own cancellation.</param>
    /// <param name="timeout">The per-phase bound the patch declared.</param>
    /// <returns>A linked source the caller disposes when the phase ends.</returns>
    private static CancellationTokenSource NewPhaseTimeout(
        CancellationToken cancellationToken,
        TimeSpan timeout)
    {
        CancellationTokenSource phase =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        phase.CancelAfter(timeout);
        return phase;
    }

    private async Task RemovePatchAsync(
        PatchEntry entry,
        SteamUiPatchContext context,
        CancellationToken cancellationToken)
    {
        if (entry.Snapshot.State != SteamUiPatchState.Disabled)
        {
            var removed = await entry.Patch.RemoveAsync(context, cancellationToken)
                .ConfigureAwait(false);
            SetState(entry,
                removed.Succeeded ? SteamUiPatchState.Disabled : SteamUiPatchState.RemoveFailed,
                entry.Snapshot.Fingerprint,
                removed.Succeeded ? null : removed.Diagnostic);
        }
        else
        {
            SetState(entry, SteamUiPatchState.Disabled, entry.Snapshot.Fingerprint, null);
        }
        if (entry.Subscription is not null)
        {
            await entry.Subscription.DisposeAsync().ConfigureAwait(false);
            entry.Subscription = null;
        }
    }

    private void OnGenerationChanged(object? sender, SteamUiTransportSnapshot snapshot)
    {
        foreach (var entry in _patches.Values)
        {
            if (entry.Patch.TargetRole != snapshot.Role)
            {
                continue;
            }
            entry.TransportSnapshot = snapshot;
            if (entry.Snapshot.State is SteamUiPatchState.Applied or SteamUiPatchState.Verified)
            {
                SetState(entry, SteamUiPatchState.Retrying, entry.Snapshot.Fingerprint,
                    "Steam UI generation changed; reapply required.");
            }
        }
    }

    /// <summary>Records a patch's new state and reports the transition.</summary>
    /// <remarks>
    /// The single funnel every outcome of <see cref="SynchronizePatchAsync"/> passes through, which
    /// is why the log line belongs here rather than at the seven call sites.
    /// <para>
    /// This state machine already computed exactly what a remote diagnosis needs — which patch,
    /// whether the target was absent, whether the fingerprint matched, and a bounded diagnostic
    /// saying why — and then put all of it in a snapshot that nothing logged. A native QAM that
    /// never appeared produced no line at all, so "nothing is in Steam's QAM" and "WSGM never tried"
    /// looked identical from a pasted log.
    /// </para>
    /// <para>
    /// Keyed per patch so each one's transitions are tracked independently, and via
    /// <see cref="SteamUiLog.Change"/> because synchronization re-runs on every Steam UI generation and a
    /// steady Verified state would otherwise be the next thing to flood the log.
    /// </para>
    /// </remarks>
    private static void SetState(
        PatchEntry entry,
        SteamUiPatchState state,
        string? fingerprint,
        string? failure)
    {
        var generations = entry.TransportSnapshot?.Generations ?? default;
        entry.Snapshot = new SteamUiPatchSnapshot(
            entry.Patch.Id,
            entry.Patch.Version,
            entry.Enabled,
            state,
            Bound(fingerprint, 512),
            generations,
            Bound(failure, entry.Patch.Bounds.MaximumDiagnosticCharacters),
            DateTimeOffset.UtcNow);

        string detail = string.IsNullOrWhiteSpace(entry.Snapshot.LastFailure)
            ? string.Empty
            : $" — {entry.Snapshot.LastFailure}";
        SteamUiLog.Change(
            "steam.ui.patch." + entry.Patch.Id,
            $"Steam UI patch {entry.Patch.Id} v{entry.Patch.Version}: {state}{detail}",
            warning: state is not (SteamUiPatchState.Applied or SteamUiPatchState.Verified
                or SteamUiPatchState.Applying or SteamUiPatchState.Disabled));
    }

    private static string? Bound(string? value, int maximumLength) =>
        value is null || value.Length <= maximumLength
            ? value
            : value[..maximumLength] + "...";

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _transport.GenerationChanged -= OnGenerationChanged;
        _globalEnabled = false;
        await _schedulerGate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var entry in _patches.Values)
            {
                var context = new SteamUiPatchContext(_transport, entry.Patch.Bounds);
                try
                {
                    using var timeout = new CancellationTokenSource(entry.Patch.Bounds.OperationTimeout);
                    await RemovePatchAsync(entry, context, timeout.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    SetState(entry, SteamUiPatchState.RemoveFailed,
                        entry.Snapshot.Fingerprint, ex.Message);
                }
            }
        }
        finally
        {
            _schedulerGate.Release();
        }
        foreach (var gate in _resourceGates.Values)
        {
            gate.Dispose();
        }
        _schedulerGate.Dispose();
    }

    private sealed class PatchEntry(ISteamUiPatch patch)
    {
        internal ISteamUiPatch Patch { get; } = patch;

        internal bool Enabled { get; set; } = true;

        internal IAsyncDisposable? Subscription { get; set; }

        internal SteamUiTransportSnapshot? TransportSnapshot { get; set; }

        internal SteamUiPatchSnapshot Snapshot { get; set; } = new(
            patch.Id,
            patch.Version,
            true,
            SteamUiPatchState.Unknown,
            null,
            default,
            null,
            DateTimeOffset.UtcNow);
    }
}
