using WSGM.Shell;

namespace WSGM.Tests;

public sealed class NativeQamAudioProjectionTests
{
    [Fact]
    public void Project_NoEndpoints_ReportsUnavailableWithAReason()
    {
        SteamAudioState state = AudioManagerNativeQamAudioService.Project(Manager());

        Assert.False(state.Available);
        Assert.NotEqual(string.Empty, state.StatusText);
        Assert.Empty(state.Devices);
    }

    [Fact]
    public void Project_OutputAndInputEndpoints_CarryTheirOwnDirections()
    {
        AudioManager audio = Manager();
        audio.OutputEndpoints.Add(Endpoint("speakers", "Speakers"));
        audio.InputEndpoints.Add(Endpoint("mic", "Microphone"));

        SteamAudioState state = AudioManagerNativeQamAudioService.Project(audio);

        Assert.True(state.Available);
        SteamAudioDevice speakers = Assert.Single(state.Devices, d => d.Id == "speakers");
        SteamAudioDevice mic = Assert.Single(state.Devices, d => d.Id == "mic");
        Assert.True(speakers.HasOutput);
        Assert.False(speakers.HasInput);
        Assert.True(mic.HasInput);
        Assert.False(mic.HasOutput);
    }

    [Fact]
    public void Project_EndpointPresentInBothDirections_IsReportedOnceCarryingBoth()
    {
        // A headset shows up under render and capture with the same id. Steam's model is one entry
        // with a direction test, so listing it twice would put the same hardware in the picker
        // under two identities.
        AudioManager audio = Manager();
        audio.OutputEndpoints.Add(Endpoint("headset", "Headset"));
        audio.InputEndpoints.Add(Endpoint("headset", "Headset"));

        SteamAudioState state = AudioManagerNativeQamAudioService.Project(audio);

        SteamAudioDevice headset = Assert.Single(state.Devices);
        Assert.True(headset.HasOutput);
        Assert.True(headset.HasInput);
    }

    [Fact]
    public void Project_SelectedEndpoints_BecomeTheActiveIds()
    {
        AudioManager audio = Manager();
        AudioEndpointEntry speakers = Endpoint("speakers", "Speakers");
        AudioEndpointEntry mic = Endpoint("mic", "Microphone");
        audio.OutputEndpoints.Add(speakers);
        audio.InputEndpoints.Add(mic);
        audio.SelectedOutput = speakers;
        audio.SelectedInput = mic;

        SteamAudioState state = AudioManagerNativeQamAudioService.Project(audio);

        Assert.Equal("speakers", state.ActiveOutputDeviceId);
        Assert.Equal("mic", state.ActiveInputDeviceId);
    }

    [Fact]
    public void Project_NothingSelected_ReportsEmptyRatherThanGuessing()
    {
        AudioManager audio = Manager();
        audio.OutputEndpoints.Add(Endpoint("speakers", "Speakers"));

        SteamAudioState state = AudioManagerNativeQamAudioService.Project(audio);

        Assert.Equal(string.Empty, state.ActiveOutputDeviceId);
    }

    [Fact]
    public void Project_MicrophoneVolume_ReachesSteamSeparatelyFromTheSpeakerVolume()
    {
        // The two volumes travel as separate fields because Steam keys them by direction. Steam's
        // own audio store is what backs both the Quick Access audio section and the Settings audio
        // page, so this one projection is what puts the microphone on both of them.
        AudioManager audio = Manager();
        audio.OutputEndpoints.Add(Endpoint("speakers", "Speakers"));
        audio.InputEndpoints.Add(Endpoint("mic", "Microphone"));
        // ApplyInputVolume records what Windows reported. The public setter is the user's path and
        // queues a hardware write, which a test must never take.
        audio.ApplyInputVolume(75, muted: false);

        SteamAudioState state = AudioManagerNativeQamAudioService.Project(audio);

        Assert.Equal(75, state.InputVolumePercent);
        Assert.False(state.InputMuted);
    }

    [Fact]
    public void Project_MicrophoneMute_IsCarriedApartFromTheSpeakerMute()
    {
        AudioManager audio = Manager();
        audio.InputEndpoints.Add(Endpoint("mic", "Microphone"));
        audio.ApplyInputVolume(60, muted: true);

        SteamAudioState state = AudioManagerNativeQamAudioService.Project(audio);

        Assert.True(state.InputMuted);
        Assert.False(state.Muted);
    }

    [Fact]
    public void Project_NoCaptureEndpoint_LeavesTheMicrophoneVolumeUnstatedRatherThanZero()
    {
        // Zero is a volume a user can set. A machine with no capture endpoint has no microphone
        // volume at all, and publishing zero would render a real slider sitting at silent.
        AudioManager audio = Manager();
        audio.OutputEndpoints.Add(Endpoint("speakers", "Speakers"));

        SteamAudioState state = AudioManagerNativeQamAudioService.Project(audio);

        Assert.Null(state.InputVolumePercent);
    }

    [Fact]
    public void Project_MicrophoneAtZero_IsPublishedAsZeroRatherThanAsAbsent()
    {
        AudioManager audio = Manager();
        audio.InputEndpoints.Add(Endpoint("mic", "Microphone"));
        audio.ApplyInputVolume(0, muted: true);

        Assert.Equal(0, AudioManagerNativeQamAudioService.Project(audio).InputVolumePercent);
    }

    private static AudioManager Manager() => new();

    private static AudioEndpointEntry Endpoint(string id, string name) => new(id, name);
}
