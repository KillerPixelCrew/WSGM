using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Input;
using WSGM.Interop;
using CanonicalButtons = LibHandheld.Contracts.CanonicalButtons;
using PhysicalDeviceIdentity = LibHandheld.Contracts.PhysicalDeviceIdentity;

namespace WSGM.Shell;

/// <summary>
///     The one owner of WSGM's controller management for a session.
/// </summary>
/// <remarks>
///     Everything WSGM does to the controller happens here: the virtual target and its replacement, the
///     haptic return path, hiding the physical pad and the release. <see cref="Forwarding" /> carries the
///     samples, the local UI capture and the source WSGM's own surfaces navigate from. There is
///     deliberately no second policy layer between a setting and this object: the overlay, Settings, and
///     the shared running-application monitor all call it directly.
///     <para>
///         <see cref="DeviceCoordinator" /> owns the plugin lifecycle; this object owns WSGM's virtual
///         controller half, the way HC's <c>ControllerManager</c> and <c>VirtualManager</c> split it.
///         Like HC, the virtual controller and the hidden pad stay up across sleep and restarts; only
///         leaving takes them down.
///     </para>
/// </remarks>
internal sealed class ControllerManager : IAsyncDisposable
{
    /// <summary>What the controller target row says while the physical pad is the target.</summary>
    internal const string NativeDetail = "Steam drives the Steam Deck controller directly.";

    private readonly IControllerTargetBackend _backend;
    private readonly string _controllerReaderApplication;
    private readonly HidHideOwnership _hidHide;
    private readonly ControllerProcessPriority _processPriority;

    private readonly ManagedControllerRouter _router;
    private readonly Lock _stateGate = new();
    private readonly SemaphoreSlim _transition = new(1, 1);

    // Written under the transition gate but read from the plugin's callbacks, which must not take it.
    private volatile bool _disposed;

    private IReadOnlyList<PhysicalDeviceIdentity> _physicalDevices = [];

    private ControllerSelection _selection = new(
        false,
        new ProfileConfig(),
        "Controller management has not started.");

    /// <summary>The published projection: built under <see cref="_stateGate" />, read without it.</summary>
    /// <remarks>
    ///     One immutable record, so a reader never pairs one transition's state with another's target.
    /// </remarks>
    private ControllerManagerStatus _status = new(
        ControllerManagementState.Off,
        null,
        ProfileSource.None,
        null,
        "Controller management has not started.");

    internal ControllerManager(
        IControllerTargetBackend backend,
        IPhysicalHapticSink hapticSink,
        HidHideOwnership hidHide,
        string controllerReaderApplication,
        ControllerProcessPriority processPriority,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(hapticSink);
        ArgumentNullException.ThrowIfNull(hidHide);
        ArgumentNullException.ThrowIfNull(processPriority);
        _processPriority = processPriority;
        _backend = backend;
        _hidHide = hidHide;
        _controllerReaderApplication = controllerReaderApplication;
        _router = new ManagedControllerRouter(backend, hapticSink, timeProvider);
        _router.TargetFaulted += OnRouterTargetFaulted;
        Forwarding = new ControllerForwarding(_router, () => State);
    }

    /// <summary>The route from the plugin's samples to WSGM's own surfaces and the virtual target.</summary>
    internal ControllerForwarding Forwarding { get; }

    /// <summary>Current state of controller management.</summary>
    internal ControllerManagementState State => Volatile.Read(ref _status).State;

    /// <summary>The haptic return path, its calibration and the calibration preview.</summary>
    internal ControllerOutputRouter Rumble => _router.Output;

    /// <summary>Targets the backend on this machine can create, once it has been discovered.</summary>
    /// <remarks>
    ///     Empty until controller management starts, which is also the only time a surface offers the
    ///     choice. Advertising a target the backend cannot build is worse than offering fewer: the
    ///     selection persists, the target creation fails, and management reports itself unavailable.
    /// </remarks>
    internal IReadOnlyList<ManagedControllerTarget> SupportedTargets { get; private set; } = [];

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await DisposeAsync(Deadline.Never).ConfigureAwait(false);
    }

    internal static ControllerManager CreateProduction(string root, IPhysicalHapticSink hapticSink)
    {
        var reader = NativeHidHide.FromDosPath(Environment.ProcessPath
                                               ?? throw new InvalidOperationException(
                                                   "The WSGM executable path is unavailable."));
        if (reader.Skipped is not null)
        {
            Log.Warn($"NT device-path conversion skipped: {reader.Skipped}.");
        }
        else if (reader.Error != 0)
        {
            Log.Warn(
                $"NT device-path conversion failed for {reader.Path[..2]} with Win32 error "
                + $"{reader.Error}; HidHide readability may be unavailable.");
        }

        return new ControllerManager(new ViiperControllerBackend(), hapticSink, HidHideOwnership.ForUser(root),
            reader.Path, new ControllerProcessPriority());
    }

    internal async ValueTask DisposeAsync(Deadline deadline)
    {
        if (_disposed)
        {
            return;
        }

        using var bounded = deadline.CreateCancellationSource();
        var entered = false;
        try
        {
            Forwarding.CloseAdmission();

            try
            {
                // A free gate is taken at once: WaitAsync refuses even a free gate on an expired token,
                // which would skip the target removal for nothing.
                if (!_transition.Wait(0))
                {
                    await _transition.WaitAsync(bounded.Token).ConfigureAwait(false);
                }

                entered = true;
            }
            catch (OperationCanceledException) when (bounded.Token.IsCancellationRequested)
            {
                Log.Warn("Controller disposal: a controller transition was still running at the deadline; "
                         + "the virtual pad is left to process exit.");
            }

            if (entered && _disposed)
            {
                return;
            }

            lock (_stateGate)
            {
                _disposed = true;
                _processPriority.SetActive(false);
                Forwarding.Close();
            }

            _router.TargetFaulted -= OnRouterTargetFaulted;
            if (!entered)
            {
                return;
            }

            try
            {
                await Forwarding.Drain.WaitAsync(bounded.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (bounded.Token.IsCancellationRequested)
            {
                Log.Warn("Controller disposal: the sample drain was still running at the deadline.");
            }

            try
            {
                await DisposeTargetAsync().WaitAsync(bounded.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (bounded.Token.IsCancellationRequested)
            {
                Log.Warn("Controller disposal: the virtual pad was still being removed at the deadline.");
            }
        }
        finally
        {
            lock (_stateGate)
            {
                if (!_disposed)
                {
                    _disposed = true;
                    _processPriority.SetActive(false);
                }

                Forwarding.Close();
            }

            try
            {
                await ShowPhysicalUnderGateAsync(CancellationToken.None).ConfigureAwait(false);
                SetState(ControllerManagementState.Off, "Controller management disposed.");
            }
            finally
            {
                if (entered)
                {
                    _transition.Release();
                }
            }
        }
    }

    /// <summary>Disposes the router and then the backend, in order, as one task a deadline can stop waiting for.</summary>
    /// <returns>A task completing once both are disposed.</returns>
    private async Task DisposeTargetAsync()
    {
        await _router.DisposeAsync().ConfigureAwait(false);
        await _backend.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Reports the projection change a lost target must produce.</summary>
    /// <param name="detail">Why the router faulted.</param>
    /// <remarks>
    ///     The manager must stop reporting Active once the backend stops accepting frames, or WSGM's
    ///     surfaces stay on a managed source that has gone silent.
    /// </remarks>
    private void OnRouterTargetFaulted(string detail)
    {
        Forwarding.MarkGameIdle();
        SetState(ControllerManagementState.Faulted, detail);
        Log.Observe(
            Forwarding.BlockForwardingAsync("source-faulted", CancellationToken.None),
            "Controller source-fault neutralization");
        TargetLost?.Invoke(detail);
    }

    internal event Action<string>? TargetLost;

    /// <summary>Raised when the projection changes, for the overlay and Settings.</summary>
    /// <remarks>Raised on any thread; handlers must not touch UI state directly.</remarks>
    internal event Action<ControllerManagerStatus>? StatusChanged;

    /// <summary>Returns the current projection.</summary>
    /// <returns>The controller-management projection.</returns>
    internal ControllerManagerStatus Snapshot()
    {
        return Volatile.Read(ref _status);
    }

    /// <summary>
    ///     Starts controller management for the current plugin cycle.
    /// </summary>
    /// <param name="selection">The controller selection in effect.</param>
    /// <param name="physicalDevices">Physical devices the plugin owns and WSGM must hide.</param>
    /// <param name="applicationId">Canonical identity of the running application, when known.</param>
    /// <param name="executable">Its executable, when known.</param>
    /// <param name="cancellationToken">Cancels the start.</param>
    /// <returns>The resulting projection.</returns>
    /// <remarks>
    ///     Fails open in every unavailable case. A missing backend, unusable HidHide, or a target that
    ///     does not enumerate shows the physical pad again and leaves the shell, the SDL path, and the
    ///     Steam Input lease exactly as they were. WSGM owns HidHide's cloak: it turns it on here, off
    ///     again on leaving, and never removes an entry it did not add.
    /// </remarks>
    internal async Task<ControllerManagerStatus> StartAsync(
        ControllerSelection selection,
        IReadOnlyList<PhysicalDeviceIdentity> physicalDevices,
        string? applicationId,
        string? executable,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(physicalDevices);
        await _transition.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var status = await StartUnderGateAsync(selection, physicalDevices, applicationId, executable,
                cancellationToken).ConfigureAwait(false);
            if (selection.Enabled && status.State is not ControllerManagementState.Active)
            {
                // Management is on but no virtual controller came up, so nothing would drive a hidden
                // pad. A disabled selection leaves HidHide alone: the release already showed the pad.
                await ShowPhysicalUnderGateAsync(cancellationToken).ConfigureAwait(false);
            }

            return status;
        }
        finally
        {
            _transition.Release();
        }
    }

    private async Task<ControllerManagerStatus> StartUnderGateAsync(
        ControllerSelection selection,
        IReadOnlyList<PhysicalDeviceIdentity> physicalDevices,
        string? applicationId,
        string? executable,
        CancellationToken cancellationToken)
    {
        _physicalDevices = physicalDevices;
        _selection = selection;
        Forwarding.DropPendingSample();

        if (!selection.Enabled)
        {
            return SetState(ControllerManagementState.Off, selection.DisabledDetail);
        }

        var resolved = ControllerTargetSelection.Resolve(
            selection.Profiles,
            applicationId,
            executable);
        if (selection.IsNative(resolved) && _router.Target is null)
        {
            // Nothing is hidden and no target is made; StartAsync shows anything a previous owner hid.
            return await EnterNativeUnderGateAsync(resolved, cancellationToken).ConfigureAwait(false);
        }

        var health = await _backend.DiscoverAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!health.Ready)
        {
            SupportedTargets = [];
            return SetState(ControllerManagementState.Unavailable, health.Detail);
        }

        // What the backend on this machine can actually create: the surfaces offer these and
        // nothing else, because an advertised target the backend has no encoder for reads as a
        // broken feature rather than an unimplemented one.
        SupportedTargets = [.. health.Targets];

        if (!health.Targets.Contains(resolved.Target))
        {
            return SetState(
                ControllerManagementState.Unavailable,
                $"The backend cannot create a {resolved.Target} target.");
        }

        var hidden = await _hidHide.HideAsync(
            _controllerReaderApplication,
            physicalDevices,
            cancellationToken).ConfigureAwait(false);
        if (!hidden.Succeeded)
        {
            return SetState(ControllerManagementState.Unavailable, hidden.Detail);
        }

        try
        {
            await ApplyTargetUnderGateAsync(
                resolved,
                _router.Target is not null,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error("Controller management could not create its virtual target", ex);
            return SetState(ControllerManagementState.Faulted, ex.Message);
        }

        return SetState(
            ControllerManagementState.Active,
            $"Managed target {resolved.Target} is active ({resolved.Source}).");
    }

    /// <summary>
    ///     Applies a changed selection, replacing the target when the effective target changed.
    /// </summary>
    /// <param name="selection">The new controller selection.</param>
    /// <param name="applicationId">Canonical identity of the running application, when known.</param>
    /// <param name="executable">Its executable, when known.</param>
    /// <param name="cancellationToken">Cancels the apply.</param>
    /// <returns>The resulting projection.</returns>
    /// <remarks>
    ///     Turning management off here is not a release and deliberately does not perform one: the
    ///     caller that owns the plugin conversation runs <see cref="ReleaseAsync" /> so the physical
    ///     release is ordered against WSGM's own removal.
    /// </remarks>
    internal async Task<ControllerManagerStatus> ApplySelectionAsync(
        ControllerSelection selection,
        string? applicationId,
        string? executable,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        await _transition.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _selection = selection;
            return await ReconcileTargetUnderGateAsync(applicationId, executable, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _transition.Release();
        }
    }

    /// <summary>
    ///     Applies a running-application change from the one shared monitor.
    /// </summary>
    /// <param name="snapshot">The canonical running-application snapshot.</param>
    /// <param name="cancellationToken">Cancels the apply.</param>
    /// <returns>The resulting projection.</returns>
    /// <remarks>
    ///     The same monitor resolves the RTSS profile, so the controller target and the performance
    ///     profile can never disagree about which application is running.
    /// </remarks>
    internal async Task<ControllerManagerStatus> ApplyRunningApplicationAsync(
        RunningApplicationTargetSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        await _transition.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await ReconcileTargetUnderGateAsync(snapshot.ApplicationId, snapshot.RtssProfileName,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _transition.Release();
        }
    }

    /// <summary>Makes WSGM readable to HidHide before the plugin tries to find the controller.</summary>
    /// <param name="controllerManagementEnabled">Whether management may run at all.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>A task completing once the check has run.</returns>
    /// <remarks>
    ///     Called before the plugin's cycle starts, which is the only point that helps: once discovery
    ///     has run against a device it could not see, allowlisting WSGM afterwards changes nothing for
    ///     that cycle. Never fatal — the result is logged and the cycle continues, because a machine
    ///     with no HidHide at all is the normal one.
    /// </remarks>
    internal async Task EnsureHidHideReadableAsync(
        bool controllerManagementEnabled,
        CancellationToken cancellationToken)
    {
        try
        {
            var detail = await _hidHide.EnsureReadableAsync(
                controllerManagementEnabled,
                _controllerReaderApplication,
                cancellationToken).ConfigureAwait(false);
            Log.Info($"HidHide readability: {detail}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"HidHide readability check failed: {ex.Message}");
        }
    }

    /// <summary>Presses Steam's own button on the virtual pad, so Steam opens its menu or Quick Access.</summary>
    /// <param name="quickAccess">Quick Access rather than the Steam menu.</param>
    /// <param name="cancellationToken">Cancels the press interval.</param>
    /// <returns>Whether the active target took the press.</returns>
    /// <remarks>
    ///     HC injects the button into its virtual controller instead of handing the physical pad to
    ///     Steam. The Deck target has a Quick Access button; the others use Steam's Guide + A chord.
    /// </remarks>
    internal Task<bool> PressSteamButtonAsync(bool quickAccess, CancellationToken cancellationToken)
    {
        var buttons = !quickAccess
            ? CanonicalButtons.Guide
            : Snapshot().Target is ManagedControllerTarget.SteamDeckComposite
                ? CanonicalButtons.QuickAccess
                : CanonicalButtons.Guide | CanonicalButtons.A;
        return Forwarding.PulseAsync(buttons, cancellationToken);
    }

    /// <summary>Lets go of the controller: WSGM's virtual pad goes, and the physical one comes back.</summary>
    /// <param name="scope">Whether only the controller or the whole cycle is being released.</param>
    /// <param name="releasePhysicalAsync">Asks the plugin to stop reading and put its mode back.</param>
    /// <param name="deadline">The caller's deadline for all controller release steps.</param>
    /// <param name="cancellationToken">Cancels waiting for the gates.</param>
    /// <param name="keepPhysicalHidden">
    ///     A fault restart takes the pad again at once, so the pad stays hidden and Steam cannot grab it in
    ///     between; every other release shows it again.
    /// </param>
    /// <param name="faultDetail">
    ///     Set when the release answers a lost target: the release then ends Faulted with this detail
    ///     instead of Off or Idle, so observers see one fault rather than a fault, an idle and a fault.
    /// </param>
    /// <returns>A task completing once every step was attempted.</returns>
    /// <remarks>
    ///     The order is HC's: silence the virtual pad, have the plugin let go, remove the virtual pad, show
    ///     the physical one. Each step is attempted whatever the one before did, and each failure is only
    ///     logged; nothing waits for a readback and nothing is retried.
    /// </remarks>
    internal async Task ReleaseAsync(
        HandoffScope scope,
        Func<CancellationToken, Task> releasePhysicalAsync,
        Deadline deadline,
        CancellationToken cancellationToken,
        bool keepPhysicalHidden = false,
        string? faultDetail = null)
    {
        ArgumentNullException.ThrowIfNull(releasePhysicalAsync);
        using var bounded = deadline.CreateCancellationSource(cancellationToken);
        var entered = false;
        try
        {
            Forwarding.CloseAdmission();

            try
            {
                // A free gate is taken at once: WaitAsync refuses even a free gate on an expired token,
                // which would skip the target removal for nothing.
                if (!_transition.Wait(0))
                {
                    await _transition.WaitAsync(bounded.Token).ConfigureAwait(false);
                }

                entered = true;
            }
            catch (OperationCanceledException)
            {
                Log.Warn("Controller release: another controller transition still held the gate at the "
                         + "caller's deadline; the release steps were skipped.");
                return;
            }

            Forwarding.DropPendingSample();

            // Admission closes before the target is quietened, not after: a sample arriving once the
            // router reaches Neutral would re-activate the source and publish a live report again.
            try
            {
                await Forwarding.NeutralizeTargetAsync("release", bounded.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Warn($"Controller release: the virtual pad could not be silenced: {ex.Message}");
            }

            try
            {
                await releasePhysicalAsync(bounded.Token).WaitAsync(bounded.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Warn($"Controller release: the plugin did not let go cleanly: {ex.Message}");
            }

            try
            {
                await _router.RemoveAsync("release", bounded.Token).WaitAsync(bounded.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Warn($"Controller release: the virtual pad could not be removed: {ex.Message}");
            }
        }
        finally
        {
            try
            {
                if (!keepPhysicalHidden)
                {
                    await ShowPhysicalUnderGateAsync(CancellationToken.None).ConfigureAwait(false);
                }

                if (entered)
                {
                    if (faultDetail is not null)
                    {
                        SetState(ControllerManagementState.Faulted, faultDetail);
                    }
                    else
                    {
                        SetState(
                            scope is HandoffScope.FullDeactivation
                                ? ControllerManagementState.Off
                                : ControllerManagementState.Idle,
                            "Controller management released the controller.");
                    }

                    Log.Info($"Controller released: scope={scope}, physicalKeptHidden={keepPhysicalHidden}.");
                }
            }
            finally
            {
                if (entered)
                {
                    _transition.Release();
                }
            }
        }
    }

    /// <summary>Shows the physical pad again when no virtual controller is driving it.</summary>
    /// <param name="reason">Why, for the log.</param>
    /// <param name="cancellationToken">Cancels waiting for the transition gate or cooperative HidHide cleanup.</param>
    /// <returns>Completion after cleanup is attempted, or immediately when active or disposed. Cleanup failures are logged.</returns>
    /// <remarks>For a fault restart that gave up: the pad it kept hidden must not stay hidden.</remarks>
    internal async Task ShowPhysicalControllerAsync(string reason, CancellationToken cancellationToken)
    {
        try
        {
            await _transition.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            if (_disposed || State is ControllerManagementState.Active)
            {
                return;
            }

            Log.Info($"Showing the physical controller again: {reason}.");
            await ShowPhysicalUnderGateAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _transition.Release();
        }
    }

    internal async Task RecoverPhysicalControllerAsync(string reason, CancellationToken cancellationToken)
    {
        if (await _hidHide.HasOwnershipRecordAsync(cancellationToken).ConfigureAwait(false))
        {
            await ShowPhysicalControllerAsync(reason, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ShowPhysicalUnderGateAsync(CancellationToken cancellationToken)
    {
        try
        {
            var shown = await _hidHide.ShowAsync(cancellationToken).ConfigureAwait(false);
            if (!shown.Succeeded)
            {
                Log.Warn($"Controller HidHide cleanup incomplete: {shown.Detail}");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error("Controller HidHide cleanup failed", ex);
        }
    }

    /// <summary>Reports the physical pad as the target, with no virtual pad and nothing hidden.</summary>
    /// <param name="resolved">The native target as resolved.</param>
    /// <param name="cancellationToken">Cancels the backend query.</param>
    /// <returns>The Native projection.</returns>
    /// <remarks>
    ///     The surfaces still offer the emulated targets, so the backend is asked what it can build once,
    ///     without building anything. The native target is always offered: it needs no backend.
    /// </remarks>
    private async Task<ControllerManagerStatus> EnterNativeUnderGateAsync(
        ResolvedControllerTarget resolved,
        CancellationToken cancellationToken)
    {
        if (SupportedTargets.Count == 0)
        {
            var health = await _backend.DiscoverAsync(cancellationToken).ConfigureAwait(false);
            SupportedTargets = health.Ready ? [.. health.Targets] : [];
        }

        if (!SupportedTargets.Contains(resolved.Target))
        {
            SupportedTargets = [resolved.Target, .. SupportedTargets];
        }

        SetEffective(resolved);
        return SetState(ControllerManagementState.Native, NativeDetail);
    }

    private async Task<ControllerManagerStatus> ReconcileTargetUnderGateAsync(
        string? applicationId,
        string? executable,
        CancellationToken cancellationToken)
    {
        var resolved = ControllerTargetSelection.Resolve(
            _selection.Profiles,
            applicationId,
            executable);
        // The native target needs no plugin conversation once nothing is left to remove: the caller that
        // owns that conversation released the pad first, so only the state follows the selection here.
        if (_router.Target is null)
        {
            if (_selection.IsNative(resolved))
            {
                return await EnterNativeUnderGateAsync(resolved, cancellationToken).ConfigureAwait(false);
            }

            if (State is ControllerManagementState.Native)
            {
                SetEffective(resolved);
                return _selection.Enabled
                    ? SetState(ControllerManagementState.Idle, "Waiting for the handheld to hand over its controller.")
                    : SetState(ControllerManagementState.Off, _selection.DisabledDetail);
            }
        }

        // A disabled selection is not reconciled here. Removing the target without ordering it
        // against the plugin's physical release opens a duplicate-input window, so the caller that
        // owns the plugin conversation runs ReleaseAsync instead.
        if (State is not ControllerManagementState.Active || !_selection.Enabled)
        {
            return Snapshot();
        }

        if (Snapshot().Target == resolved.Target)
        {
            SetEffective(resolved);
            return Snapshot();
        }

        try
        {
            // Replacement is one operation on purpose: the old target is neutralized and removed
            // before the new one is created, so no window exists in which both are enumerated.
            await ApplyTargetUnderGateAsync(resolved, true, cancellationToken)
                .ConfigureAwait(false);
            return SetState(
                ControllerManagementState.Active,
                $"Managed target {resolved.Target} is active ({resolved.Source}).");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error("Managed controller target replacement failed", ex);
            await ShowPhysicalUnderGateAsync(cancellationToken).ConfigureAwait(false);
            return SetState(ControllerManagementState.Faulted, ex.Message);
        }
    }

    private async Task ApplyTargetUnderGateAsync(
        ResolvedControllerTarget resolved,
        bool replace,
        CancellationToken cancellationToken)
    {
        var (target, kept) = await Forwarding.ApplyTargetAsync(
            resolved.Target,
            replace,
            () => SetEffective(resolved),
            cancellationToken).ConfigureAwait(false);
        Log.Info(kept
            ? $"Managed controller target kept: {resolved.Target} ({resolved.Source}), "
              + $"generation={target.Generation}."
            : replace
                ? $"Managed controller target replaced: {resolved.Target} ({resolved.Source}), "
                  + $"generation={target.Generation}."
                : $"Managed controller target created: {resolved.Target} ({resolved.Source}), "
                  + $"generation={target.Generation}, devices={_physicalDevices.Count}.");
    }

    /// <summary>Records the target in effect and the layer that chose it, without raising <see cref="StatusChanged" />.</summary>
    /// <param name="resolved">The resolved target.</param>
    private void SetEffective(ResolvedControllerTarget resolved)
    {
        lock (_stateGate)
        {
            Volatile.Write(ref _status, _status with
            {
                Target = resolved.Target,
                TargetSource = resolved.Source,
                ApplicationId = resolved.ApplicationId
            });
        }
    }

    private ControllerManagerStatus SetState(ControllerManagementState state, string detail)
    {
        ControllerManagerStatus status;
        lock (_stateGate)
        {
            _processPriority.SetActive(!_disposed && state is ControllerManagementState.Active);
            Forwarding.UiPad.SetActive(!_disposed && state is ControllerManagementState.Active);
            status = state is ControllerManagementState.Active or ControllerManagementState.Idle
                or ControllerManagementState.Native
                ? _status with { State = state, Detail = detail }
                : new ControllerManagerStatus(state, null, ProfileSource.None, null, detail);
            Volatile.Write(ref _status, status);
        }

        StatusChanged?.Invoke(status);
        return status;
    }
}
