using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using WSGM.Settings;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>In-window audio controls backed by the existing overlay actions.</summary>
public partial class AudioPanel : UserControl
{
    private readonly AudioManager _audio;
    private readonly AudioProfileService? _profiles;
    private bool _audioSubscribed;
    private bool _loadingCapabilities;

    /// <summary>Creates an audio panel over the supplied live manager.</summary>
    /// <param name="audio">The taskbar-owned audio manager.</param>
    /// <param name="profiles">The optional live advanced-audio service.</param>
    internal AudioPanel(AudioManager audio, AudioProfileService? profiles)
    {
        _audio = audio;
        _profiles = profiles;
        InitializeComponent();
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
            FormatRow.IsVisible = false;
            SpatialRow.IsVisible = false;
            CapabilityStatus.Text = "Advanced audio controls are unavailable in this preview.";
            return;
        }

        try
        {
            var capabilities = await _profiles.ReadPlaybackCapabilitiesAsync(CancellationToken.None);
            _loadingCapabilities = true;
            FormatChoice.ItemsSource = capabilities?.SupportedFormats is { Count: > 0 } formats
                ? new ObservableCollection<AudioFormatOption>(formats.Select(static format => new AudioFormatOption(format)))
                : null;
            var spatialOptions = capabilities?.SupportedSpatialFormats is { Count: > 0 } spatial
                ? new ObservableCollection<SpatialAudioOption>(
                    [new SpatialAudioOption(WindowsDeviceControl.CoreAudio.SpatialAudioFormats.Off, "Off"),
                        .. spatial.Select(static format => new SpatialAudioOption(format, AudioProfileEditor.SpatialName(format)))] )
                : null;
            SpatialChoice.ItemsSource = spatialOptions;
            FormatChoice.SelectedItem = capabilities is null ? null : new AudioFormatOption(capabilities.CurrentFormat);
            SpatialChoice.SelectedItem = capabilities is null
                ? null
                : spatialOptions?.FirstOrDefault(option => option.Format == capabilities.CurrentSpatialFormat);
            FormatRow.IsVisible = capabilities?.SupportedFormats.Count > 0;
            SpatialRow.IsVisible = capabilities?.SupportedSpatialFormats.Count > 0;
            CapabilityStatus.Text = capabilities is null ? "Advanced audio controls are unavailable for the current output." : "";
        }
        catch (System.Exception ex)
        {
            CapabilityStatus.Text = "Could not read advanced audio controls: " + ex.Message;
            FormatRow.IsVisible = false;
            SpatialRow.IsVisible = false;
        }
        finally
        {
            _loadingCapabilities = false;
        }
    }

    private async void OnFormatChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingCapabilities || FormatChoice.SelectedItem is not AudioFormatOption option
            || _audio.SelectedOutput is not { } output)
        {
            return;
        }

        var result = await _profiles!.SetPlaybackFormatAsync(output.Id, option.Format, CancellationToken.None);
        CapabilityStatus.Text = result.Succeeded ? "" : result.Name + ": " + result.Detail;
        await RefreshCapabilitiesAsync();
    }

    private async void OnSpatialChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingCapabilities || SpatialChoice.SelectedItem is not SpatialAudioOption option
            || _audio.SelectedOutput is not { } output)
        {
            return;
        }

        var result = await _profiles!.SetSpatialFormatAsync(output.Id, option.Format, CancellationToken.None);
        CapabilityStatus.Text = result.Succeeded ? "" : result.Name + ": " + result.Detail;
        await RefreshCapabilitiesAsync();
    }
}
