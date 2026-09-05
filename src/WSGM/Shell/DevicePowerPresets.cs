using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Shell;

internal sealed record DevicePowerPresetState(
    IReadOnlyList<DevicePowerPreset> Presets, bool Available, string Current, string Status);

/// <summary>One-shot device presets shared by the overlay and Steam. Nothing is reapplied on drift.</summary>
internal sealed class DevicePowerPresets(
    Func<IReadOnlyList<DeviceCapabilityView>> snapshot,
    Func<string, CapabilityValue, long, long, bool, CancellationToken, Task<CapabilityCommandResult>> execute,
    WindowsPowerModes modes,
    Func<bool?>? readOnAc = null)
{
    // Also borrowed by independent power writes so a preset cannot interleave with AutoTDP or a
    // second WSGM surface. Firmware and other applications remain authoritative through readback.
    internal SemaphoreSlim MutationGate { get; } = new(1, 1);
    private string _status = string.Empty;

    internal async Task<DevicePowerPresetState> ReadAsync(CancellationToken cancellationToken = default)
    {
        await MutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IReadOnlyList<DeviceCapabilityView> views = snapshot();
            DevicePowerPreset[] presets = Presets(views);
            if (presets.Length == 0) { return new([], false, string.Empty, string.Empty); }
            try
            {
                Guid mode = await Task.Run(modes.Read, cancellationToken).ConfigureAwait(false);
                return Project(views, mode, _status, readOnAc?.Invoke());
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return new(presets, false, string.Empty, $"Windows power mode is unavailable: {ex.Message}");
            }
        }
        finally { MutationGate.Release(); }
    }

    internal async Task<SteamUiCommandResult> ApplyAsync(string id, CancellationToken cancellationToken, bool persistValues = true)
    {
        await MutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IReadOnlyList<DeviceCapabilityView> views = snapshot();
            DevicePowerPreset? preset = Presets(views).FirstOrDefault(item => item.Id == id);
            if (preset is null || !TryPair(views, out DeviceCapabilityView? sustained, out DeviceCapabilityView? slow))
            {
                return new(false, "The power preset is no longer available.");
            }
            bool mutationStarted = false;
            try
            {
                // Check Windows access before touching hardware. There is no fallback to another plan.
                await Task.Run(modes.Read, cancellationToken).ConfigureAwait(false);
                long cycle = sustained!.Projection.State.CycleGeneration;
                long generation = sustained.Projection.State.DescriptorGeneration;
                bool? onAc = readOnAc?.Invoke();
                void CheckCurrent()
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!SameGeneration(snapshot(), cycle, generation, preset)
                        || (preset.ScenarioOnAc is not null && (onAc is null || readOnAc?.Invoke() != onAc)))
                    {
                        throw new InvalidOperationException("Device capabilities or power source changed during selection.");
                    }
                }
                CheckCurrent();
                if (preset.ScenarioOnAc is not null)
                {
                    DeviceCapabilityView scenario = ScenarioView(views)!;
                    string target = onAc == true ? preset.ScenarioOnAc : preset.ScenarioOnDc!;
                    mutationStarted = true;
                    CapabilityCommandResult result = await execute(scenario.Descriptor.CapabilityId,
                        new CapabilityValue { Kind = CapabilityValueKind.Choice, ChoiceValue = target },
                        cycle, generation, persistValues, cancellationToken).ConfigureAwait(false);
                    if (result.Outcome != CommandOutcome.AppliedVerified)
                    {
                        throw new InvalidOperationException(result.Reason?.Detail ?? $"The device reported {result.Outcome}.");
                    }
                    CheckCurrent();
                    views = snapshot();
                    if (ScenarioView(views)?.Projection.State.ObservedValue?.ChoiceValue != target
                        || !TryPair(views, out sustained, out slow))
                    {
                        throw new InvalidOperationException("The firmware scenario could not be confirmed.");
                    }
                }
                // Firmware scenario selection can change the pair, so order using its new readback.
                // Raise PL2 before PL1 when necessary; lower PL1 before lowering PL2.
                (DeviceCapabilityView View, int Watts)[] writes = preset.SustainedWatts > slow!.Projection.State.ObservedValue!.IntegerValue
                    ? [(slow, preset.SlowWatts), (sustained!, preset.SustainedWatts)]
                    : [(sustained!, preset.SustainedWatts), (slow, preset.SlowWatts)];
                foreach (var write in writes)
                {
                    CheckCurrent();
                    // Send even an unchanged PL1 through the manual-value funnel, so choosing a
                    // preset pauses AutoTDP just like moving the TDP slider.
                    mutationStarted = true;
                    CapabilityCommandResult result = await execute(write.View.Descriptor.CapabilityId,
                        new CapabilityValue { Kind = CapabilityValueKind.Integer, IntegerValue = write.Watts }, cycle, generation, persistValues, cancellationToken)
                        .ConfigureAwait(false);
                    if (result.Outcome != CommandOutcome.AppliedVerified)
                    {
                        throw new InvalidOperationException(result.Reason?.Detail ?? $"The device reported {result.Outcome}.");
                    }
                }
                CheckCurrent();
                await Task.Run(() => modes.Apply(preset.WindowsMode, cancellationToken), cancellationToken).ConfigureAwait(false);
                Guid confirmedMode = await Task.Run(modes.Read, cancellationToken).ConfigureAwait(false);
                IReadOnlyList<DeviceCapabilityView> confirmedViews = snapshot();
                CheckCurrent();
                if (Project(confirmedViews, confirmedMode, onAc: onAc).Current != preset.Id)
                {
                    throw new InvalidOperationException("The final observed values do not match the preset.");
                }
                _status = string.Empty;
                return new(true, null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (mutationStarted) { _status = "Preset selection was cancelled; some values may have changed."; }
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // No retry or rollback across independently owned Windows and device controls.
                _status = mutationStarted
                    ? $"Preset was not fully applied; some values may have changed. {ex.Message}"
                    : $"Preset could not be applied. {ex.Message}";
                return new(false, _status);
            }
        }
        finally { MutationGate.Release(); }
    }

    internal static DevicePowerPresetState Project(IReadOnlyList<DeviceCapabilityView> views, Guid mode, string status = "", bool? onAc = null)
    {
        DevicePowerPreset[] presets = Presets(views);
        if (!TryPair(views, out DeviceCapabilityView? sustained, out DeviceCapabilityView? slow)
            || (presets.Any(preset => preset.ScenarioOnAc is not null)
                && (onAc is null || !Current(ScenarioView(views))
                    || ScenarioView(views)!.Projection.State.ObservedValue?.ChoiceValue is null
                    || ScenarioView(views)!.Projection.State.CycleGeneration != sustained!.Projection.State.CycleGeneration
                    || ScenarioView(views)!.Projection.State.DescriptorGeneration != sustained!.Projection.State.DescriptorGeneration)))
        {
            return new(presets, false, string.Empty, "Waiting for current device power readings.");
        }
        DevicePowerPreset? match = presets.FirstOrDefault(preset =>
            preset.SustainedWatts == sustained!.Projection.State.ObservedValue!.IntegerValue
            && preset.SlowWatts == slow!.Projection.State.ObservedValue!.IntegerValue
            && WindowsPowerModes.Id(preset.WindowsMode) == mode
            && (preset.ScenarioOnAc is null || ScenarioView(views)?.Projection.State.ObservedValue?.ChoiceValue
                == (onAc == true ? preset.ScenarioOnAc : preset.ScenarioOnDc)));
        return new(presets, presets.Length > 0, match?.Id ?? "custom", status.Length > 0 ? status
            : match is null ? "Custom: current power limits, firmware scenario or Windows mode do not match a preset."
            : $"{match.SustainedWatts}/{match.SlowWatts} W · {WindowsPowerModes.Label(match.WindowsMode)}");
    }

    private static DevicePowerPreset[] Presets(IReadOnlyList<DeviceCapabilityView> views) =>
        DevicePowerPreset.TryValidate(views.Select(view => view.Descriptor).ToArray(), out _)
            ? views.SelectMany(view => view.Descriptor.PowerPresets).ToArray() : [];

    private static bool SameGeneration(IReadOnlyList<DeviceCapabilityView> views, long cycle, long generation, DevicePowerPreset preset) =>
        Presets(views).Contains(preset) && TryPair(views, out DeviceCapabilityView? sustained, out DeviceCapabilityView? slow)
        && sustained!.Projection.State.CycleGeneration == cycle && sustained.Projection.State.DescriptorGeneration == generation
        && slow!.Projection.State.CycleGeneration == cycle && slow.Projection.State.DescriptorGeneration == generation
        && (preset.ScenarioOnAc is null || (Current(ScenarioView(views))
            && ScenarioView(views)!.Projection.State.CycleGeneration == cycle
            && ScenarioView(views)!.Projection.State.DescriptorGeneration == generation));

    private static DeviceCapabilityView? ScenarioView(IReadOnlyList<DeviceCapabilityView> views)
    {
        DeviceCapabilityView[] matches = views.Where(view => view.Descriptor.Role == CapabilityRole.ScenarioMode).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static bool TryPair(IReadOnlyList<DeviceCapabilityView> views, out DeviceCapabilityView? sustained, out DeviceCapabilityView? slow)
    {
        DeviceCapabilityView[] sustainedMatches = views.Where(view => view.Descriptor.Role == CapabilityRole.PowerSustainedLimit).ToArray();
        DeviceCapabilityView[] slowMatches = views.Where(view => view.Descriptor.Role == CapabilityRole.PowerSlowLimit).ToArray();
        sustained = sustainedMatches.Length == 1 ? sustainedMatches[0] : null;
        slow = slowMatches.Length == 1 ? slowMatches[0] : null;
        return Current(sustained) && Current(slow)
            && sustained!.Projection.State.ObservedValue?.IntegerValue is not null
            && slow!.Projection.State.ObservedValue?.IntegerValue is not null
            && sustained.Projection.State.CycleGeneration == slow.Projection.State.CycleGeneration
            && sustained.Projection.State.DescriptorGeneration == slow.Projection.State.DescriptorGeneration;
    }

    private static bool Current(DeviceCapabilityView? view) => view is not null
        && view.Projection.State.Available
        && view.Projection.State.Quality is HardwareStateQuality.Observed or HardwareStateQuality.Verified
        && view.Projection.State.ObservedValue is not null
        && view.Projection.Progress != CommandProgress.Pending
        && (view.Projection.Progress != CommandProgress.Uncertain
            || view.Projection.State.ObservedAt > view.LastResult?.CompletedAt);
}
