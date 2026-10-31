using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;

namespace WSGM.Shell;

internal sealed partial class SteamNativeSettingsService
{
    /// <summary>Builds the Power page: presets, TDP and AutoTDP, Windows power, sleep and CPU behavior.</summary>
    /// <param name="power">The page's sections, appended in order.</param>
    /// <param name="offer">The read's offered command set.</param>
    /// <param name="presetsRead">The device preset read, started with the other backends.</param>
    /// <param name="schemesRead">The Windows power-profile read.</param>
    /// <param name="boostRead">The processor boost read; a default value when no boost owner exists.</param>
    /// <param name="coresRead">The processor core-placement read.</param>
    private async Task AddPowerPageAsync(List<SteamSettingsSection> power, OfferedRows offer,
        ValueTask<SteamPowerPresetState?> presetsRead, ValueTask<SteamPowerProfileState?> schemesRead,
        ValueTask<SteamCpuBoostState?> boostRead, ValueTask<SteamHybridCoreState?> coresRead)
    {
        var presets = await presetsRead.ConfigureAwait(false);
        if (presets is { Available: true })
        {
            List<SteamSettingsRow> rows = [];
            var options = new[] { new SteamSettingsChoice(string.Empty, presets.UnsetLabel) }
                .Concat(presets.Options.Where(option => option.Selectable)
                    .Select(option => new SteamSettingsChoice(option.Id, option.Label))).ToArray();
            offer.Add(rows, new SteamSettingsRow("power.preset-ac", SteamSettingsRowKind.Choice,
                    "Performance mode (plugged in)", presets.Scope, Text: presets.Ac, Choices: options,
                    Accent: presets.AcAccent),
                (value, token) => powerPresets.SetAssignmentAsync(true, EmptyToNull(value.GetString()), token),
                true);
            offer.Add(rows, new SteamSettingsRow("power.preset-battery", SteamSettingsRowKind.Choice,
                    "Performance mode (on battery)", presets.Scope, Text: presets.Battery, Choices: options,
                    Accent: presets.BatteryAccent),
                (value, token) => powerPresets.SetAssignmentAsync(false, EmptyToNull(value.GetString()), token),
                true);
            rows.Add(new SteamSettingsRow("power.preset-current", SteamSettingsRowKind.Note,
                "Current performance mode", presets.StatusText, Text: presets.Current));
            Section(power, "Device performance", "presets", rows);
        }

        List<SteamSettingsRow> limits = [];
        var powerLimit = tdp.PowerLimit;
        if (powerLimit.CanSelectMode)
        {
            offer.Add(limits, new SteamSettingsRow("power.unified", SteamSettingsRowKind.Boolean,
                    "Unified TDP", "One target moves sustained and boost limits together.", powerLimit.Unified,
                    Accent: powerLimit.ModeAccent),
                (value, token) => tdp.SetUnifiedModeAsync(value.GetBoolean(), token), true);
        }

        PowerRange("power.sustained", powerLimit.Unified ? "TDP" : "Sustained power", powerLimit.Sustained,
            tdp.SetPrimaryLimitAsync);
        if (!powerLimit.Unified)
        {
            PowerRange("power.boost", "Boost power", powerLimit.Boost, tdp.SetBoostLimitAsync);
        }

        var automatic = autoTdp.Current;
        if (automatic.Available)
        {
            offer.Add(limits, new SteamSettingsRow("power.auto-tdp", SteamSettingsRowKind.Boolean, "AutoTDP",
                    automatic.StatusText, automatic.Enabled, Disabled: Busy(automatic.Progress)),
                (value, token) => autoTdp.SetAutoTdpAsync(value.GetBoolean(), token));
            // The target follows the switch, as it does in Quick Access and the overlay.
            if (automatic is { Enabled: true, Target: { } target })
            {
                offer.Add(limits, new SteamSettingsRow("power.auto-tdp-target", SteamSettingsRowKind.Range,
                        "AutoTDP target frame rate", target.StatusText, Number: target.Fps,
                        Minimum: target.MinimumFps, Maximum: target.MaximumFps, Step: 1, Suffix: " FPS",
                        Disabled: Busy(automatic.Progress), Accent: target.Accent),
                    (value, token) => autoTdp.SetAutoTdpTargetAsync(Integer(value), token), true);
            }
        }

        Section(power, "TDP and AutoTDP", "tdp", limits);
        if (await schemesRead.ConfigureAwait(false) is { Available: true } schemes)
        {
            List<SteamSettingsRow> rows = [];
            offer.Add(rows, Choice("power.scheme", "Windows power profile", schemes.Options,
                    schemes.Current, schemes.StatusText),
                (value, token) => powerProfiles.SetPowerProfileAsync(value.GetString()!, token));
            Section(power, "Windows power policy", "windows-power", rows);
        }

        if (displayTimeouts is not null)
        {
            List<SteamSettingsRow> rows = [];
            SleepTimeout(PowerTimeoutKind.SleepDc, "standby-battery", "Standby after (on battery)",
                "Idle time before the device enters standby.");
            SleepTimeout(PowerTimeoutKind.SleepAc, "standby-plugged-in", "Standby after (plugged in)",
                "Idle time before the device enters standby.");
            SleepTimeout(PowerTimeoutKind.HibernateDc, "hibernate-battery", "Hibernate after (on battery)",
                "Time in standby before the device hibernates. Only the power button wakes it.");
            SleepTimeout(PowerTimeoutKind.HibernateAc, "hibernate-plugged-in", "Hibernate after (plugged in)",
                "Time in standby before the device hibernates. Only the power button wakes it.");
            Section(power, "Sleep and hibernation", "sleep", rows);

            void SleepTimeout(PowerTimeoutKind kind, string id, string label, string description)
            {
                // An unreadable value, or a hibernate timeout on a machine that cannot hibernate, is not offered.
                if (displayTimeouts.Read(kind) is not { } current)
                {
                    return;
                }

                offer.Add(rows, new SteamSettingsRow("power.timeout-" + id, SteamSettingsRowKind.Choice, label,
                        description, Text: current.ToString(CultureInfo.InvariantCulture),
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
        if (cpuBoost is not null && await boostRead.ConfigureAwait(false) is { Available: true } boost)
        {
            offer.Add(cpu, Choice("power.cpu-boost", "Processor boost", boost.Options, boost.Current, boost.StatusText)
                    with
                    {
                        Accent = boost.Accent
                    },
                (value, token) => cpuBoost.SetCpuBoostAsync(value.GetString()!, token), true);
        }

        if (await coresRead.ConfigureAwait(false) is { Available: true } cores)
        {
            offer.Add(cpu, Choice("power.hybrid", "Processor core preference", cores.Options, cores.Current,
                    cores.StatusText),
                (value, token) => hybridCores.SetHybridCoresAsync(value.GetString()!, token));
        }

        Section(power, "CPU behavior", "cpu", cpu);

        void PowerRange(string key, string label, SteamPowerLimitRangeState range,
            Func<int, CancellationToken, Task<SteamUiCommandResult>> write)
        {
            if (range is not
                { Available: true, MinimumWatts: { } minimum, MaximumWatts: { } maximum, StepWatts: { } step })
            {
                return;
            }

            offer.Add(limits, new SteamSettingsRow(key, SteamSettingsRowKind.Range, label, range.StatusText,
                    Number: range.ObservedWatts ?? maximum, Minimum: minimum, Maximum: maximum, Step: step,
                    Suffix: " W", Disabled: Busy(range.Progress), Accent: range.Accent),
                (value, token) => write(Integer(value), token), true);
        }
    }
}
