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
