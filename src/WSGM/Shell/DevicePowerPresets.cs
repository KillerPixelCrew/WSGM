using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Shell;

/// <summary>Current device preset choices and their projection from device/Windows power state.</summary>
/// <param name="Presets">Presets declared by the current capability set.</param>
/// <param name="Available">Whether the current power pair and any required source/scenario admit preset commands.</param>
/// <param name="Current">Matching preset identifier, <c>custom</c>, or empty when no current choice can be projected.</param>
/// <param name="Status">Last operation or availability detail; empty means no detail.</param>
/// <param name="Values">Current watt limits and Windows mode when projectable, otherwise null.</param>
internal sealed record DevicePowerPresetState(
    IReadOnlyList<DevicePowerPreset> Presets,
    bool Available,
    string Current,
    string Status,
    DevicePowerCustomValues? Values = null);

/// <summary>Whether a preset was applied and, when it was not, why.</summary>
/// <param name="Succeeded">Whether every step of the preset was applied.</param>
/// <param name="Error">Why it was not applied, or null on success.</param>
internal sealed record PowerPresetApplyResult(bool Succeeded, string? Error);

/// <summary>One-shot device presets shared by the overlay and Steam. Nothing is reapplied on drift.</summary>
/// <param name="snapshot">Reads the published capabilities.</param>
/// <param name="execute">Writes one capability; called while this holds the power lane.</param>
/// <param name="modes">The Windows power mode port.</param>
/// <param name="readOnAc">Reads the power source, or null when it is unknown.</param>
/// <param name="powerLane">
///     The device owner's one power lane, shared with every other sustained, boost and AutoTDP write so a
///     preset cannot interleave with them; null gives this instance a lane of its own.
/// </param>
/// <param name="automaticPowerOwner">Whether AutoTDP currently owns the runtime power limits.</param>
internal sealed class DevicePowerPresets(
    Func<IReadOnlyList<DeviceCapabilityView>> snapshot,
    Func<string, CapabilityValue, bool, CancellationToken, Task<CapabilityCommandResult>> execute,
    WindowsPowerModes modes,
    Func<bool?>? readOnAc = null,
    SemaphoreSlim? powerLane = null,
    Func<bool>? automaticPowerOwner = null)
{
    private readonly SemaphoreSlim _lane = powerLane ?? new SemaphoreSlim(1, 1);
    private string _status = string.Empty;

    /// <summary>Whether AutoTDP currently owns the runtime power limits, so no preset is in force.</summary>
    internal bool AutomaticPowerOwned => automaticPowerOwner?.Invoke() == true;

    /// <summary>Reads Windows power mode and published device values while holding the shared power lane.</summary>
    /// <param name="cancellationToken">Cancels lane admission or the Windows read wait.</param>
    /// <returns>Current preset projection; a Windows read failure becomes an unavailable result with diagnostic status.</returns>
    internal async Task<DevicePowerPresetState> ReadAsync(CancellationToken cancellationToken = default)
    {
        await _lane.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var views = snapshot();
            var presets = Presets(views);
            if (presets.Length == 0)
            {
                return new DevicePowerPresetState([], false, string.Empty, string.Empty);
            }

            try
            {
                var mode = await Task.Run(modes.Read, cancellationToken).ConfigureAwait(false);
                return Project(views, mode, _status, readOnAc?.Invoke(), AutomaticPowerOwned);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new DevicePowerPresetState(presets, false, string.Empty,
                    $"Windows power mode is unavailable: {ex.Message}");
            }
        }
        finally
        {
            _lane.Release();
        }
    }

    /// <summary>Applies one admitted preset as ordered scenario, paired-power, and Windows-mode steps.</summary>
    /// <param name="id">Declared preset identifier, or <c>custom</c> with explicit custom values.</param>
    /// <param name="cancellationToken">Cancels admission or remaining steps; completed native effects are not rolled back.</param>
    /// <param name="persistValues">Whether accepted watt-control writes enter the host's manual preference path.</param>
    /// <param name="expectedOnAc">Optional AC/battery state the caller observed; a changed source refuses remaining work.</param>
    /// <param name="customValues">Values for a custom assignment; ignored for a declared preset.</param>
    /// <returns>
    ///     Success when every step reports application, including accepted writes without readback; otherwise the failure
    ///     detail.
    /// </returns>
    /// <remarks>
    ///     A partial or uncertain operation is neither automatically retried nor rolled back across device and Windows
    ///     owners.
    /// </remarks>
    internal async Task<PowerPresetApplyResult> ApplyAsync(string id, CancellationToken cancellationToken,
        bool persistValues = true, bool? expectedOnAc = null,
        DevicePowerCustomValues? customValues = null)
    {
        await _lane.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var views = snapshot();
            var preset = id == "custom" && customValues is not null
                ? customValues.ToPreset()
                : Presets(views).FirstOrDefault(item => item.Id == id);
            if (preset is not null && !ValidTarget(views, preset, customValues is not null))
            {
                preset = null;
            }

            if (preset is null || !TryPair(views, out var sustained, out var slow))
            {
                return new PowerPresetApplyResult(false, "The power preset is no longer available.");
            }

            var mutationStarted = false;
            try
            {
                // Check Windows access before touching hardware. There is no fallback to another plan.
                await Task.Run(modes.Read, cancellationToken).ConfigureAwait(false);
                var onAc = expectedOnAc ?? readOnAc?.Invoke();
                CheckCurrent();
                if (preset.ScenarioOnAc is not null)
                {
                    var scenario = ScenarioView(views)!;
                    var target = onAc == true ? preset.ScenarioOnAc : preset.ScenarioOnDc!;
                    mutationStarted = true;
                    var result = await execute(scenario.Descriptor.CapabilityId,
                        new CapabilityValue { Kind = CapabilityValueKind.Choice, ChoiceValue = target },
                        false, cancellationToken).ConfigureAwait(false);
                    if (!result.Outcome.IsApplied())
                    {
                        throw new InvalidOperationException(result.Reason?.Detail ??
                                                            $"The device reported {result.Outcome}.");
                    }

                    // The scenario is trusted to have taken the write, as HC trusts it; nothing waits for
                    // it to be read back.
                    CheckCurrent();
                    views = snapshot();
                    if (!TryPair(views, out sustained, out slow))
                    {
                        throw new InvalidOperationException("The power limits left the device cycle.");
                    }
                }

                // Firmware scenario selection can change the pair, so order using its new readback.
                // Raise PL2 before PL1 when necessary; lower PL1 before lowering PL2.
                (DeviceCapabilityView View, int Watts)[] writes =
                    preset.SustainedWatts > (slow!.Projection.State.ObservedValue?.IntegerValue ?? preset.SlowWatts)
                        ? [(slow, preset.SlowWatts), (sustained!, preset.SustainedWatts)]
                        : [(sustained!, preset.SustainedWatts), (slow, preset.SlowWatts)];
                foreach (var write in writes)
                {
                    CheckCurrent();
                    // Send even an unchanged PL1 through the manual-value funnel, so choosing a
                    // preset pauses AutoTDP just like moving the TDP slider.
                    mutationStarted = true;
                    var result = await execute(write.View.Descriptor.CapabilityId,
                            new CapabilityValue { Kind = CapabilityValueKind.Integer, IntegerValue = write.Watts },
                            persistValues, cancellationToken)
                        .ConfigureAwait(false);
                    if (!result.Outcome.IsApplied())
                    {
                        throw new InvalidOperationException(result.Reason?.Detail ??
                                                            $"The device reported {result.Outcome}.");
                    }
                }

                CheckCurrent();
                await Task.Run(() => modes.Apply(preset.WindowsMode, cancellationToken), cancellationToken)
                    .ConfigureAwait(false);

                // Every write returned applied, so the preset is applied. Nothing is read back to confirm it:
                // firmware such as the Ally's cannot report its limits, and failing the preset on that left
                // the selection unusable.
                _status = string.Empty;
                Log.Info($"Power preset {id} applied (AC={onAc}, persist={persistValues}).");
                return new PowerPresetApplyResult(true, null);

                void CheckCurrent()
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!SameTarget(snapshot(), preset, customValues is not null)
                        || readOnAc?.Invoke() != onAc
                        || (preset.ScenarioOnAc is not null && onAc is null))
                    {
                        throw new InvalidOperationException(
                            "Device capabilities or power source changed during selection.");
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (mutationStarted)
                {
                    _status = "Preset selection was cancelled; some values may have changed.";
                }

                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // No retry or rollback across independently owned Windows and device controls.
                _status = mutationStarted
                    ? $"Preset was not fully applied; some values may have changed. {ex.Message}"
                    : $"Preset could not be applied. {ex.Message}";
                Log.Warn($"Power preset {id}: {_status}");
                return new PowerPresetApplyResult(false, _status);
            }
        }
        finally
        {
            _lane.Release();
        }
    }

    /// <summary>Projects preset selection from a captured capability set without reading or changing hardware.</summary>
    /// <param name="views">Published capability projections for one device.</param>
    /// <param name="mode">Observed Windows power-mode identifier.</param>
    /// <param name="status">Existing operation detail to retain when the controls are available.</param>
    /// <param name="onAc">Current power source, required when presets declare source-dependent scenarios.</param>
    /// <param name="automaticPowerOwner">Whether AutoTDP owns the runtime limits and selection must show custom.</param>
    /// <returns>Available choices, matching/custom selection, and any reason that commands are unavailable.</returns>
    internal static DevicePowerPresetState Project(IReadOnlyList<DeviceCapabilityView> views, Guid mode,
        string status = "", bool? onAc = null,
        bool automaticPowerOwner = false)
    {
        var presets = Presets(views);
        if (!TryPair(views, out var sustained, out var slow)
            || (presets.Any(preset => preset.ScenarioOnAc is not null)
                && !Current(ScenarioView(views))))
        {
            return new DevicePowerPresetState(presets, false, string.Empty,
                "The device's power controls are not available.");
        }

        if (presets.Any(preset => preset.ScenarioOnAc is not null) && onAc is null)
        {
            return new DevicePowerPresetState(presets, false, string.Empty,
                "Waiting for Windows to report the power source.");
        }

        var match = presets.FirstOrDefault(preset =>
            preset.SustainedWatts == sustained!.Projection.State.ObservedValue?.IntegerValue
            && preset.SlowWatts == slow!.Projection.State.ObservedValue?.IntegerValue
            && WindowsPowerModes.Id(preset.WindowsMode) == mode
            && (preset.ScenarioOnAc is null || ScenarioView(views)?.Projection.State.ObservedValue?.ChoiceValue
                == (onAc == true ? preset.ScenarioOnAc : preset.ScenarioOnDc)));
        var observedMode = Enum.GetValues<DevicePowerMode>().Cast<DevicePowerMode?>()
            .FirstOrDefault(item => WindowsPowerModes.Id(item!.Value) == mode);
        var values = observedMode is { } knownMode
                     && sustained!.Projection.State.ObservedValue?.IntegerValue is { } sustainedWatts
                     && slow!.Projection.State.ObservedValue?.IntegerValue is { } slowWatts
            ? new DevicePowerCustomValues
            {
                SustainedWatts = sustainedWatts,
                SlowWatts = slowWatts,
                WindowsMode = knownMode,
                Scenario = presets.Any(preset => preset.ScenarioOnAc is not null)
                    ? ScenarioView(views)!.Projection.State.ObservedValue?.ChoiceValue
                    : null
            }
            : null;
        if (automaticPowerOwner)
        {
            return new DevicePowerPresetState(presets, presets.Length > 0, "custom",
                "Custom: AutoTDP controls the runtime power limits.", values);
        }

        return new DevicePowerPresetState(presets, presets.Length > 0, match?.Id ?? "custom", status.Length > 0 ? status
            : match is null ? "Custom: current power limits, firmware scenario or Windows mode do not match a preset."
            : $"{match.SustainedWatts}/{match.SlowWatts} W · {WindowsPowerModes.Label(match.WindowsMode)}", values);
    }

    private static DevicePowerPreset[] Presets(IReadOnlyList<DeviceCapabilityView> views)
    {
        return DevicePowerPreset.TryValidate([.. views.Select(view => view.Descriptor)], out _)
            ? [.. views.SelectMany(view => view.Descriptor.PowerPresets)]
            : [];
    }

    private static bool ValidTarget(IReadOnlyList<DeviceCapabilityView> views, DevicePowerPreset preset, bool custom)
    {
        return custom
            ? Presets(views).Length > 0
              && Presets(views).Any(item => item.ScenarioOnAc is not null) == preset.ScenarioOnAc is not null
              && DevicePowerPreset.TryValidate([
                  .. views.Select(view => view.Descriptor with
                  {
                      PowerPresets = view.Descriptor.Role == CapabilityRole.PowerSustainedLimit ? [preset] : []
                  })
              ], out _)
            : Presets(views).Contains(preset);
    }

    private static bool SameTarget(IReadOnlyList<DeviceCapabilityView> views, DevicePowerPreset preset, bool custom)
    {
        return ValidTarget(views, preset, custom) && TryPair(views, out _, out _)
                                                  && (preset.ScenarioOnAc is null || Current(ScenarioView(views)));
    }

    private static DeviceCapabilityView? ScenarioView(IReadOnlyList<DeviceCapabilityView> views)
    {
        var matches = views.Where(view => view.Descriptor.Role == CapabilityRole.ScenarioMode).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static bool TryPair(IReadOnlyList<DeviceCapabilityView> views, out DeviceCapabilityView? sustained,
        out DeviceCapabilityView? slow)
    {
        var sustainedMatches =
            views.Where(view => view.Descriptor.Role == CapabilityRole.PowerSustainedLimit).ToArray();
        var slowMatches = views.Where(view => view.Descriptor.Role == CapabilityRole.PowerSlowLimit).ToArray();
        sustained = sustainedMatches.Length == 1 ? sustainedMatches[0] : null;
        slow = slowMatches.Length == 1 ? slowMatches[0] : null;
        return Current(sustained) && Current(slow);
    }

    /// <summary>Whether the capability can be commanded in this cycle.</summary>
    /// <remarks>
    ///     Nothing about readback: not a value, not a settled uncertain result, not a write in flight. A
    ///     preset stays selectable exactly as long as the device takes commands, as in HC.
    /// </remarks>
    private static bool Current(DeviceCapabilityView? view)
    {
        return view is not null && DeviceCapabilityRouter.CanCommand(view.Projection.State);
    }
}
