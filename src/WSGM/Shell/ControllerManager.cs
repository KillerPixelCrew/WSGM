using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Input;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Input;
using WSGM.Interop;

namespace WSGM.Shell;

/// <summary>Truthful state of WSGM's controller management for one session.</summary>
internal enum ControllerManagementState
{
    /// <summary>The user has not enabled controller management, or the release gate is closed.</summary>
    Off,

    /// <summary>Enabled, but no usable backend exists on this machine.</summary>
    Unavailable,

    /// <summary>Enabled and ready, with no virtual target present.</summary>
    Idle,

    /// <summary>A virtual target exists and canonical samples reach it.</summary>
    Active,

    /// <summary>Management faulted for this run; input falls back to SDL and the Steam lease.</summary>
    Faulted
}

/// <summary>The complete controller-management projection consumed by the overlay and diagnostics.</summary>
internal sealed record ControllerManagerStatus(
    ControllerManagementState State,
    ManagedControllerTarget? Target,
    ProfileSource TargetSource,
    string? ApplicationId,
    string Detail);

/// <summary>
///     The one owner of WSGM's controller management for a session.
/// </summary>
/// <remarks>
///     Everything WSGM does to the controller happens here: the virtual target and its replacement, the
///     haptic return path, hiding the physical pad, the local UI capture, the source WSGM's own surfaces
///     navigate from, and the release. There is deliberately no second policy layer between a setting
///     and this object: the overlay, Settings, and the shared running-application monitor all call it
///     directly.
///     <para>
///         <see cref="DeviceCoordinator" /> owns the plugin lifecycle; this object owns WSGM's virtual
///         controller half, the way HC's <c>ControllerManager</c> and <c>VirtualManager</c> split it.
///         Like HC, the virtual controller and the hidden pad stay up across sleep and restarts; only
///         leaving takes them down.
///     </para>
/// </remarks>
internal sealed class ControllerManager : IAsyncDisposable
{
    internal static ControllerManager CreateProduction(string root, IPhysicalHapticSink hapticSink)
    {
        return new ControllerManager(new ViiperControllerBackend(), hapticSink, HidHideOwnership.ForUser(root),
            NativeHidHide.FromDosPath(Environment.ProcessPath
                ?? throw new InvalidOperationException("The WSGM executable path is unavailable.")),
            new ControllerProcessPriority());
    }

    /// <summary>How long a synthetic press is held: HC's <c>KeyPressDelay</c>.</summary>
    private static readonly TimeSpan SyntheticPressInterval = TimeSpan.FromMilliseconds(200);

    private readonly IControllerTargetBackend _backend;
    private readonly string _controllerReaderApplication;
    private readonly HidHideOwnership _hidHide;
    private readonly ControllerProcessPriority _processPriority;

    /// <summary>Serializes routing a sample against the neutralizations that must precede it.</summary>
    /// <remarks>
    ///     A lock cannot do this: the publication it protects is asynchronous, and a route decided
    ///     under <see cref="_stateGate" /> but published outside it can land a stale live sample on top
    ///     of the neutral packet a capture claim just wrote.
    /// </remarks>
    private readonly SemaphoreSlim _routeGate = new(1, 1);

    private readonly ManagedControllerRouter _router;
    private readonly Channel<CanonicalControllerSample> _samples = Channel.CreateBounded<CanonicalControllerSample>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, AllowSynchronousContinuations = false });
    private readonly Task _sampleDrain;
    private readonly Lock _stateGate = new();
    private readonly SemaphoreSlim _transition = new(1, 1);
    private readonly UiCaptureState _uiCapture = new();

    // Written under the transition gate but read from the sample path, which must not take it.
    private volatile bool _disposed;
    private bool _forwardingBlocked;
    private bool _gameLive;

    private CanonicalButtons _lastButtons;
    private CanonicalControllerSample? _lastSample;

    private IReadOnlyList<PhysicalDeviceIdentity> _physicalDevices = [];

    private ControllerSelection _selection = new(
        false,
        new ProfileConfig(),
        "Controller management has not started.");

    private CanonicalButtons _syntheticButtons;

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
        _sampleDrain = DrainSamplesAsync();
    }

    /// <summary>Current state of controller management.</summary>
    internal ControllerManagementState State { get; private set; } = ControllerManagementState.Off;

    /// <summary>Why the current state holds, for logs and the overlay.</summary>
    private string Detail { get; set; } = "Controller management has not started.";

    /// <summary>The managed controller as WSGM's own surfaces read it.</summary>
    /// <remarks>
    ///     Active only while a target is being driven; in every other state the UI reads SDL with the
    ///     Steam Input lease, which is why that path stays a permanent capability.
    /// </remarks>
    internal ManagedUiPad UiPad { get; } = new();

    /// <summary>The target in effect and the layer that chose it.</summary>
    private ResolvedControllerTarget? Effective { get; set; }

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
            lock (_stateGate)
            {
                _forwardingBlocked = true;
                _gameLive = false;
            }

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
            }

            _router.TargetFaulted -= OnRouterTargetFaulted;
            _samples.Writer.TryComplete();
            if (!entered)
            {
                return;
            }

            try
            {
                await _sampleDrain.WaitAsync(bounded.Token).ConfigureAwait(false);
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
            }

            _samples.Writer.TryComplete();
            await ShowPhysicalUnderGateAsync(CancellationToken.None).ConfigureAwait(false);
            SetState(ControllerManagementState.Off, "Controller management disposed.");
            if (entered)
            {
                _transition.Release();
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
        lock (_stateGate)
        {
            _gameLive = false;
        }
        SetState(ControllerManagementState.Faulted, detail);
        Log.Observe(
            BlockForwardingAsync("source-faulted", CancellationToken.None),
            "Controller source-fault neutralization");
        TargetLost?.Invoke(detail);
    }

    internal event Action<string>? TargetLost;

    internal void ReportTargetFault(string detail)
    {
        SetState(ControllerManagementState.Faulted, detail);
    }

    /// <summary>Raised when the projection changes, for the overlay and Settings.</summary>
    internal event Action<ControllerManagerStatus>? StatusChanged;

    /// <summary>Every physical sample, for diagnostics only.</summary>
    /// <remarks>Raised before routing and never used to drive input.</remarks>
    internal event Action<CanonicalControllerSample>? PhysicalSampleObserved;

    /// <summary>Returns the current projection.</summary>
    /// <returns>The controller-management projection.</returns>
    internal ControllerManagerStatus Snapshot()
    {
        return new ControllerManagerStatus(
            State,
            Effective?.Target,
            Effective?.Source ?? ProfileSource.None,
            Effective?.ApplicationId,
            Detail);
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
        _samples.Reader.TryRead(out _);

        if (!selection.Enabled)
        {
            return SetState(ControllerManagementState.Off, selection.DisabledDetail);
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

        var resolved = ControllerTargetSelection.Resolve(
            selection.Profiles,
            applicationId,
            executable);
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

    /// <summary>
    ///     Forwards one canonical sample published by the plugin.
    /// </summary>
    /// <param name="sample">The sample the plugin published.</param>
    /// <remarks>
    ///     A captured sample never reaches the virtual target. WSGM's own surfaces read every sample
    ///     through <see cref="UiPad" />, whose edge detection keeps the chord that opened the overlay
    ///     from activating whatever now has focus underneath it.
    /// </remarks>
    internal void Submit(CanonicalControllerSample sample)
    {
        if (!_disposed)
        {
            // Only the newest full state matters. Observers run on the drain, never the HID reader.
            _samples.Writer.TryWrite(sample);
        }
    }

    private static void LogRouteFault(Exception ex)
    {
        Log.Change(
            "controller-sample-route-fault",
            $"Controller sample route recovered after {ex.GetType().Name}: {ex.Message}");
    }

    private async Task DrainSamplesAsync()
    {
        while (await _samples.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            if (!_samples.Reader.TryRead(out var sample))
            {
                continue;
            }

            try
            {
                await RouteAsync(sample, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LogRouteFault(ex);
            }
        }
    }

    /// <summary>Routes one canonical sample and reports whether it reached the virtual target.</summary>
    /// <param name="sample">The sample the plugin published.</param>
    /// <param name="cancellationToken">Cancels the route.</param>
    /// <returns><see langword="true" /> when the sample reached the virtual target.</returns>
    internal async Task<bool> RouteAsync(
        CanonicalControllerSample sample,
        CancellationToken cancellationToken)
    {
        // Raised before any routing decision, because this is what the plugin reported. Read-only: an
        // observer cannot change what is routed, so it is not a second input path.
        PhysicalSampleObserved?.Invoke(sample);
        UiPad.Publish(sample);

        // Held across the decision and the publication it authorizes. Every neutralization takes
        // the same gate, so a live sample can no longer be written after the neutral packet that
        // was meant to replace it.
        await _routeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            bool toUi;
            lock (_stateGate)
            {
                if (_disposed)
                {
                    return false;
                }

                _lastButtons = sample.Buttons;
                _lastSample = sample;
                // Forwarding resumes only on a clean boundary: every control the UI used has to be
                // released first, or the game sees a press whose start it never saw.
                toUi = _uiCapture.Withholds(sample.Buttons) || _forwardingBlocked;
                _gameLive = !toUi;
            }

            if (toUi)
            {
                return false;
            }

            CanonicalControllerSample routed;
            lock (_stateGate)
            {
                routed = _syntheticButtons is CanonicalButtons.None
                    ? sample
                    : sample with { Buttons = sample.Buttons | _syntheticButtons };
            }

            return await _router.RouteAsync(routed, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _routeGate.Release();
        }
    }

    /// <summary>Claims controller input for one WSGM surface.</summary>
    /// <param name="surfaceId">Identifier of the claiming surface.</param>
    /// <param name="cancellationToken">Cancels the claim.</param>
    /// <returns>A task completing once the target has been left neutral.</returns>
    internal Task ClaimUiAsync(string surfaceId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(surfaceId);
        bool started;
        lock (_stateGate)
        {
            started = _uiCapture.Claim(surfaceId, _lastButtons);
            if (started)
            {
                _gameLive = false;
            }
        }

        return started
            ? NeutralizeForUiCaptureAsync(cancellationToken)
            : Task.CompletedTask;
    }

    /// <summary>Releases one surface's claim on controller input.</summary>
    /// <param name="surfaceId">Identifier of the releasing surface.</param>
    /// <remarks>
    ///     Releasing the last claim does not resume forwarding by itself. Forwarding resumes on the
    ///     first sample in which every control the UI used is up, so the press that closed the surface
    ///     never arrives in the game as a fresh input.
    /// </remarks>
    internal void ReleaseUi(string surfaceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(surfaceId);
        lock (_stateGate)
        {
            _uiCapture.Release(surfaceId);
        }
    }

    /// <summary>Stops game forwarding until a target is successfully created or replaced.</summary>
    /// <param name="reason">Diagnostic reason recorded with the neutral report.</param>
    /// <param name="cancellationToken">Cancels the neutralization.</param>
    /// <returns>A task completing once the target has been left neutral.</returns>
    internal Task BlockForwardingAsync(string reason, CancellationToken cancellationToken)
    {
        return NeutralizeRoutingAsync(reason, true, cancellationToken);
    }

    /// <summary>Lets samples reach the kept target again after a sleep or a session lock.</summary>
    /// <param name="reason">Why, for the log.</param>
    /// <param name="cancellationToken">Cancels waiting for the route gate.</param>
    /// <returns>A task completing once forwarding is open.</returns>
    /// <remarks>
    ///     HC's <c>SetSystemSleepState(false)</c>: the wake itself reopens forwarding, whether or not the
    ///     plugin republished its pad. The first sample then re-arms the neutral target, and a control
    ///     the UI still holds is withheld until it is released as usual.
    /// </remarks>
    internal async Task ResumeForwardingAsync(string reason, CancellationToken cancellationToken)
    {
        await _routeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_stateGate)
            {
                if (!_forwardingBlocked || State is not ControllerManagementState.Active)
                {
                    return;
                }

                _forwardingBlocked = false;
            }

            Log.Info($"Controller forwarding resumed: {reason}.");
        }
        finally
        {
            _routeGate.Release();
        }
    }

    private Task NeutralizeForUiCaptureAsync(CancellationToken cancellationToken)
    {
        return NeutralizeRoutingAsync("ui-capture", false, cancellationToken);
    }

    private async Task NeutralizeRoutingAsync(
        string reason,
        bool blockForwarding,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        // Under the route gate so forwarding closes and the target is neutralized without a sample
        // decided a moment earlier landing between the two.
        await _routeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            bool neutralize;
            lock (_stateGate)
            {
                var newlyBlocked = blockForwarding && !_forwardingBlocked;
                _forwardingBlocked |= blockForwarding;
                _gameLive = false;
                neutralize = State is ControllerManagementState.Active
                             && (newlyBlocked || !blockForwarding);
            }

            if (neutralize)
            {
                await _router.NeutralizeAsync(reason, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _routeGate.Release();
        }
    }

    /// <summary>Sends one bounded rear-button pulse through the active virtual target.</summary>
    /// <param name="button">One-based rear-button number.</param>
    /// <param name="cancellationToken">Cancels the press interval.</param>
    /// <returns>Whether an active target and source sample accepted the pulse.</returns>
    internal Task<bool> PulseRearButtonAsync(
        int button,
        CancellationToken cancellationToken)
    {
        var pressed = button switch
        {
            1 => CanonicalButtons.RearPaddle1,
            2 => CanonicalButtons.RearPaddle2,
            _ => CanonicalButtons.None
        };
        if (pressed is CanonicalButtons.None)
        {
            Log.Warn($"Virtual rear-button pulse refused: unsupported button={button}.");
            return Task.FromResult(false);
        }

        return PulseButtonsAsync(pressed, cancellationToken);
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
            : Effective?.Target is ManagedControllerTarget.SteamDeckComposite
                ? CanonicalButtons.QuickAccess
                : CanonicalButtons.Guide | CanonicalButtons.A;
        return PulseButtonsAsync(buttons, cancellationToken);
    }

    /// <summary>Holds buttons on the virtual pad for HC's key press interval, then releases them.</summary>
    private async Task<bool> PulseButtonsAsync(CanonicalButtons pressed, CancellationToken cancellationToken)
    {
        try
        {
            if (!await SetSyntheticButtonAsync(pressed, true, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            await Task.Delay(SyntheticPressInterval, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            // A cancelled action must still publish the release; otherwise the virtual target keeps
            // the button held until the next physical sample happens to arrive.
            await SetSyntheticButtonAsync(pressed, false, CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private async Task<bool> SetSyntheticButtonAsync(
        CanonicalButtons button,
        bool enabled,
        CancellationToken cancellationToken)
    {
        await _routeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CanonicalControllerSample sample;
            lock (_stateGate)
            {
                if (!enabled)
                {
                    _syntheticButtons &= ~button;
                }

                if (_disposed || State is not ControllerManagementState.Active)
                {
                    Log.Warn(
                        $"Virtual button {(enabled ? "press" : "release")} refused: "
                        + $"controllerState={State}.");
                    return false;
                }

                if (!_gameLive || _forwardingBlocked || _uiCapture.IsCaptured)
                {
                    Log.Warn("Virtual button refused: reason=forwarding-closed.");
                    return false;
                }

                if (_lastSample is not { } last)
                {
                    Log.Warn(
                        $"Virtual button {(enabled ? "press" : "release")} refused: "
                        + "no canonical controller sample has arrived.");
                    return false;
                }

                if (enabled)
                {
                    _syntheticButtons |= button;
                }

                sample = last with { Buttons = last.Buttons | _syntheticButtons };
            }

            return await _router.RouteAsync(sample, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _routeGate.Release();
        }
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
        bool keepPhysicalHidden = false)
    {
        ArgumentNullException.ThrowIfNull(releasePhysicalAsync);
        using var bounded = deadline.CreateCancellationSource(cancellationToken);
        var entered = false;
        try
        {
            lock (_stateGate)
            {
                _forwardingBlocked = true;
                _gameLive = false;
            }

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

            _samples.Reader.TryRead(out _);

            // Admission closes before the target is quietened, not after: a sample arriving once the
            // router reaches Neutral would re-activate the source and publish a live report again.
            var routeEntered = false;
            try
            {
                await _routeGate.WaitAsync(bounded.Token).ConfigureAwait(false);
                routeEntered = true;
                await _router.NeutralizeAsync("release", bounded.Token).WaitAsync(bounded.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Warn($"Controller release: the virtual pad could not be silenced: {ex.Message}");
            }
            finally
            {
                if (routeEntered)
                {
                    _routeGate.Release();
                }
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
            if (!keepPhysicalHidden)
            {
                await ShowPhysicalUnderGateAsync(CancellationToken.None).ConfigureAwait(false);
            }

            if (entered)
            {
                SetState(
                    scope is HandoffScope.FullDeactivation
                        ? ControllerManagementState.Off
                        : ControllerManagementState.Idle,
                    "Controller management released the controller.");
                Log.Info($"Controller released: scope={scope}, physicalKeptHidden={keepPhysicalHidden}.");
                _transition.Release();
            }
        }
    }

    /// <summary>Shows the physical pad again when no virtual controller is driving it.</summary>
    /// <param name="reason">Why, for the log.</param>
    /// <param name="cancellationToken">Cancels waiting for the transition gate.</param>
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

    private async Task<ControllerManagerStatus> ReconcileTargetUnderGateAsync(
        string? applicationId,
        string? executable,
        CancellationToken cancellationToken)
    {
        var resolved = ControllerTargetSelection.Resolve(
            _selection.Profiles,
            applicationId,
            executable);
        // A disabled selection is not reconciled here. Removing the target without ordering it
        // against the plugin's physical release opens a duplicate-input window, so the caller that
        // owns the plugin conversation runs ReleaseAsync instead.
        if (State is not ControllerManagementState.Active || !_selection.Enabled)
        {
            return Snapshot();
        }

        if (Effective is { } current && current.Target == resolved.Target)
        {
            Effective = resolved;
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
        await _routeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A target of the right kind is kept, as HC keeps its virtual controller across sleep and
            // reconnects: recreating it made Steam see the pad unplug and re-attach on every wake.
            var kept = _router.Target is { } existing && existing.Kind == resolved.Target;
            if (replace && !kept)
            {
                lock (_stateGate)
                {
                    _forwardingBlocked = true;
                }
            }

            var target = kept
                ? _router.Target!
                : replace
                    ? await _router.ReplaceAsync(resolved.Target, cancellationToken)
                        .ConfigureAwait(false)
                    : await _router.CreateAsync(resolved.Target, cancellationToken)
                        .ConfigureAwait(false);
            Effective = resolved;
            bool captured;
            lock (_stateGate)
            {
                _forwardingBlocked = false;
                captured = _uiCapture.IsCaptured;
                _gameLive = !captured;
            }

            if (captured)
            {
                await _router.NeutralizeAsync("ui-capture", cancellationToken)
                    .ConfigureAwait(false);
            }
            Log.Info(kept
                ? $"Managed controller target kept: {resolved.Target} ({resolved.Source}), "
                  + $"generation={target.Generation}."
                : replace
                    ? $"Managed controller target replaced: {resolved.Target} ({resolved.Source}), "
                      + $"generation={target.Generation}."
                    : $"Managed controller target created: {resolved.Target} ({resolved.Source}), "
                      + $"generation={target.Generation}, devices={_physicalDevices.Count}.");
        }
        finally
        {
            _routeGate.Release();
        }
    }

    private ControllerManagerStatus SetState(ControllerManagementState state, string detail)
    {
        lock (_stateGate)
        {
            State = state;
            Detail = detail;
            _processPriority.SetActive(!_disposed && state is ControllerManagementState.Active);
            UiPad.SetActive(!_disposed && state is ControllerManagementState.Active);
            if (state is not (ControllerManagementState.Active or ControllerManagementState.Idle))
            {
                Effective = null;
            }
        }

        var status = Snapshot();
        StatusChanged?.Invoke(status);
        return status;
    }
}
