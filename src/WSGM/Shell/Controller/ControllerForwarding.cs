using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Input;
using CanonicalButtons = LibHandheld.Contracts.CanonicalButtons;
using CanonicalControllerSample = LibHandheld.Contracts.CanonicalControllerSample;

namespace WSGM.Shell;

/// <summary>
///     Carries the plugin's canonical samples to WSGM's own surfaces and, when forwarding is open, to
///     the virtual target.
/// </summary>
/// <remarks>
///     Owns the one sample drain, the managed UI pad, the local UI capture, the forwarding-blocked
///     state, synthetic button presses and the route gate that orders every live sample against the
///     neutral packets and target swaps that must precede it. <see cref="ControllerManager" /> owns
///     the target lifecycle and the state this route reads; it closes the route when it is disposed.
/// </remarks>
internal sealed class ControllerForwarding
{
    /// <summary>How long a synthetic press is held: HC's <c>KeyPressDelay</c>.</summary>
    private static readonly TimeSpan SyntheticPressInterval = TimeSpan.FromMilliseconds(200);

    private readonly Lock _gate = new();

    /// <summary>Serializes routing a sample against the neutralizations that must precede it.</summary>
    /// <remarks>
    ///     A lock cannot do this: the publication it protects is asynchronous, and a route decided
    ///     under <see cref="_gate" /> but published outside it can land a stale live sample on top
    ///     of the neutral packet a capture claim just wrote.
    /// </remarks>
    private readonly SemaphoreSlim _routeGate = new(1, 1);

    private readonly ManagedControllerRouter _router;

    private readonly Channel<CanonicalControllerSample> _samples = Channel.CreateBounded<CanonicalControllerSample>(
        new BoundedChannelOptions(1)
            { FullMode = BoundedChannelFullMode.DropOldest, AllowSynchronousContinuations = false });

    private readonly Func<ControllerManagementState> _state;
    private readonly UiCaptureState _uiCapture = new();

    // Set under the gate but read from the plugin's sample callback, which must not take it.
    private volatile bool _closed;
    private bool _forwardingBlocked;
    private bool _gameLive;

    private CanonicalButtons _lastButtons;
    private CanonicalControllerSample? _lastSample;
    private CanonicalButtons _syntheticButtons;

    /// <summary>Starts the sample drain over the manager's router.</summary>
    /// <param name="router">The manager's virtual-target router; borrowed, not disposed here.</param>
    /// <param name="state">Reads the manager's current management state without locking.</param>
    internal ControllerForwarding(ManagedControllerRouter router, Func<ControllerManagementState> state)
    {
        _router = router;
        _state = state;
        Drain = DrainSamplesAsync();
    }

    /// <summary>The managed controller as WSGM's own surfaces read it.</summary>
    /// <remarks>
    ///     Active only while a target is being driven; in every other state the UI reads SDL with the
    ///     Steam Input lease, which is why that path stays a permanent capability.
    /// </remarks>
    internal ManagedUiPad UiPad { get; } = new();

    /// <summary>Gets the sample drain, which completes once <see cref="Close" /> has run and the last sample is routed.</summary>
    internal Task Drain { get; }

    /// <summary>Optional owner-provided OEM translation; physical/UI observers retain the original sample.</summary>
    internal Func<CanonicalControllerSample, CanonicalButtons>? FilterOemButtons { get; set; }

    /// <summary>Every physical sample, for diagnostics only.</summary>
    /// <remarks>Raised before routing and never used to drive input.</remarks>
    internal event Action<CanonicalControllerSample>? PhysicalSampleObserved;

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
        if (!_closed)
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
            CanonicalButtons synthetic;
            lock (_gate)
            {
                if (_closed)
                {
                    return false;
                }

                // Synthetic presses change only under the route gate held here, so this read stays current.
                synthetic = _syntheticButtons;
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

            var buttons = FilterOemButtons?.Invoke(sample) ?? sample.Buttons;
            var routed = synthetic is CanonicalButtons.None && buttons == sample.Buttons
                ? sample
                : sample with { Buttons = buttons | synthetic };
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
        lock (_gate)
        {
            started = _uiCapture.Claim(surfaceId, _lastButtons);
            if (started)
            {
                _gameLive = false;
            }
        }

        return started
            ? NeutralizeRoutingAsync("ui-capture", blockForwarding: false, cancellationToken)
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
        lock (_gate)
        {
            _uiCapture.Release(surfaceId);
        }
    }

    /// <summary>Keeps controls from the game until each is released, as the controls of a closed surface are.</summary>
    /// <param name="buttons">Controls to withhold.</param>
    /// <remarks>Called from <see cref="PhysicalSampleObserved" />, it withholds the sample being routed.</remarks>
    internal void WithholdUntilReleased(CanonicalButtons buttons)
    {
        lock (_gate)
        {
            _uiCapture.WithholdUntilReleased(buttons);
        }
    }

    /// <summary>Stops game forwarding until a target is successfully created or replaced.</summary>
    /// <param name="reason">Diagnostic reason recorded with the neutral report.</param>
    /// <param name="cancellationToken">Cancels the neutralization.</param>
    /// <returns>A task completing once the target has been left neutral.</returns>
    internal Task BlockForwardingAsync(string reason, CancellationToken cancellationToken)
    {
        return NeutralizeRoutingAsync(reason, blockForwarding: true, cancellationToken);
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
            lock (_gate)
            {
                if (!_forwardingBlocked || _state() is not ControllerManagementState.Active)
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
            lock (_gate)
            {
                var newlyBlocked = blockForwarding && !_forwardingBlocked;
                _forwardingBlocked |= blockForwarding;
                _gameLive = false;
                neutralize = _state() is ControllerManagementState.Active
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

        return PulseAsync(pressed, cancellationToken);
    }

    /// <summary>Holds buttons on the virtual pad for HC's key press interval, then releases them.</summary>
    /// <param name="pressed">The buttons to hold.</param>
    /// <param name="cancellationToken">Cancels the press interval; the release is still published.</param>
    /// <returns>Whether the active target took the press.</returns>
    internal async Task<bool> PulseAsync(CanonicalButtons pressed, CancellationToken cancellationToken)
    {
        try
        {
            if (!await SetSyntheticButtonAsync(pressed, enabled: true, cancellationToken).ConfigureAwait(false))
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
            await SetSyntheticButtonAsync(pressed, enabled: false, CancellationToken.None)
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
            lock (_gate)
            {
                if (!enabled)
                {
                    _syntheticButtons &= ~button;
                }

                if (_closed || _state() is not ControllerManagementState.Active)
                {
                    Log.Warn(
                        $"Virtual button {(enabled ? "press" : "release")} refused: "
                        + $"controllerState={_state()}.");
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

    /// <summary>Closes game forwarding at once, ahead of a release or disposal that waits for the transition gate.</summary>
    internal void CloseAdmission()
    {
        lock (_gate)
        {
            _forwardingBlocked = true;
            _gameLive = false;
        }
    }

    /// <summary>Records that the game no longer receives live samples, as when the target was lost.</summary>
    internal void MarkGameIdle()
    {
        lock (_gate)
        {
            _gameLive = false;
        }
    }

    /// <summary>Drops a sample still waiting for the drain, so a transition starts from the next one.</summary>
    internal void DropPendingSample()
    {
        _samples.Reader.TryRead(out _);
    }

    /// <summary>Stops routing and completes the drain's input. Idempotent.</summary>
    /// <remarks>Called under the manager's state lock when it is disposed; takes only this route's own lock.</remarks>
    internal void Close()
    {
        lock (_gate)
        {
            _closed = true;
        }

        _samples.Writer.TryComplete();
    }

    /// <summary>Leaves the virtual target neutral under the route gate, so no live sample lands after it.</summary>
    /// <param name="reason">Diagnostic reason recorded with the neutral report.</param>
    /// <param name="cancellationToken">Bounds both the gate wait and the neutralization.</param>
    /// <returns>A task completing once the target has been neutralized.</returns>
    internal async Task NeutralizeTargetAsync(string reason, CancellationToken cancellationToken)
    {
        await _routeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _router.NeutralizeAsync(reason, cancellationToken).WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _routeGate.Release();
        }
    }

    /// <summary>Creates, replaces or keeps the virtual target under the route gate, then reopens forwarding.</summary>
    /// <param name="target">The target kind to drive.</param>
    /// <param name="replace">Whether an existing target is replaced rather than a first one created.</param>
    /// <param name="applied">Records the target in effect, under the route gate, before forwarding reopens.</param>
    /// <param name="cancellationToken">Cancels the gate wait and the backend operation.</param>
    /// <returns>The target now driven and whether the existing one was kept.</returns>
    /// <remarks>
    ///     A target of the right kind is kept, as HC keeps its virtual controller across sleep and
    ///     reconnects: recreating it made Steam see the pad unplug and re-attach on every wake. A failed
    ///     create or replace throws and leaves forwarding as it was.
    /// </remarks>
    internal async Task<(ControllerTargetHandle Target, bool Kept)> ApplyTargetAsync(
        ManagedControllerTarget target,
        bool replace,
        Action applied,
        CancellationToken cancellationToken)
    {
        await _routeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var kept = _router.Target is { } existing && existing.Kind == target;
            if (replace && !kept)
            {
                lock (_gate)
                {
                    _forwardingBlocked = true;
                }
            }

            var handle = kept
                ? _router.Target!
                : replace
                    ? await _router.ReplaceAsync(target, cancellationToken)
                        .ConfigureAwait(false)
                    : await _router.CreateAsync(target, cancellationToken)
                        .ConfigureAwait(false);
            applied();
            bool captured;
            lock (_gate)
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

            return (handle, kept);
        }
        finally
        {
            _routeGate.Release();
        }
    }
}
