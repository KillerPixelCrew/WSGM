using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Shell;

/// <summary>Projects sustained and boost readback into Steam's power sliders.</summary>
/// <remarks>
///     With a null coordinator (device integration not active this session) the state is the constant
///     unavailable one and every write is refused with its reason, so the surface stays honest without
///     a separate stand-in implementation. Publication follows the coordinator's capability and
///     configuration changes, which the Steam UI host hears once for every device row.
/// </remarks>
internal sealed class DeviceCoordinatorNativeQamTdpService : ISteamPowerLimitBackend
{
    private const string UnavailableText = "Device Integration is not active in this session.";

    private readonly DeviceCoordinator? _coordinator;

    /// <summary>Creates the power-limit projection without acquiring device ownership.</summary>
    /// <param name="coordinator">Borrowed device coordinator, or null to publish unavailable state and refuse writes.</param>
    internal DeviceCoordinatorNativeQamTdpService(DeviceCoordinator? coordinator)
    {
        _coordinator = coordinator;
    }

    /// <summary>Both sliders follow device readback, including profile changes.</summary>
    internal SteamPowerLimitState PowerLimit
    {
        get
        {
            var views = _coordinator?.Capabilities.Snapshot() ?? [];
            var state = ProjectPowerLimits(views);
            var layers = _coordinator?.Profiles.Current.Layers;
            return state with
            {
                Sustained = state.Sustained with
                {
                    Accent = layers is { } resolved
                             && CapabilityProjection.OverrideId(resolved, resolved.PowerTargetKey) is not null
                },
                Boost = state.Boost with
                {
                    Accent = views.FirstOrDefault(view => view.Descriptor.Role is CapabilityRole.PowerSlowLimit)
                                 is { } boost
                             && CapabilityProjection.DeviceOverrideId(boost) is not null
                },
                Unified = _coordinator?.ManualTdpUnified == true,
                CanSelectMode = _coordinator?.ManualTdpMode.Available == true,
                ModeAccent =
                CapabilityProjection.OverrideId(layers, new ProfileSettingKey(ProfileField.TdpUnified)) is not null
            };
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Profile changes republish the sliders. Cancellation is checked before the coordinator save; later cancellation
    ///     does not revoke it. An unavailable mode returns its refusal reason.
    /// </remarks>
    public async Task<SteamUiCommandResult> SetUnifiedModeAsync(bool unified, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_coordinator is null)
        {
            return SteamUiCommandResult.Refused;
        }

        try
        {
            await _coordinator.SetManualTdpModeAsync(unified).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return new SteamUiCommandResult(false, ex.Message);
        }

        return new SteamUiCommandResult(true, null);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> SetPrimaryLimitAsync(int watts, CancellationToken cancellationToken)
    {
        return SetLimitAsync(CapabilityRole.PowerSustainedLimit, watts, cancellationToken);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> SetBoostLimitAsync(int watts, CancellationToken cancellationToken)
    {
        return SetLimitAsync(CapabilityRole.PowerSlowLimit, watts, cancellationToken);
    }

    /// <summary>Projects sustained and slow/boost capabilities into Steam slider ranges.</summary>
    /// <param name="views">Current descriptor and observation snapshot; no device reads or writes occur here.</param>
    /// <returns>Both range states, using observed watts, then desired watts, then the ceiling for the slider position.</returns>
    internal static SteamPowerLimitState ProjectPowerLimits(IReadOnlyList<DeviceCapabilityView> views)
    {
        return new SteamPowerLimitState(
            ToRange(IntegerRangeProjection.PowerLimit(views)),
            ToRange(IntegerRangeProjection.PowerLimit(views, CapabilityRole.PowerSlowLimit)));
    }

    /// <summary>The slider's view of a power limit.</summary>
    /// <param name="limit">The raw projection.</param>
    /// <returns>The range state Steam's slider reads.</returns>
    /// <remarks>
    ///     Only the slider falls back: it shows what was last read or written, else what the profile asks
    ///     for, else the ceiling, so a device that cannot read its limits still gets a slider position.
    ///     The overlay reads the raw projection and shows no figure in that case.
    /// </remarks>
    private static SteamPowerLimitRangeState ToRange(IntegerRangeProjection limit)
    {
        return new SteamPowerLimitRangeState(
            limit.Available, limit.Minimum, limit.Maximum, limit.Step,
            limit.Observed ?? limit.Desired ?? limit.Maximum, limit.Progress, limit.StatusText);
    }

    private async Task<SteamUiCommandResult> SetLimitAsync(
        CapabilityRole role,
        int watts,
        CancellationToken cancellationToken)
    {
        if (_coordinator is null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new SteamUiCommandResult(false, UnavailableText);
        }

        var limit = IntegerRangeProjection.PowerLimit(_coordinator.Capabilities.Snapshot(), role);
        if (!limit.Available
            || limit.CapabilityId is not { } capabilityId
            || limit.Minimum is not { } minimum
            || limit.Maximum is not { } maximum
            || limit.Step is not { } step
            || CapabilityProjection.ValidInteger(CapabilityValue.Integer(watts), minimum, maximum, step) is null)
        {
            return new SteamUiCommandResult(false,
                "The requested power limit is unavailable or outside its current descriptor.");
        }

        var result = await _coordinator.ExecuteCapabilityAsync(
            capabilityId,
            limit.InstanceId,
            CapabilityValue.Integer(watts),
            NativeQamUi.CommandTimeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return NativeQamUi.CommandResult(result, OutcomeText(result.Outcome));
    }

    private static string OutcomeText(CommandOutcome outcome)
    {
        return outcome switch
        {
            CommandOutcome.Rejected => "The requested power-limit command was rejected.",
            CommandOutcome.TimedOut => "The requested power-limit command timed out.",
            CommandOutcome.Indeterminate => "The requested power-limit result is indeterminate.",
            _ => "The requested power-limit command did not complete."
        };
    }
}
