using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     Projects WSGM's AutoTDP into Steam's native quick-access menu, beside the limit it moves.
/// </summary>
/// <remarks>
///     AutoTDP is a WSGM setting driving a plugin capability, not a capability of its own, so this reads
///     the coordinator directly rather than looking for a descriptor. One owner: this switch, the
///     overlay's Power and thermals row, and the Settings checkbox all move
///     <c>DeviceIntegration.AutoTdpEnabled</c> through the same method, and none of them holds a copy.
///     A null coordinator projects the constant unavailable state and refuses every write. The setting
///     and the power limit follow the coordinator's own changes, which the Steam UI host hears once for
///     every device row; this raises only AutoTDP's own status changes. The frame-rate target is a
///     profile value, published with the switch and saved through the coordinator like the overlay's.
/// </remarks>
internal sealed class DeviceCoordinatorNativeQamAutoTdpService : ISteamAutoTdpBackend, IDisposable
{
    private static readonly SteamAutoTdpState UnavailableState = new(
        false,
        false,
        false,
        null,
        string.Empty,
        "Device Integration is not active in this session.");

    private readonly AutoTdpService? _autoTdp;
    private readonly DeviceCoordinator? _coordinator;
    private bool _disposed;

    /// <summary>Creates the projection over a running coordinator, or the unavailable one.</summary>
    /// <param name="coordinator">The coordinator owning the AutoTDP setting, or null.</param>
    /// <param name="autoTdp">The session's AutoTDP service, or null when it is not running.</param>
    internal DeviceCoordinatorNativeQamAutoTdpService(
        DeviceCoordinator? coordinator,
        AutoTdpService? autoTdp)
    {
        _coordinator = coordinator;
        _autoTdp = autoTdp;
        if (_autoTdp is not null)
        {
            // The setting is not the state: AutoTDP moves between idle, controlling and paused, and
            // its wattage and frametime detail change, with the stored setting and every capability
            // view untouched. Without this the row rendered whatever it last saw.
            _autoTdp.StatusChanged += OnAutoTdpStatusChanged;
        }
    }

    /// <summary>The state Steam should currently be rendering.</summary>
    public SteamAutoTdpState Current
    {
        get
        {
            if (_coordinator is null)
            {
                return UnavailableState;
            }

            var availability = _autoTdp?.Availability;
            return Project(
                _coordinator.AutoTdpEnabled,
                _autoTdp?.Status,
                PowerLimitProjection.Project(_coordinator.Capabilities.Snapshot()).Available,
                availability,
                AutoTdpTargetView.Resolve(_coordinator.Profiles.Current.Layers, availability));
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_autoTdp is not null)
        {
            _autoTdp.StatusChanged -= OnAutoTdpStatusChanged;
        }
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> SetAutoTdpAsync(bool enabled, CancellationToken cancellationToken)
    {
        return SetEnabledAsync(enabled, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<SteamUiCommandResult> SetAutoTdpTargetAsync(int fps, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_coordinator is null)
        {
            return new SteamUiCommandResult(false, UnavailableState.StatusText);
        }

        if (!AutoTdpTarget.IsValid(fps))
        {
            return new SteamUiCommandResult(false, NativeQamUi.Text(string.Create(CultureInfo.InvariantCulture,
                $"Choose a target between {AutoTdpTarget.MinimumFps} and {AutoTdpTarget.MaximumFps} FPS.")));
        }

        await _coordinator.SetAutoTdpTargetAsync(fps, cancellationToken).ConfigureAwait(false);
        return new SteamUiCommandResult(true, null);
    }

    /// <summary>Raised when the projected state changes.</summary>
    public event Action? StateChanged;

    /// <summary>Stores the AutoTDP setting through its one owner.</summary>
    /// <param name="enabled">
    ///     Requested AutoTDP state; disabling remains allowed when a coordinator exists even if control is
    ///     unavailable.
    /// </param>
    /// <param name="cancellationToken">Cancels the coordinator's serialized setting transition.</param>
    /// <returns>Whether the setting was accepted, or the authoritative availability refusal.</returns>
    /// <exception cref="ObjectDisposedException">This adapter has been disposed.</exception>
    public async Task<SteamUiCommandResult> SetEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var state = Current;
        // Switching off is always allowed; only switching on needs something to control.
        if (_coordinator is null || (enabled && !state.Available))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new SteamUiCommandResult(false, state.StatusText);
        }

        if (enabled && _autoTdp?.Availability is { Available: false } availability)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new SteamUiCommandResult(false, NativeQamUi.Text(availability.Detail));
        }

        // Idempotent rather than an error: the page and the store can disagree for one frame after
        // a change made somewhere else, and re-sending the value it already has is the harmless way
        // that resolves. The coordinator compares and sets under its own transition gate, so the
        // requested value is what lands even when another surface changed it in between — a toggle
        // decided from the snapshot above would invert that newer value instead.
        await _coordinator.SetAutoTdpEnabledAsync(enabled, cancellationToken).ConfigureAwait(false);
        return new SteamUiCommandResult(true, null);
    }

    /// <summary>Projects the stored setting and live status into the menu's vocabulary.</summary>
    /// <param name="enabled">The stored setting.</param>
    /// <param name="status">The running service's state, or null when it is not running.</param>
    /// <param name="powerLimitAvailable">Whether a primary power limit exists to drive.</param>
    /// <param name="availability">The service's authoritative admission state.</param>
    /// <param name="target">The frame-rate target, or null to publish no slider.</param>
    /// <returns>The state the menu renders.</returns>
    /// <remarks>
    ///     Only a device without a power limit hides the switch, as it hides the power-limit row beside
    ///     it. Anything else that keeps AutoTDP from running is said in the row's description, and the row
    ///     stays where the user looks for it. A missing frame limit used to hide it outright.
    /// </remarks>
    internal static SteamAutoTdpState Project(
        bool enabled,
        AutoTdpStatus? status,
        bool powerLimitAvailable,
        AutoTdpAvailability? availability = null,
        AutoTdpTargetView? target = null)
    {
        // Without a power limit there is nothing to control, so the switch is not offered rather
        // than offered and then silently ineffective.
        if (!powerLimitAvailable)
        {
            return new SteamAutoTdpState(
                false,
                enabled,
                false,
                null,
                string.Empty,
                NativeQamUi.Text("No primary power limit is available to control."));
        }

        var slider = target is null
            ? null
            : new SteamAutoTdpTarget(target.Fps, AutoTdpTarget.MinimumFps, AutoTdpTarget.MaximumFps,
                NativeQamUi.Text(target.Description), target.GameOverride);
        if (availability is { Available: false })
        {
            return new SteamAutoTdpState(true, enabled, false, null, "failed",
                NativeQamUi.Text(availability.Detail), slider);
        }

        if (status is null)
        {
            return new SteamAutoTdpState(
                true,
                enabled,
                false,
                null,
                enabled ? "applying" : string.Empty,
                NativeQamUi.Text(enabled ? "Starting." : string.Empty),
                slider);
        }

        var controlling = status.State is AutoTdpState.Controlling;
        return new SteamAutoTdpState(
            // A write that did not apply leaves the switch operable; whether AutoTDP can run at all is
            // the availability check above.
            true,
            enabled,
            controlling,
            status.Watts,
            status.State switch
            {
                AutoTdpState.Controlling => "completed",
                AutoTdpState.Unavailable => "failed",
                _ => string.Empty
            },
            NativeQamUi.Text(AutoTdpReason.Describe(status.Detail)),
            slider);
    }

    // Raised from AutoTDP's own tick loop. Queueing a publication is thread-safe, so no UI-thread hop.
    private void OnAutoTdpStatusChanged(AutoTdpStatus status)
    {
        StateChanged?.Invoke();
    }
}
