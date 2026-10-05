using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using WindowsDeviceControl;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>In-window audio controls backed by the existing overlay actions.</summary>
public partial class AudioPanel : UserControl
{
    private readonly AudioManager _audio;
    private readonly AudioProfileService? _profiles;

    private bool _audioSubscribed;

    // The endpoint the controls currently describe, and the read that put them there. A refresh
    // starts when the panel attaches, when Refresh is clicked and whenever the default output
    // changes, so an older read can land after a newer endpoint is already selected.
    private string? _capabilityEndpointId;
    private int _capabilityGeneration;
    private bool _loadingCapabilities;
    private int _refreshGeneration;

    /// <summary>Creates an audio panel over the supplied live manager.</summary>
    /// <param name="audio">The taskbar-owned audio manager.</param>
    /// <param name="profiles">The optional live advanced-audio service.</param>
    internal AudioPanel(AudioManager audio, AudioProfileService? profiles)
    {
        _audio = audio;
        _profiles = profiles;
        InitializeComponent();
        // Browsing an open dropdown with the controller passes over items; only the choice the user settles
        // on changes the default device or the format.
        ComboCommit.Attach<AudioEndpointEntry>(OutputChoice, static () => false,
            entry => _audio.SelectedOutput = entry);
        ComboCommit.Attach<AudioEndpointEntry>(InputChoice, static () => false,
            entry => _audio.SelectedInput = entry);
        ComboCommit.Attach<AudioPlaybackChoice>(ChannelsChoice, () => _loadingCapabilities,
            option => _ = SetFormatAsync(option));
        ComboCommit.Attach<AudioPlaybackChoice>(FormatChoice, () => _loadingCapabilities,
            option => _ = SetFormatAsync(option));
        ComboCommit.Attach<SpatialAudioOption>(SpatialChoice, () => _loadingCapabilities,
            option => _ = SetSpatialAsync(option));
        DataContext = audio;
        AttachedToVisualTree += (_, _) =>
        {
            if (!_audioSubscribed)
            {
                _audio.PropertyChanged += OnAudioChanged;
                _audioSubscribed = true;
            }

            _audio.Refresh();
            VolumeSlider.Focus(NavigationMethod.Directional);
            _ = RefreshCapabilitiesAsync();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            if (_audioSubscribed)
            {
                _audio.PropertyChanged -= OnAudioChanged;
                _audioSubscribed = false;
            }
        };
    }

    /// <summary>The slider receives controller focus when the panel opens.</summary>
    internal InputElement DefaultFocusTarget => VolumeSlider;

    private void OnRefreshClicked(object? sender, RoutedEventArgs e)
    {
        _audio.Refresh();
        _ = RefreshCapabilitiesAsync();
    }

    private void OnAudioChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AudioManager.SelectedOutput))
        {
            _ = RefreshCapabilitiesAsync();
        }
    }

    private async Task RefreshCapabilitiesAsync()
    {
        if (_profiles is null)
        {
            ChannelsRow.IsVisible = false;
            FormatRow.IsVisible = false;
            SpatialRow.IsVisible = false;
            CapabilityStatus.Text = "Advanced audio controls are unavailable in this preview.";
            return;
        }

        var generation = ++_refreshGeneration;
        try
        {
            var capabilities = await _profiles.ReadPlaybackCapabilitiesAsync(CancellationToken.None);
            if (generation != _refreshGeneration)
            {
                return;
            }

            _loadingCapabilities = true;
            var channels = capabilities is null ? null : AudioPlaybackChoices.Channels(capabilities);
            var formats = capabilities is null ? null : AudioPlaybackChoices.Formats(capabilities);
            ChannelsChoice.ItemsSource = channels;
            FormatChoice.ItemsSource = formats;
            ChannelsChoice.SelectedItem =
                channels?.FirstOrDefault(choice => choice.Format == capabilities?.CurrentFormat);
            var spatialOptions = capabilities?.SupportedSpatialFormats is { Count: > 0 } spatial
                ? new ObservableCollection<SpatialAudioOption>(
                [
                    new SpatialAudioOption(CoreAudio.SpatialAudioFormats.Off, "Off"),
                    .. spatial.Select(static format =>
                        new SpatialAudioOption(format, SpatialAudioNames.For(format)))
                ])
                : null;
            SpatialChoice.ItemsSource = spatialOptions;
            FormatChoice.SelectedItem = formats?.FirstOrDefault(choice => choice.Format == capabilities?.CurrentFormat);
            SpatialChoice.SelectedItem = capabilities is null
                ? null
                : spatialOptions?.FirstOrDefault(option => option.Format == capabilities.CurrentSpatialFormat);
            ChannelsRow.IsVisible = channels?.Count > 0;
            FormatRow.IsVisible = formats?.Count > 0;
            SpatialRow.IsVisible = capabilities?.SupportedSpatialFormats.Count > 0;
            CapabilityStatus.Text = capabilities is null
                ? "Advanced audio controls are unavailable for the current output."
                : "";
            _capabilityEndpointId = capabilities?.EndpointId;
            _capabilityGeneration = generation;
        }
        catch (Exception ex)
        {
            if (generation != _refreshGeneration)
            {
                return;
            }

            _capabilityEndpointId = null;
            CapabilityStatus.Text = "Could not read advanced audio controls: " + ex.Message;
            ChannelsRow.IsVisible = false;
            FormatRow.IsVisible = false;
            SpatialRow.IsVisible = false;
        }
        finally
        {
            if (generation == _refreshGeneration)
            {
                _loadingCapabilities = false;
            }
        }
    }

    // An option is only offered because one read of one endpoint reported it. Two endpoints can
    // support the same format, and the same endpoint can report a different set on the next read,
    // so neither the service nor the endpoint id alone can tell that a selection was made against
    // a superseded list. Only the read the controls are currently showing may be acted on.
    private bool Stale(AudioEndpointEntry output)
    {
        if (_capabilityEndpointId is { } endpointId
            && endpointId == output.Id
            && _capabilityGeneration == _refreshGeneration)
        {
            return false;
        }

        CapabilityStatus.Text = "The playback device changed; re-reading its controls.";
        _ = RefreshCapabilitiesAsync();
        return true;
    }

    private async Task SetFormatAsync(AudioPlaybackChoice option)
    {
        if (_audio.SelectedOutput is not { } output || Stale(output))
        {
            return;
        }

        try
        {
            var result = await _profiles!.SetPlaybackFormatAsync(output.Id, option.Format, CancellationToken.None);
            CapabilityStatus.Text = result.Succeeded ? "" : result.Name + ": " + result.Detail;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("Could not change the playback format", ex);
            CapabilityStatus.Text = ex.Message;
        }

        await RefreshCapabilitiesAsync();
    }

    private async Task SetSpatialAsync(SpatialAudioOption option)
    {
        if (_audio.SelectedOutput is not { } output || Stale(output))
        {
            return;
        }

        try
        {
            var result = await _profiles!.SetSpatialFormatAsync(output.Id, option.Format, CancellationToken.None);
            CapabilityStatus.Text = result.Succeeded ? "" : result.Name + ": " + result.Detail;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("Could not change the spatial audio format", ex);
            CapabilityStatus.Text = ex.Message;
        }

        await RefreshCapabilitiesAsync();
    }
}
