using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>In-window audio controls backed by the existing overlay actions.</summary>
public partial class AudioPanel : UserControl
{
    private readonly AudioManager _audio;

    /// <summary>Creates an audio panel over the supplied live manager.</summary>
    /// <param name="audio">The taskbar-owned audio manager.</param>
    public AudioPanel(AudioManager audio)
    {
        _audio = audio;
        InitializeComponent();
        DataContext = audio;
        AttachedToVisualTree += (_, _) =>
        {
            _audio.Refresh();
            VolumeSlider.Focus(NavigationMethod.Directional);
        };
    }

    /// <summary>The slider receives controller focus when the panel opens.</summary>
    internal InputElement DefaultFocusTarget => VolumeSlider;

    private void OnRefreshClicked(object? sender, RoutedEventArgs e)
    {
        _audio.Refresh();
    }
}
