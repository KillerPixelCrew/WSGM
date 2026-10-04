using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using WindowsDeviceControl;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Settings;

using static WSGM.Core.AppConfigDefaults;

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
        return [];
    }

    private static AudioProfilePreference? NormalizeAudioProfile(AudioProfilePreference? profile)
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
        if (endpoint?.Id is not { Length: > 0 and <= 512 } id)
        {
            return null;
        }

        endpoint.Id = id.Trim();
        if (endpoint.Id.Length == 0)
        {
            return null;
        }

        endpoint.Name = endpoint.Name?.Trim() is { Length: > 0 and <= 256 } name ? name : null;
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
