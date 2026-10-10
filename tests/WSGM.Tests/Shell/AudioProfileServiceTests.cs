using WindowsDeviceControl;
using WSGM.Core;
using WSGM.Shell;
using WSGM.Tests.Fakes;

namespace WSGM.Tests.Shell;

public sealed class AudioProfileServiceTests
{
    private static AudioProfilePreference TvAudio()
    {
        return new AudioProfilePreference
        {
            Output = new AudioEndpointPreference { Id = "tv", Name = "TV HDMI" }, VolumePercent = 40,
            PlaybackFormat = AudioProfileService.ToPreference(CoreAudio.AudioDeviceFormat.Pcm(2, 48000, 24)),
            SpatialFormat = CoreAudio.SpatialAudioFormats.Off
        };
    }

    [Fact]
    public async Task AHdmiEndpointAppearingAfterTheLayoutIsSelectedBeforeItsVolumeAndFormatsAreWritten()
    {
        var native = new AudioOperations { ArriveOnWatch = true };
        await using var service = new AudioProfileService(native);

        var result = await service.ApplyAsync(TvAudio(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(["watch", "default:tv", "volume:40", "format:tv", "spatial:tv"], native.Writes);
    }

    [Fact]
    public async Task AMissingEndpointCannotSendItsVolumeOrFormatToTheDesktopsCurrentEndpoint()
    {
        var native = new AudioOperations();
        await using var service = new AudioProfileService(native);

        var result = await service.ApplyAsync(TvAudio(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(4, result.Operations.Count);
        Assert.All(result.Operations, operation => Assert.False(operation.Succeeded));
        Assert.Equal(["watch"], native.Writes);
    }

    [Fact]
    public async Task PendingHdmiAudioDoesNotBlockTheNextEntryBeforeItsDisplayCanBeEnabled()
    {
        using var store = new TemporaryConfigStore();
        store.Store.Update(config =>
        {
            config.GameModeLaunchRecovery.PendingReturnAudio = TvAudio();
            return true;
        });
        await using var service = new AudioProfileService(new AudioOperations());
        List<string> warnings = [];

        var canEnter = await GameModeReturnRecovery.RestorePendingAsync(store.Store, CancellationToken.None,
            service, warnings.Add, requireAudio: false);

        Assert.True(canEnter);
        Assert.NotEmpty(warnings);
        Assert.Equal("tv", store.Store.Read().RequireConfig().GameModeLaunchRecovery.PendingReturnAudio?.Output?.Id);
        Assert.False(await GameModeReturnRecovery.RestorePendingAsync(store.Store, CancellationToken.None,
            service, warnings.Add));
    }

    [Fact]
    public async Task ExplicitFormatOnTheCurrentDefaultDoesNotRequireChoosingAnotherEndpoint()
    {
        var native = new AudioOperations { InitiallyAvailable = true };
        await using var service = new AudioProfileService(native);
        var preference = TvAudio();
        preference.Output = null;
        GameModeLaunchRules.NormalizeAudioProfile(preference);

        var result = await service.ApplyAsync(preference, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotNull(preference.PlaybackFormat);
        Assert.Equal(["volume:40", "format:tv", "spatial:tv"], native.Writes);
        Assert.DoesNotContain("default:tv", native.Writes);
    }

    private sealed class AudioOperations : IAudioProfileOperations
    {
        private bool _available;
        private bool _selected;
        internal bool ArriveOnWatch { get; init; }

        internal bool InitiallyAvailable
        {
            init
            {
                _available = value;
                _selected = value;
            }
        }

        internal List<string> Writes { get; } = [];

        public int ListEndpoints(CoreAudio.AudioDirection direction,
            out IReadOnlyList<CoreAudio.AudioEndpoint> endpoints)
        {
            endpoints = direction == CoreAudio.AudioDirection.Render && _available
                ? [new CoreAudio.AudioEndpoint("tv", "TV HDMI", _selected)]
                : [];
            return 0;
        }

        public int WatchEndpoints(Action onChanged, out IDisposable? watch)
        {
            Writes.Add("watch");
            watch = null;
            if (!ArriveOnWatch)
            {
                return -1;
            }

            _available = true;
            onChanged();
            return 0;
        }

        public int SetDefaultEndpoint(string endpointId)
        {
            Writes.Add("default:" + endpointId);
            _selected = true;
            return 0;
        }

        public int GetVolume(CoreAudio.AudioDirection direction, out int volume, out int muted)
        {
            volume = 50;
            muted = 0;
            return 0;
        }

        public int SetVolume(CoreAudio.AudioDirection direction, int volume, out int muted)
        {
            Writes.Add("volume:" + volume);
            muted = 0;
            return 0;
        }

        public int SetMuted(bool muted)
        {
            return 0;
        }

        public int GetDeviceFormat(string endpointId, out CoreAudio.AudioDeviceFormat format)
        {
            format = CoreAudio.AudioDeviceFormat.Pcm(2, 48000, 24);
            return 0;
        }

        public int ListSupportedDeviceFormats(string endpointId, out IReadOnlyList<CoreAudio.AudioDeviceFormat> formats)
        {
            formats = [CoreAudio.AudioDeviceFormat.Pcm(2, 48000, 24)];
            return 0;
        }

        public int SetDeviceFormat(string endpointId, CoreAudio.AudioDeviceFormat format)
        {
            Writes.Add("format:" + endpointId);
            return 0;
        }

        public int GetSpatialAudio(string endpointId, out CoreAudio.SpatialAudioState state)
        {
            state = new CoreAudio.SpatialAudioState(false, CoreAudio.SpatialAudioFormats.Off,
                CoreAudio.SpatialAudioFormats.Off, []);
            return 0;
        }

        public int SetSpatialAudio(string endpointId, Guid format, out CoreAudio.SpatialAudioSetStatus status)
        {
            Writes.Add("spatial:" + endpointId);
            status = CoreAudio.SpatialAudioSetStatus.Succeeded;
            return 0;
        }
    }
}
