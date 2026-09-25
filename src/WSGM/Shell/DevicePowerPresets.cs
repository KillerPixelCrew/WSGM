using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Shell;

internal sealed record DevicePowerPresetState(
    IReadOnlyList<DevicePowerPreset> Presets,
    bool Available,
    string Current,
    string Status,
    DevicePowerCustomValues? Values = null);

/// <summary>One-shot device presets shared by the overlay and Steam. Nothing is reapplied on drift.</summary>
internal sealed class DevicePowerPresets(
    Func<IReadOnlyList<DeviceCapabilityView>> snapshot,
    Func<string, CapabilityValue, long, long, bool, CancellationToken, Task<CapabilityCommandResult>> execute,
    WindowsPowerModes modes,
    Func<bool?>? readOnAc = null)
{
    private string _status = string.Empty;

    // Also borrowed by independent power writes so a preset cannot interleave with AutoTDP or a
    // second WSGM surface. Firmware and other applications remain authoritative through readback.
    internal SemaphoreSlim MutationGate { get; } = new(1, 1);
    internal Func<bool>? AutomaticPowerOwner { get; set; }

    internal async Task<DevicePowerPresetState> ReadAsync(CancellationToken cancellationToken = default)
    {
        await MutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
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
                return Project(views, mode, _status, readOnAc?.Invoke(), AutomaticPowerOwner?.Invoke() == true);
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
            MutationGate.Release();
        }
    }

    internal async Task<SteamUiCommandResult> ApplyAsync(string id, CancellationToken cancellationToken,
        bool persistValues = true, bool? expectedOnAc = null,
        DevicePowerCustomValues? customValues = null)
    {
        await MutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
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
                return new SteamUiCommandResult(false, "The power preset is no longer available.");
            }

            var mutationStarted = false;
            try
            {
                // Check Windows access before touching hardware. There is no fallback to another plan.
                await Task.Run(modes.Read, cancellationToken).ConfigureAwait(false);
                var cycle = sustained!.Projection.State.CycleGeneration;
                var generation = sustained.Projection.State.DescriptorGeneration;
                var onAc = expectedOnAc ?? readOnAc?.Invoke();
                CheckCurrent();
                if (preset.ScenarioOnAc is not null)
                {
                    var scenario = ScenarioView(views)!;
                    var target = onAc == true ? preset.ScenarioOnAc : preset.ScenarioOnDc!;
                    mutationStarted = true;
                    var result = await execute(scenario.Descriptor.CapabilityId,
                        new CapabilityValue { Kind = CapabilityValueKind.Choice, ChoiceValue = target },
                        cycle, generation, false, cancellationToken).ConfigureAwait(false);
                    if (!result.Outcome.IsApplied())
                    {
                        throw new InvalidOperationException(result.Reason?.Detail ??
                                                            $"The device reported {result.Outcome}.");
                    }

                    CheckCurrent();
                    views = snapshot();
                    // Firmware that cannot report its scenario is trusted to have taken the write.
                    if ((ScenarioView(views)?.Projection.State.ObservedValue?.ChoiceValue is { } scenarioNow
                         && scenarioNow != target)
                        || !TryPair(views, out sustained, out slow))
                    {
                        throw new InvalidOperationException("The firmware scenario could not be confirmed.");
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
                            cycle, generation, persistValues, cancellationToken)
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
                var confirmedMode = await Task.Run(modes.Read, cancellationToken).ConfigureAwait(false);
                var confirmedViews = snapshot();
                CheckCurrent();
                var projected = Project(confirmedViews, confirmedMode, onAc: onAc);
                if (customValues is not null ? projected.Values != customValues : projected.Current != preset.Id)
                {
                    var observed = string.Join(", ", confirmedViews.Where(view => view.Descriptor.Role is
                            CapabilityRole.PowerSustainedLimit or CapabilityRole.PowerSlowLimit
                            or CapabilityRole.ScenarioMode)
                        .Select(view =>
                            $"{view.Descriptor.Role}={view.Projection.State.ObservedValue?.IntegerValue?.ToString()
                                                      ?? view.Projection.State.ObservedValue?.ChoiceValue ?? "unknown"}"));
                    throw new InvalidOperationException(
                        $"The final observed values do not match the preset: {observed}, Windows mode={confirmedMode}.");
                }

                _status = string.Empty;
                Log.Info($"Power preset {id} applied and verified (AC={onAc}, persist={persistValues}).");
                return new SteamUiCommandResult(true, null);

                void CheckCurrent()
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!SameGeneration(snapshot(), cycle, generation, preset, customValues is not null)
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
                return new SteamUiCommandResult(false, _status);
            }
        }
        finally
        {
            MutationGate.Release();
        }
    }

    internal static DevicePowerPresetState Project(IReadOnlyList<DeviceCapabilityView> views, Guid mode,
        string status = "", bool? onAc = null,
        bool automaticPowerOwner = false)
    {
        var presets = Presets(views);
        if (!TryPair(views, out var sustained, out var slow)
            || (presets.Any(preset => preset.ScenarioOnAc is not null)
                && (onAc is null || !Current(ScenarioView(views))
                                 || ScenarioView(views)!.Projection.State.CycleGeneration !=
                                 sustained!.Projection.State.CycleGeneration
                                 || ScenarioView(views)!.Projection.State.DescriptorGeneration !=
                                 sustained.Projection.State.DescriptorGeneration)))
        {
            return new DevicePowerPresetState(presets, false, string.Empty,
                "Waiting for current device power readings.");
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

    private static bool SameGeneration(IReadOnlyList<DeviceCapabilityView> views, long cycle, long generation,
        DevicePowerPreset preset, bool custom)
    {
        return ValidTarget(views, preset, custom) && TryPair(views, out var sustained, out var slow)
                                                  && sustained!.Projection.State.CycleGeneration == cycle &&
                                                  sustained.Projection.State.DescriptorGeneration == generation
                                                  && slow!.Projection.State.CycleGeneration == cycle &&
                                                  slow.Projection.State.DescriptorGeneration == generation
                                                  && (preset.ScenarioOnAc is null || (Current(ScenarioView(views))
                                                      && ScenarioView(views)!.Projection.State.CycleGeneration == cycle
                                                      && ScenarioView(views)!.Projection.State.DescriptorGeneration ==
                                                      generation));
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
        return Current(sustained) && Current(slow)
                                  && sustained!.Projection.State.CycleGeneration ==
                                  slow!.Projection.State.CycleGeneration
                                  && sustained.Projection.State.DescriptorGeneration ==
                                  slow.Projection.State.DescriptorGeneration;
    }

    private static bool Current(DeviceCapabilityView? view)
    {
        // Readback is not required: firmware that cannot report its limits still takes presets.
        return view is not null
               && DeviceCapabilityRouter.CanCommand(view.Projection.State)
               && view.Projection.Progress != CommandProgress.Pending
               && (view.Projection.Progress != CommandProgress.Uncertain
                   || view.Projection.State.ObservedAt > view.LastResult?.CompletedAt);
    }
}
