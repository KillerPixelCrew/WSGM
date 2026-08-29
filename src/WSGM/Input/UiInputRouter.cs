using System;
using WSGM.Device.Sdk.Input;

namespace WSGM.Input;

/// <summary>
/// The one source WSGM's own navigation subscribes to, whichever source is actually delivering.
/// </summary>
/// <remarks>
/// Every navigation surface used to subscribe to <see cref="GamepadService"/> directly, which meant
/// they could only ever see what SDL sees. On a handheld that is not enough: SDL has no rear
/// paddles, no Quick Access button and no trackpad clicks, so WSGM's own UI could not be driven by
/// half the device's controls even while the plugin was reporting them.
/// <para>
/// The switch is make-before-break, and the hard part is not the swap. It is the buttons held across
/// it: without explicit handling, a control held while the source changes produces a press edge on
/// the new source that the user never made, or a release that never arrives and leaves the control
/// latched. <see cref="SourceArbitration"/> owns that policy; this owns the plumbing.
/// </para>
/// <para>
/// SDL stays running throughout rather than being stopped when the managed source takes over. It is
/// what the fallback returns to, and a source that has been stopped cannot be shown to be healthy
/// before the switch that needs it.
/// </para>
/// </remarks>
public sealed class UiInputRouter : IUiButtonSource, IDisposable
{
    private readonly IUiButtonSource _fallback;
    private readonly TimeProvider _time;
    private readonly CanonicalButtonSource _managed = new();
    private UiInputSource _current = UiInputSource.SdlWithSteamLease;
    private GamepadButtons _suppressed;
    private DateTimeOffset _switchedAt;
    private bool _managedHealthy;
    private bool _disposed;

    /// <summary>Creates the router over the always-present fallback source.</summary>
    /// <param name="fallback">The SDL source, which stays subscribed for the whole session.</param>
    /// <param name="timeProvider">Clock used to bound held-control suppression.</param>
    public UiInputRouter(IUiButtonSource fallback, TimeProvider? timeProvider = null)
    {
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
        _time = timeProvider ?? TimeProvider.System;
        _fallback.ButtonPressed += OnFallbackPressed;
    }

    /// <inheritdoc/>
    public event Action<GamepadButtons>? ButtonPressed;

    /// <summary>Which source WSGM's navigation is currently being driven by.</summary>
    public UiInputSource Current => _current;

    /// <summary>Feeds one canonical sample from the plugin.</summary>
    /// <param name="sample">The sample the plugin published.</param>
    /// <remarks>
    /// The first sample is what makes the managed source healthy, which is the condition
    /// <see cref="SourceArbitration.Decide"/> requires before the fallback is dropped: switching on
    /// "a managed source exists" rather than "it is delivering" leaves a gap in which nothing is
    /// delivering and the UI appears frozen.
    /// </remarks>
    public void Submit(CanonicalControllerSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        if (_disposed)
        {
            return;
        }

        if (!_managedHealthy)
        {
            _managedHealthy = true;
            BeginSwitch(UiInputSource.ManagedCanonical);
        }

        GamepadButtons held = CanonicalButtonSource.Translate(sample);
        if (_current is not UiInputSource.ManagedCanonical)
        {
            // Still tracked while the fallback is current, so the state is already correct at the
            // moment a later switch happens rather than starting from nothing.
            _managed.Submit(sample);
            return;
        }

        ReleaseSuppressed(held);
        _managed.Submit(sample);
    }

    /// <summary>Reports that the managed source has stopped delivering.</summary>
    /// <remarks>
    /// Called when controller management stops or faults. The fallback is already subscribed and
    /// running, so this is a break-after-make in the other direction and cannot leave a gap.
    /// </remarks>
    public void ManagedSourceLost()
    {
        if (_disposed || !_managedHealthy)
        {
            return;
        }

        _managedHealthy = false;
        BeginSwitch(UiInputSource.SdlWithSteamLease);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _fallback.ButtonPressed -= OnFallbackPressed;
        _managed.ButtonPressed -= OnManagedPressed;
    }

    private void BeginSwitch(UiInputSource to)
    {
        if (_current == to)
        {
            return;
        }

        // What the outgoing source had held is what must not produce edges on the incoming one.
        _suppressed = to is UiInputSource.ManagedCanonical ? _managed.Held : 0;
        _switchedAt = _time.GetUtcNow();
        if (_current is UiInputSource.ManagedCanonical)
        {
            _managed.ButtonPressed -= OnManagedPressed;
        }

        _current = to;
        if (to is UiInputSource.ManagedCanonical)
        {
            _managed.ButtonPressed += OnManagedPressed;
        }
        else
        {
            // The managed source is no longer current, so its held state is stale. Leaving it would
            // swallow the first press after it comes back.
            _managed.Reset();
        }
    }

    private void ReleaseSuppressed(GamepadButtons observedNow)
    {
        if (_suppressed == 0)
        {
            return;
        }

        // A control stays suppressed while the incoming source still reports it held, and is
        // released once observed up — or once the bound expires, for controls the incoming source
        // cannot see at all and would otherwise suppress forever.
        _suppressed = _time.GetUtcNow() - _switchedAt >= SourceSwitch.HeldControlTimeout
            ? 0
            : _suppressed & observedNow;
    }

    private void OnFallbackPressed(GamepadButtons buttons)
    {
        if (_current is UiInputSource.SdlWithSteamLease)
        {
            ButtonPressed?.Invoke(buttons);
        }
    }

    private void OnManagedPressed(GamepadButtons buttons)
    {
        // Neither a press edge nor a release edge is emitted for a suppressed control: the user made
        // neither, so reporting either would be inventing input.
        GamepadButtons allowed = buttons & ~_suppressed;
        if (allowed != 0)
        {
            ButtonPressed?.Invoke(allowed);
        }
    }
}
