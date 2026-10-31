using System;
using System.Collections.Generic;
using System.Linq;
using WindowsDeviceControl;

namespace WSGM.Shell;

/// <summary>Separates channel layout from encoding without offering unsupported combinations.</summary>
internal static class AudioPlaybackChoices
{
    /// <summary>Offers one supported format per channel layout, preferring the current encoding.</summary>
    /// <param name="capabilities">One endpoint snapshot; no new device queries are made.</param>
    /// <returns>Distinct channel-layout choices, each backed by a complete supported format.</returns>
    internal static IReadOnlyList<AudioPlaybackChoice> Channels(AudioPlaybackCapabilities capabilities)
    {
        var current = capabilities.CurrentFormat;
        return capabilities.SupportedFormats.Distinct()
            .GroupBy(format => (format.Channels, format.ChannelMask))
            .Select(group => group.OrderByDescending(format => format == current)
                .ThenByDescending(format => format.SampleRate == current.SampleRate)
                .ThenByDescending(format => format.BitsPerSample == current.BitsPerSample)
                .ThenByDescending(format => format.IsFloat == current.IsFloat)
                .ThenByDescending(format => format.ContainerBitsPerSample == current.ContainerBitsPerSample)
                .First())
            .Select(format => new AudioPlaybackChoice(format, ChannelLabel(format)))
            .ToArray();
    }

    /// <summary>Offers supported encodings for the current channel count and mask.</summary>
    /// <param name="capabilities">One endpoint snapshot; no new device queries are made.</param>
    /// <returns>Distinct format choices compatible with the current channel layout.</returns>
    internal static IReadOnlyList<AudioPlaybackChoice> Formats(AudioPlaybackCapabilities capabilities)
    {
        var current = capabilities.CurrentFormat;
        return capabilities.SupportedFormats.Distinct()
            .Where(format => format.Channels == current.Channels && format.ChannelMask == current.ChannelMask)
            .Select(format => new AudioPlaybackChoice(format, FormatLabel(format)))
            .ToArray();
    }

    /// <summary>Offers spatial sound off first, then each supported spatial format once.</summary>
    /// <param name="supported">The endpoint's supported spatial formats; no new device queries are made.</param>
    /// <returns>The choices every audio surface lists, with their display names.</returns>
    internal static IReadOnlyList<SpatialAudioOption> Spatial(IReadOnlyList<Guid> supported)
    {
        return
        [
            .. new[] { CoreAudio.SpatialAudioFormats.Off }.Concat(supported).Distinct()
                .Select(static format => new SpatialAudioOption(format, SpatialAudioNames.For(format)))
        ];
    }

    private static string ChannelLabel(CoreAudio.AudioDeviceFormat format)
    {
        var name = format.Channels switch
        {
            1 => "Mono",
            2 => "Stereo",
            6 => format.ChannelMask == 0x60F ? "5.1 surround (side)" : "5.1 surround (rear)",
            8 => "7.1 surround",
            _ => $"{format.Channels} channels"
        };
        return name;
    }

    private static string FormatLabel(CoreAudio.AudioDeviceFormat format)
    {
        return $"{format.SampleRate / 1000.0:0.#} kHz · {format.BitsPerSample}-bit"
               + (format.IsFloat ? " float" : " PCM");
    }
}
