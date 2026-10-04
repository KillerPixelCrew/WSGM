using System;
using System.Collections.Generic;
using System.Linq;
using WindowsDeviceControl;

namespace WSGM.Core;

internal static class GameModeLaunchRules
{
    internal static AudioProfilePreference? DesktopAudio(GameModeLaunchConfiguration launch,
        GameModeLaunchRecovery recovery)
    {
        return launch.DesktopAudio ?? recovery.PendingReturnAudio;
    }

    internal static IReadOnlyList<string> Normalize(GameModeLaunchConfiguration launch)
    {
        List<string> diagnostics = [];
        launch.GameLayout = NormalizeLayout(launch.GameLayout, nameof(launch.GameLayout), diagnostics);
        launch.DesktopLayout = NormalizeLayout(launch.DesktopLayout, nameof(launch.DesktopLayout), diagnostics);
        launch.GameAudio = NormalizeAudioProfile(launch.GameAudio);
        launch.DesktopAudio = NormalizeAudioProfile(launch.DesktopAudio);
        launch.EnterActions = NormalizeSteps(launch.EnterActions);
        launch.LeaveActions = NormalizeSteps(launch.LeaveActions);
        launch.DesktopStartupActions = NormalizeSteps(launch.DesktopStartupActions);
        launch.DesktopWakeActions = NormalizeSteps(launch.DesktopWakeActions);
        launch.KnownDisplays ??= [];
        launch.KnownDisplays.RemoveAll(static display => display?.Target is null);
        foreach (var display in launch.KnownDisplays)
        {
            display.Modes =
            [
                .. (display.Modes ?? [])
                .Where(static mode => mode is { Width: > 0 and <= 16384, Height: > 0 and <= 16384 })
                .Distinct()
            ];
            display.MaximumDpiPercent = Math.Clamp(display.MaximumDpiPercent, 0, 500);
        }

        return diagnostics;
    }

    /// <summary>
    ///     Drops a layout whose JSON lacks its outputs, an output or an output's target or refresh, since
    ///     nothing could read it. A layout that is whole but fails <see cref="DisplayLayouts.Describe" /> stays
    ///     stored: applying it reports the reason, and the editor shows it when the layout is opened.
    /// </summary>
    private static DisplayLayout? NormalizeLayout(DisplayLayout? layout, string name, List<string> diagnostics)
    {
        if (layout is null)
        {
            return null;
        }

        // ReSharper disable ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (layout.Outputs is null
            || layout.Outputs.Any(static output => output?.Target is null || output.Refresh is null))
        {
            diagnostics.Add($"Game Mode {name} was dropped: its stored displays are incomplete.");
            return null;
        }
        // ReSharper restore ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract

        if (DisplayLayouts.Describe(layout) is { } reason)
        {
            diagnostics.Add($"Game Mode {name} is kept but cannot be applied as stored: {reason}");
        }

        return layout;
    }

    internal static AudioProfilePreference? NormalizeAudioProfile(AudioProfilePreference? profile)
    {
        if (profile is null)
        {
            return null;
        }

        profile.Output = NormalizeAudioEndpoint(profile.Output);
        profile.Input = NormalizeAudioEndpoint(profile.Input);
        profile.VolumePercent = profile.VolumePercent is { } volume and >= 0 and <= 100 ? volume : null;
        profile.PlaybackFormat = NormalizeAudioFormat(profile.PlaybackFormat);
        if (profile.Output is null)
        {
            profile.PlaybackFormat = null;
            profile.SpatialFormat = null;
        }

        return profile.Output is not null
               || profile.Input is not null
               || profile.VolumePercent is not null
               || profile.Muted is not null
               || profile.PlaybackFormat is not null
               || profile.SpatialFormat is not null
            ? profile
            : null;
    }

    private static AudioEndpointPreference? NormalizeAudioEndpoint(AudioEndpointPreference? endpoint)
    {
        if (endpoint?.Id?.Trim() is not { Length: > 0 } id)
        {
            return null;
        }

        endpoint.Id = id;
        endpoint.Name = endpoint.Name?.Trim() is { Length: > 0 } name ? name : null;
        return endpoint;
    }

    private static AudioFormatPreference? NormalizeAudioFormat(AudioFormatPreference? format)
    {
        return format is
        {
            Channels: > 0 and <= 32,
            SampleRate: > 0 and <= 384000,
            BitsPerSample: > 0 and <= 32,
            ContainerBitsPerSample: > 0 and <= 32,
            ChannelMask: > 0
        } && format.BitsPerSample <= format.ContainerBitsPerSample
            ? format
            : null;
    }

    /// <summary>
    ///     Removes action steps that have no plugin or action identity, preserving explicit steps.
    /// </summary>
    private static List<PluginActionStep> NormalizeSteps(List<PluginActionStep>? steps)
    {
        steps ??= [];
        steps.RemoveAll(static step => step?.Plugin is not { } plugin
                                       || string.IsNullOrWhiteSpace(plugin.PluginId)
                                       || string.IsNullOrWhiteSpace(plugin.InstanceId)
                                       || string.IsNullOrWhiteSpace(step.ActionId));
        foreach (var step in steps)
        {
            step.Arguments ??= [];
            step.TimeoutSeconds = Math.Clamp(step.TimeoutSeconds, 1, 120);
        }

        return steps;
    }
}
