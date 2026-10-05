using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using WindowsDeviceControl;
using WSGM.Overlay;
using WSGM.Shell;
using WSGM.UiTests.Infrastructure;

namespace WSGM.UiTests.Visual;

/// <summary>Renders the production Audio markup with frozen values and no attached audio owner.</summary>
internal static class PreviewAudioPanel
{
    internal static void Export(string directory)
    {
        using var fixture = new UiFixture();
        using var audio = new AudioManager();
        var panel = new AudioPanel(audio, null);
        var body = (Control)panel.Content!;
        // The owner never attaches, so its refresh, capability reads and watches never start.
        panel.Content = null;
        body.DataContext = audio;
        var volume = UiFixture.Named<Slider>(panel, "VolumeSlider");
        Freeze(volume, RangeBase.ValueProperty, 60d);
        var volumeText = ((Grid)volume.Parent!).Children.OfType<TextBlock>().Last();
        Freeze(volumeText, TextBlock.TextProperty, "60%");
        var choices = body.GetLogicalDescendants().OfType<ComboBox>().ToArray();
        AudioEndpointEntry output = new("preview-output", "Speakers (Realtek Audio)");
        AudioEndpointEntry input = new("preview-input", "Microphone Array");
        // A disabled dropdown commits nothing, so freezing the endpoints never asks Windows to change the
        // default device.
        choices[0].IsEnabled = choices[^1].IsEnabled = false;
        Freeze(choices[0], SelectingItemsControl.SelectedItemProperty, output);
        choices[0].ItemsSource = new[] { output };
        choices[0].SelectedItem = output;
        Freeze(choices[^1], SelectingItemsControl.SelectedItemProperty, input);
        choices[^1].ItemsSource = new[] { input };
        choices[^1].SelectedItem = input;
        choices[0].IsEnabled = choices[^1].IsEnabled = true;
        var format = new CoreAudio.AudioDeviceFormat(2, 48000, 24, 32, 3, false);
        Show("Channels", new AudioPlaybackChoice(format, "Stereo"));
        Show("Format", new AudioPlaybackChoice(format, "48 kHz · 24-bit PCM"));
        Show("Spatial", new SpatialAudioOption(CoreAudio.SpatialAudioFormats.Off, "Off"));
        var border = new Border { Child = body, Padding = new Thickness(20) };
        var window = new Window
        {
            Width = 620, SizeToContent = SizeToContent.Height, Content = border,
            Classes = { "command-deck" }, WindowDecorations = WindowDecorations.None
        };
        window.Bind(Window.BackgroundProperty, window.GetResourceObservable("DeckSurfaceBrush"));
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.FocusManager!.Focus(null);
            window.MouseMove(new Point(-20, -20));
            using var frame = window.CaptureRenderedFrame()
                              ?? throw new InvalidOperationException("No Audio preview frame was available.");
            frame.Save(Path.Combine(directory, "audio-compact.png"), new PngBitmapEncoderOptions());
        }
        finally
        {
            window.Close();
        }

        return;

        void Show(string name, object option)
        {
            var choice = UiFixture.Named<ComboBox>(panel, name + "Choice");
            Freeze(choice, SelectingItemsControl.SelectedItemProperty, option);
            choice.ItemsSource = new[] { option };
            UiFixture.Named<Grid>(panel, name + "Row").IsVisible = true;
        }
    }

    private static void Freeze<T>(AvaloniaObject target, AvaloniaProperty<T> property, T value)
    {
        target.Bind(property, new Binding { Source = value, Mode = BindingMode.OneWay });
    }
}
