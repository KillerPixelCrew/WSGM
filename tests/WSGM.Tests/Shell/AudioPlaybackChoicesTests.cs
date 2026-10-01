using WindowsDeviceControl;
using WSGM.Settings;
using WSGM.Shell;

namespace WSGM.Tests.Shell;

public sealed class AudioPlaybackChoicesTests
{
    [Fact]
    public void ChangingChannelsPreservesEncodingWhenTheLayoutSupportsIt()
    {
        var stereo = new CoreAudio.AudioDeviceFormat(2, 48000, 24, 32, 3, false);
        var surround = stereo with { Channels = 6, ChannelMask = 0x60F };
        var other = surround with { SampleRate = 44100, BitsPerSample = 16 };
        var capabilities = new AudioPlaybackCapabilities("output", [other, stereo, surround], stereo, [], Guid.Empty);

        var channels = AudioPlaybackChoices.Channels(capabilities);

        Assert.Equal(surround, channels.Single(choice => choice.Format.Channels == 6).Format);
        Assert.Equal(stereo, channels.Single(choice => choice.Format.Channels == 2).Format);
        Assert.Equal("Stereo", channels.Single(choice => choice.Format.Channels == 2).Label);
    }

    [Fact]
    public void FormatChoicesBelongToTheCurrentChannelLayout()
    {
        var stereo = new CoreAudio.AudioDeviceFormat(2, 48000, 24, 32, 3, false);
        var surround = stereo with { Channels = 6, ChannelMask = 0x60F };
        var capabilities = new AudioPlaybackCapabilities("output", [stereo, surround], stereo, [], Guid.Empty);

        var choice = Assert.Single(AudioPlaybackChoices.Formats(capabilities));

        Assert.Equal(stereo, choice.Format);
        Assert.DoesNotContain("channels", choice.Label);
        Assert.Contains("48 kHz", choice.Label);
    }

    [Fact]
    public void SpatialOffHasAReadableLabel()
    {
        Assert.Equal("Off", AudioProfileEditor.SpatialName(CoreAudio.SpatialAudioFormats.Off));
    }
}
