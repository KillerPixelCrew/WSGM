using System.Collections.Generic;
using System.Linq;
using WindowsDeviceControl;

namespace WSGM.Shell;

/// <summary>One supported playback format presented as a channel or encoding choice.</summary>
/// <param name="Format">A probed supported device format represented by this choice.</param>
/// <param name="Label">Localized or user-facing display text.</param>
internal sealed record AudioPlaybackChoice(CoreAudio.AudioDeviceFormat Format, string Label)
{
    /// <inheritdoc />
    public override string ToString()
    {
        return Label;
    }
}

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
