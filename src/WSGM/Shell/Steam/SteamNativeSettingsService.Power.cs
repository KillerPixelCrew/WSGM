using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Shell;

internal sealed partial class SteamNativeSettingsService
{
    private const string NoDevice = "Device Integration is not active in this session.";

    /// <summary>Builds the power rows: presets, TDP and AutoTDP, Windows power, sleep and CPU behavior.</summary>
    /// <param name="power">The Settings page's sections, appended in order.</param>
    /// <param name="offer">The read's offered command set and Quick Access sections.</param>
    /// <param name="presetsRead">The device preset read, started with the other backends.</param>
    /// <param name="schemesRead">The Windows power-profile read.</param>
    /// <param name="boostRead">The processor boost read; a default value when no boost owner exists.</param>
    /// <param name="coresRead">The processor core-placement read.</param>
    private async Task AddPowerPageAsync(List<SteamSettingsSection> power, OfferedRows offer,
        ValueTask<PowerPresetRowState> presetsRead, ValueTask<ChoiceRowState?> schemesRead,
        ValueTask<ChoiceRowState?> boostRead, ValueTask<ChoiceRowState?> coresRead)
    {
        AddPresetRows(power, offer, await presetsRead.ConfigureAwait(false));

        List<SteamSettingsRow> limits = [];
        AddPowerLimitRows(limits, offer);
        AddAutoTdpRows(limits, offer);
        Section(power, "TDP and AutoTDP", "tdp", limits);

        List<SteamSettingsRow> windows = [];
        if (ChoiceRow("power.scheme", "Windows power profile", "power", await schemesRead.ConfigureAwait(false))
            is { } scheme)
        {
            offer.Quick("powerProfile", offer.Add(windows, scheme,
                (value, token) => powerProfiles.SetPowerProfileAsync(value.GetString()!, token)));
        }

        Section(power, "Windows power policy", "windows-power", windows);

        if (displayTimeouts is not null)
        {
            List<SteamSettingsRow> rows = [];
            SleepTimeout(PowerTimeoutKind.SleepDc, "standby-battery",
                "Idle time before the device enters standby.");
            SleepTimeout(PowerTimeoutKind.SleepAc, "standby-plugged-in",
                "Idle time before the device enters standby.");
            SleepTimeout(PowerTimeoutKind.HibernateDc, "hibernate-battery",
                "Time in standby before the device hibernates. Only the power button wakes it.");
            SleepTimeout(PowerTimeoutKind.HibernateAc, "hibernate-plugged-in",
                "Time in standby before the device hibernates. Only the power button wakes it.");
            Section(power, "Sleep and hibernation", "sleep", rows);

            void SleepTimeout(PowerTimeoutKind kind, string id, string description)
            {
                // An unreadable value, or a hibernate timeout on a machine that cannot hibernate, is not offered.
                if (displayTimeouts.Read(kind) is not { } current)
                {
                    return;
                }

                offer.Add(rows, new SteamSettingsRow("power.timeout-" + id, SteamSettingsRowKind.Choice,
                        PowerTimeouts.Label(kind), description, Text: current.ToString(CultureInfo.InvariantCulture),
                        Choices:
                        [
                            .. DisplayTimeoutPolicy.Choices(current, null).Select(seconds =>
                                new SteamSettingsChoice(seconds.ToString(CultureInfo.InvariantCulture),
                                    PowerTimeouts.Describe(seconds)))
                        ]),
                    (value, _) => Task.FromResult(
                        displayTimeouts.Select(kind, int.Parse(value.GetString()!, CultureInfo.InvariantCulture))
                            ? SteamUiCommandResult.Applied
                            : new SteamUiCommandResult(false, "Windows did not accept the timeout.")));
            }
        }

        List<SteamSettingsRow> cpu = [];
        if (cpuBoost is not null
            && ChoiceRow("power.cpu-boost", "CPU boost mode", "turbo", await boostRead.ConfigureAwait(false))
                is { } boost)
        {
            offer.Quick("cpuBoost", offer.Add(cpu, boost,
                (value, token) => SetCpuBoostAsync(cpuBoost, value.GetString()!, token), true));
        }

        if (ChoiceRow("power.hybrid", "Processor cores", "cores", await coresRead.ConfigureAwait(false)) is { } cores)
        {
            offer.Quick("hybridCores", offer.Add(cpu, cores,
                (value, token) => hybridCores.SetHybridCoresAsync(value.GetString()!, token)));
        }

        Section(power, "CPU behavior", "cpu", cpu);
    }

    /// <summary>The plugged-in and battery preset assignments, and the preset in effect.</summary>
    /// <param name="power">The Settings page's sections.</param>
    /// <param name="offer">The read's offered command set and Quick Access sections.</param>
    /// <param name="presets">The device's presets and assignments.</param>
    private void AddPresetRows(List<SteamSettingsSection> power, OfferedRows offer, PowerPresetRowState presets)
    {
        if (presets.Options.Count == 0)
        {
            return;
        }

        // The empty entry is the way back to Global for a game's own assignment, so an override needs only
        // its marker, not a second control.
        ChoiceRowOption[] options =
        [
            new(string.Empty, presets.UnsetLabel is { Length: > 0 } unset ? unset : "Manual selection"),
            .. presets.Options
        ];
        var detail = string.Join(" · ", new[] { presets.Scope, presets.StatusText }.Where(text => text.Length > 0));
        List<SteamSettingsRow> rows = [];
        SteamSettingsRow? active = null;
        if (presets.Current.Length > 0)
        {
            active = new SteamSettingsRow("power.preset-current", SteamSettingsRowKind.Note, "Active profile",
                detail.Length > 0 ? detail : null, Text: presets.Current, Icon: "check",
                Summary: string.Join(" · ", new[]
                    {
                        Assigned("Plugged in", presets.Ac), Assigned("Battery", presets.Battery)
                    }.Where(text => text.Length > 0)) is { Length: > 0 } assigned
                    ? assigned
                    : presets.Current);
            rows.Add(active);
            offer.Quick("powerPreset", active);
        }

        // Without an active profile to hang it on, the scope and status still reach the user under the first
        // assignment rather than leaving two disabled dropdowns and no explanation.
        Assignment("power.preset-ac", "When plugged in", "plug", presets.Ac, presets.AcAccent,
            active is null && detail.Length > 0 ? detail : null, true);
        Assignment("power.preset-battery", "On battery", "battery", presets.Battery, presets.BatteryAccent, null,
            false);
        Section(power, "Device performance", "presets", rows);

        void Assignment(string key, string label, string icon, string selected, bool accent, string? description,
            bool ac)
        {
            var row = ChoiceRow(key, label, icon,
                new ChoiceRowState(presets.Available, options, selected, description ?? string.Empty, accent))! with
            {
                // Both assignments together are the active profile's summary.
                Summary = null,
                Disabled = !presets.Available
            };
            offer.Quick("powerPreset", offer.Add(rows, row,
                (value, token) => PowerPresetRows.AssignAsync(coordinator?.Power.PowerAssignments, ac,
                    EmptyToNull(value.GetString()), token), true));
        }

        string Assigned(string label, string id)
        {
            return id.Length == 0
                ? string.Empty
                : $"{label} {options.FirstOrDefault(option => option.Id == id)?.Label ?? id}";
        }
    }

    /// <summary>The unified-mode switch and the sustained and boost sliders.</summary>
    /// <param name="limits">The Settings section's rows.</param>
    /// <param name="offer">The read's offered command set and Quick Access sections.</param>
    private void AddPowerLimitRows(List<SteamSettingsRow> limits, OfferedRows offer)
    {
        var views = coordinator?.Capabilities.Snapshot() ?? [];
        var sustained = IntegerRangeProjection.PowerLimit(views);
        var boost = IntegerRangeProjection.PowerLimit(views, CapabilityRole.PowerSlowLimit);
        var layers = coordinator?.Profiles.Current.Layers;
        var sustainedAccent = layers is { } resolved
                              && CapabilityProjection.OverrideId(resolved, resolved.PowerTargetKey) is not null;
        var boostAccent = views.FirstOrDefault(view => view.Descriptor.Role is CapabilityRole.PowerSlowLimit)
                              is { } slow
                          && CapabilityProjection.DeviceOverrideId(slow) is not null;
        var unified = coordinator?.Power.ManualTdpUnified == true;
        var busy = Busy(sustained.Progress) || Busy(boost.Progress);
        var sustainedWatts = sustained.Observed ?? sustained.Desired;
        var boostWatts = boost.Observed ?? boost.Desired;
        var summary = unified
            ? $"{Watts(sustainedWatts)} W"
            : $"{Watts(sustainedWatts)} W sustained · {Watts(boostWatts)} W boost";
        if (coordinator?.Power.ManualTdpMode.Available == true)
        {
            var modeAccent = CapabilityProjection.OverrideId(layers,
                new ProfileSettingKey(ProfileField.TdpUnified)) is not null;
            offer.Quick("powerLimit", offer.Add(limits, new SteamSettingsRow("power.unified",
                    SteamSettingsRowKind.Boolean, "Unified TDP",
                    Marked("Coordinate sustained and boost limits with one target.", modeAccent), unified,
                    Disabled: busy, Accent: modeAccent, Summary: summary),
                (value, token) => SetUnifiedModeAsync(value.GetBoolean(), token), true));
            summary = null;
        }

        PowerRange("power.sustained", unified ? "TDP" : "Sustained power (PL1)", "bolt", sustained, sustainedWatts,
            sustainedAccent, CapabilityRole.PowerSustainedLimit);
        if (!unified)
        {
            PowerRange("power.boost", "Boost power (PL2)", "boost", boost, boostWatts, boostAccent,
                CapabilityRole.PowerSlowLimit);
        }

        void PowerRange(string key, string label, string icon, IntegerRangeProjection range, int? watts, bool accent,
            CapabilityRole role)
        {
            if (range is not { Minimum: { } minimum, Maximum: { } maximum, Step: { } step })
            {
                return;
            }

            var description = unified
                ? $"Sustained {Watts(sustainedWatts)} W · Boost {Watts(boostWatts)} W"
                : range.StatusText;
            offer.Quick("powerLimit", offer.Add(limits, new SteamSettingsRow(key, SteamSettingsRowKind.Range, label,
                    Marked(description, accent), Number: watts, Minimum: minimum, Maximum: maximum, Step: step,
                    Suffix: " W", Disabled: busy || !range.Available, Accent: accent, Icon: icon, Summary: summary),
                (value, token) => SetPowerLimitAsync(role, Integer(value), token), true));
            summary = null;
        }

        static string Watts(int? value)
        {
            return value?.ToString(CultureInfo.InvariantCulture) ?? "?";
        }
    }

    /// <summary>The AutoTDP switch and, while it is on, its frame-rate target.</summary>
    /// <param name="limits">The Settings section's rows.</param>
    /// <param name="offer">The read's offered command set and Quick Access sections.</param>
    private void AddAutoTdpRows(List<SteamSettingsRow> limits, OfferedRows offer)
    {
        var automatic = ReadAutoTdp();
        if (!automatic.Available)
        {
            return;
        }

        // While controlling, the watts AutoTDP settled on lead the description: a user watching the slider
        // move needs to see that something is driving it, and what it decided.
        var description = automatic is { Controlling: true, Watts: { } watts }
            ? string.Join(" · ", new[] { $"{watts} W", automatic.StatusText }.Where(text => text.Length > 0))
            : automatic.StatusText;
        offer.Quick("autoTdp", offer.Add(limits, new SteamSettingsRow("power.auto-tdp", SteamSettingsRowKind.Boolean,
                "Automatic TDP", description.Length > 0 ? description : null, automatic.Enabled,
                Disabled: Busy(automatic.Progress), Icon: "auto",
                Summary: !automatic.Enabled
                    ? null
                    : automatic is { Controlling: true, Watts: { } held }
                        ? $"Auto TDP holding {held} W"
                        : "Auto TDP on"),
            (value, token) => SetAutoTdpAsync(value.GetBoolean(), token)));
        // The target follows the switch, as it does in the overlay.
        if (automatic is { Enabled: true, Target: { } target })
        {
            offer.Quick("autoTdp", offer.Add(limits, new SteamSettingsRow("power.auto-tdp-target",
                    SteamSettingsRowKind.Range, "Target frame rate", Marked(target.StatusText, target.Accent),
                    Number: target.Fps, Minimum: target.MinimumFps, Maximum: target.MaximumFps, Step: 1,
                    Suffix: " FPS", Disabled: Busy(automatic.Progress), Accent: target.Accent, Icon: "target"),
                (value, token) => SetAutoTdpTargetAsync(Integer(value), token), true));
        }
    }

    /// <summary>AutoTDP's switch as the coordinator reports it now.</summary>
    /// <returns>The projected switch; without a coordinator, the unavailable one.</returns>
    private AutoTdpRowState ReadAutoTdp()
    {
        if (coordinator is null)
        {
            return new AutoTdpRowState(false, false, false, null, string.Empty, NoDevice);
        }

        var autoTdp = coordinator.Power.AutoTdp;
        var availability = autoTdp.Availability;
        return ProjectAutoTdp(
            coordinator.Power.AutoTdpEnabled,
            autoTdp.Status,
            IntegerRangeProjection.PowerLimit(coordinator.Capabilities.Snapshot()).Available,
            availability,
            AutoTdpTargetView.Resolve(coordinator.Profiles.Current.Layers, availability));
    }

    /// <summary>Projects the stored AutoTDP setting and live status into the rows' vocabulary.</summary>
    /// <param name="enabled">The stored setting.</param>
    /// <param name="status">The running service's state, or null when it is not running.</param>
    /// <param name="powerLimitAvailable">Whether a primary power limit exists to drive.</param>
    /// <param name="availability">The service's authoritative admission state.</param>
    /// <param name="target">The frame-rate target, or null to publish no slider.</param>
    /// <returns>The state the rows render.</returns>
    /// <remarks>
    ///     Only a device without a power limit hides the switch, as it hides the power-limit rows beside it.
    ///     Anything else that keeps AutoTDP from running is said in the row's description, and the row stays
    ///     where the user looks for it.
    /// </remarks>
    internal static AutoTdpRowState ProjectAutoTdp(
        bool enabled,
        AutoTdpStatus? status,
        bool powerLimitAvailable,
        AutoTdpAvailability? availability = null,
        AutoTdpTargetView? target = null)
    {
        // Without a power limit there is nothing to control, so the switch is not offered rather than
        // offered and then silently ineffective.
        if (!powerLimitAvailable)
        {
            return new AutoTdpRowState(false, enabled, false, null, string.Empty,
                NativeQamUi.Text("No primary power limit is available to control."));
        }

        var slider = target is null
            ? null
            : new AutoTdpRowTarget(target.Fps, AutoTdpTarget.MinimumFps, AutoTdpTarget.MaximumFps,
                NativeQamUi.Text(target.Description), target.GameOverride);
        if (availability is { Available: false })
        {
            return new AutoTdpRowState(true, enabled, false, null, "failed", NativeQamUi.Text(availability.Detail),
                slider);
        }

        if (status is null)
        {
            return new AutoTdpRowState(true, enabled, false, null, enabled ? "applying" : string.Empty,
                NativeQamUi.Text(enabled ? "Starting." : string.Empty), slider);
        }

        // A write that did not apply leaves the switch operable; whether AutoTDP can run at all is the
        // availability check above.
        return new AutoTdpRowState(true, enabled, status.State is AutoTdpState.Controlling, status.Watts,
            status.State switch
            {
                AutoTdpState.Controlling => "completed",
                AutoTdpState.Unavailable => "failed",
                _ => string.Empty
            },
            NativeQamUi.Text(AutoTdpReason.Describe(status.Detail)), slider);
    }

    /// <summary>Stores the AutoTDP setting through its one owner.</summary>
    /// <param name="enabled">Requested state; switching off stays allowed while control is unavailable.</param>
    /// <param name="cancellationToken">Cancels the coordinator's serialized setting transition.</param>
    /// <returns>Applied, or the authoritative availability refusal.</returns>
    private async Task<SteamUiCommandResult> SetAutoTdpAsync(bool enabled, CancellationToken cancellationToken)
    {
        var state = ReadAutoTdp();
        // Switching off is always allowed; only switching on needs something to control.
        if (coordinator is null || (enabled && !state.Available))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new SteamUiCommandResult(false, state.StatusText);
        }

        if (enabled && coordinator.Power.AutoTdp.Availability is { Available: false } availability)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new SteamUiCommandResult(false, NativeQamUi.Text(availability.Detail));
        }

        // The coordinator compares and sets under its own transition gate, so the requested value is what
        // lands even when another surface changed it in between.
        await coordinator.Power.SetAutoTdpEnabledAsync(enabled, cancellationToken).ConfigureAwait(false);
        return SteamUiCommandResult.Applied;
    }

    private async Task<SteamUiCommandResult> SetAutoTdpTargetAsync(int fps, CancellationToken cancellationToken)
    {
        if (coordinator is null)
        {
            return new SteamUiCommandResult(false, NoDevice);
        }

        if (!AutoTdpTarget.IsValid(fps))
        {
            return new SteamUiCommandResult(false, string.Create(CultureInfo.InvariantCulture,
                $"Choose a target between {AutoTdpTarget.MinimumFps} and {AutoTdpTarget.MaximumFps} FPS."));
        }

        await coordinator.Power.SetAutoTdpTargetAsync(fps, cancellationToken).ConfigureAwait(false);
        return SteamUiCommandResult.Applied;
    }

    /// <remarks>Cancellation is checked before the coordinator save; later cancellation does not revoke it.</remarks>
    private async Task<SteamUiCommandResult> SetUnifiedModeAsync(bool unified, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (coordinator is null)
        {
            return new SteamUiCommandResult(false, NoDevice);
        }

        try
        {
            await coordinator.Power.SetManualTdpModeAsync(unified).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return new SteamUiCommandResult(false, ex.Message);
        }

        return SteamUiCommandResult.Applied;
    }

    /// <summary>Writes one power limit after re-resolving its capability against the current descriptor.</summary>
    /// <param name="role">The sustained or boost limit.</param>
    /// <param name="watts">The requested limit.</param>
    /// <param name="cancellationToken">Cancels the device command.</param>
    /// <returns>Applied, or the device's refusal.</returns>
    internal async Task<SteamUiCommandResult> SetPowerLimitAsync(CapabilityRole role, int watts,
        CancellationToken cancellationToken)
    {
        if (coordinator is null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new SteamUiCommandResult(false, NoDevice);
        }

        var limit = IntegerRangeProjection.PowerLimit(coordinator.Capabilities.Snapshot(), role);
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

        var result = await coordinator.Commands.ExecuteAsync(capabilityId, limit.InstanceId,
            CapabilityValue.Integer(watts), NativeQamUi.CommandTimeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return NativeQamUi.CommandResult(result, result.Outcome switch
        {
            CommandOutcome.Rejected => "The requested power-limit command was rejected.",
            CommandOutcome.TimedOut => "The requested power-limit command timed out.",
            CommandOutcome.Indeterminate => "The requested power-limit result is indeterminate.",
            _ => "The requested power-limit command did not complete."
        });
    }

    /// <summary>Refreshes Windows boost capabilities and projects the effective profile preference.</summary>
    /// <param name="cpuBoost">The boost owner.</param>
    /// <param name="profiles">The profile owner, for the effective selection and its source.</param>
    /// <returns>Offered modes and the effective one, or an unavailable state with the reason.</returns>
    /// <remarks>
    ///     Every read asks Windows: a scheme switch can carry a different mode with it. Published as unavailable
    ///     rather than withheld, so an absent control can be told from a broken one.
    /// </remarks>
    internal static async ValueTask<ChoiceRowState?> ReadCpuBoostAsync(CpuBoostService cpuBoost,
        ProfileService profiles)
    {
        CpuBoostStatus? status;
        try
        {
            status = await cpuBoost.RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new ChoiceRowState(false, [], string.Empty, ex.Message);
        }

        if (status is not { Supported: true } || cpuBoost.View(profiles.Current.Layers) is not { } view)
        {
            return new ChoiceRowState(false, [], string.Empty,
                "The active power scheme does not expose a processor boost mode.");
        }

        var scope = view.Source switch
        {
            ProfileSource.Game => "Set for this game.",
            ProfileSource.Global => "From Global.",
            _ => "Not set by WSGM; Windows keeps its own value."
        };
        return new ChoiceRowState(true,
            [.. CpuBoost.Offered.Select(option => new ChoiceRowOption(CpuBoost.IdFor(option.Mode), option.Name))],
            view.Effective is { } mode ? CpuBoost.IdFor(mode) : string.Empty,
            $"{scope} {view.SourcesText}", view.Source is ProfileSource.Game);
    }

    /// <summary>Applies a boost mode through the owner the overlay row uses, saving it to the profile.</summary>
    /// <param name="cpuBoost">The boost owner.</param>
    /// <param name="option">The mode id.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Applied, or why not.</returns>
    internal static async Task<SteamUiCommandResult> SetCpuBoostAsync(CpuBoostService cpuBoost, string option,
        CancellationToken cancellationToken)
    {
        if (CpuBoost.ModeForId(option) is not { } mode)
        {
            return new SteamUiCommandResult(false, "Unknown processor boost mode.");
        }

        return await cpuBoost.SetFromUserAsync(mode, cancellationToken).ConfigureAwait(false)
            ? SteamUiCommandResult.Applied
            : new SteamUiCommandResult(false, "Windows did not confirm the processor boost mode.");
    }
}
