namespace WSGM.Core;

/// <summary>One exact playback format, including its speaker layout.</summary>
public sealed class AudioFormatPreference
{
    /// <summary>Speaker channel count.</summary>
    public int Channels { get; set; }

    /// <summary>Samples per second.</summary>
    public int SampleRate { get; set; }

    /// <summary>Meaningful bits in each sample.</summary>
    public int BitsPerSample { get; set; }

    /// <summary>Bits occupied by each sample.</summary>
    public int ContainerBitsPerSample { get; set; }

    /// <summary>Windows speaker-position mask.</summary>
    public uint ChannelMask { get; set; }

    /// <summary>Whether the format uses IEEE float samples rather than PCM.</summary>
    public bool IsFloat { get; set; }
}
