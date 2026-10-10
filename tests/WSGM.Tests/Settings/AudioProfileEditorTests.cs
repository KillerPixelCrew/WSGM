using WindowsDeviceControl;
using WSGM.Core;
using WSGM.Settings;
using WSGM.Shell;

namespace WSGM.Tests.Settings;

public sealed class AudioProfileEditorTests
{
    [Fact]
    public async Task BoundSelectionFeedbackDuringRefreshDoesNotClearEndpointOrSavedCapabilities()
    {
        var changed = 0;
        var format = CoreAudio.AudioDeviceFormat.Pcm(6, 48000, 24);
        var read = new AudioDiscovery([new AudioEndpointOption("tv", "TV")], [], "tv", [format],
            [CoreAudio.SpatialAudioFormats.WindowsSonic]);
        AudioProfileEditor editor = new(() => ++changed, _ => read, action => action());
        editor.Load(new AudioProfilePreference
        {
            Output = new AudioEndpointPreference { Id = "tv", Name = "TV" },
            PlaybackFormat = AudioProfileService.ToPreference(format),
            SpatialFormat = CoreAudio.SpatialAudioFormats.WindowsSonic
        });
        // Avalonia sends null back to these setters as a selected item is removed from ItemsSource.
        editor.OutputChoices.CollectionChanged += (_, _) => editor.Output = null;
        editor.FormatChoices.CollectionChanged += (_, _) => editor.PlaybackFormat = null;
        editor.SpatialChoices.CollectionChanged += (_, _) => editor.SpatialFormat = null;

        await editor.RefreshEndpointsAsync();
        await editor.RefreshEndpointsAsync();

        Assert.Equal("tv", editor.Output?.Id);
        Assert.Equal(format, editor.PlaybackFormat?.Format);
        Assert.Equal(CoreAudio.SpatialAudioFormats.WindowsSonic, editor.SpatialFormat?.Format);
        Assert.Equal(0, changed);
        var saved = Assert.IsType<AudioProfilePreference>(editor.Build());
        Assert.Equal("tv", saved.Output?.Id);
        Assert.Equal(6, saved.PlaybackFormat?.Channels);
        Assert.Equal(CoreAudio.SpatialAudioFormats.WindowsSonic, saved.SpatialFormat);
    }

    [Fact]
    public async Task CurrentDefaultCapabilitiesPopulateWithoutChoosingOrPersistingAnEndpoint()
    {
        var format = CoreAudio.AudioDeviceFormat.Pcm(2, 44100, 16);
        AudioProfileEditor editor = new(() => { }, endpoint =>
        {
            Assert.Null(endpoint);
            return new AudioDiscovery([new AudioEndpointOption("default", "Speakers")], [], "default",
                [format], []);
        }, action => action());

        await editor.RefreshEndpointsAsync();

        Assert.Null(editor.Output);
        Assert.Equal(format, Assert.Single(editor.FormatChoices).Format);
        Assert.Equal(CoreAudio.SpatialAudioFormats.Off, Assert.Single(editor.SpatialChoices).Format);
        Assert.Null(editor.Build());
    }

    [Fact]
    public async Task MissingEndpointKeepsItsSavedFormatAndSpatialChoiceAcrossDiscovery()
    {
        var format = CoreAudio.AudioDeviceFormat.Pcm(8, 96000, 24);
        AudioProfileEditor editor = new(() => { }, _ => AudioDiscovery.Empty, action => action());
        editor.Load(new AudioProfilePreference
        {
            Output = new AudioEndpointPreference { Id = "unplugged", Name = "Receiver" },
            PlaybackFormat = AudioProfileService.ToPreference(format),
            SpatialFormat = CoreAudio.SpatialAudioFormats.DolbyAtmosForHomeTheater
        });

        await editor.RefreshEndpointsAsync();

        Assert.Equal("unplugged", editor.Output?.Id);
        Assert.Empty(editor.FormatChoices);
        var saved = Assert.IsType<AudioProfilePreference>(editor.Build());
        Assert.Equal(8, saved.PlaybackFormat?.Channels);
        Assert.Equal(CoreAudio.SpatialAudioFormats.DolbyAtmosForHomeTheater, saved.SpatialFormat);
    }

    [Fact]
    public async Task LoadingAnotherProfileInvalidatesAnOlderPendingCapabilityRead()
    {
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        AudioProfileEditor editor = new(() => { }, _ =>
        {
            started.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            return new AudioDiscovery([new AudioEndpointOption("old", "Old")], [], "old",
                [CoreAudio.AudioDeviceFormat.Pcm(2, 48000, 16)], []);
        }, action => action());
        editor.Load(new AudioProfilePreference { Output = new AudioEndpointPreference { Id = "old" } });
        var refresh = editor.RefreshEndpointsAsync();
        try
        {
            Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
            editor.Load(new AudioProfilePreference { Output = new AudioEndpointPreference { Id = "new" } });
        }
        finally
        {
            release.Set();
        }

        await refresh;
        Assert.Equal("new", editor.Output?.Id);
        Assert.Empty(editor.FormatChoices);
    }
}
