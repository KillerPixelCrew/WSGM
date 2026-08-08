using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Platform.Storage;
using WSGM.Core;
using WSGM.Shell;
using WSGM.Themes;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace WSGM.Settings.Pages;

/// <summary>The Appearance settings page: the accent-color picker (preset swatches,
/// hex field, color-picker flyout — applied live to the running window as a
/// process-local preview; Save persists it) and the boot-splash editor (presets,
/// content, placements, colors, images, full-screen preview and .wsgmsplash
/// export/import). Inherits the window's <see cref="SettingsViewModel"/>
/// DataContext; pickers and the preview stay in this code-behind (StorageProvider
/// and the navigation swap need the visual tree's TopLevel).</summary>
public partial class AppearancePage : UserControl
{
    /// <summary>Preset accent swatches (D-pad friendly one-tap choices).</summary>
    private static readonly string[] AccentSwatches =
    [
        "#FFFF9D3D", // WSGM orange (default)
        "#FFE5484D", // red
        "#FFE93D82", // pink
        "#FF8E4EC6", // purple
        "#FF3B82F6", // blue
        "#FF00B7C3", // cyan
        "#FF30A46C", // green
        "#FFF5D90A", // yellow
        "#FFEEEEEE", // white
    ];

    private static readonly StreamGeometry CheckGeometry = StreamGeometry.Parse("M 2,7.5 L 6,11.5 L 12.5,3");

    private readonly List<(Button Button, Color Color, ShapePath Check)> _swatches = [];
    private SettingsViewModel? _viewModel;
    private Bitmap? _logoThumbBitmap;
    private Bitmap? _backgroundThumbBitmap;
    private bool _syncingAccent;

    /// <summary>Loads the compiled page XAML, builds the accent swatches and the
    /// splash preset list, and tracks the view model for live accent preview and
    /// image-thumbnail refreshes.</summary>
    public AppearancePage()
    {
        InitializeComponent();
        BuildSwatches();
        PresetCombo.ItemsSource = SplashPresets.All.Select(SplashPresets.DisplayName).ToList();
        PresetCombo.SelectedIndex = 0;
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) =>
        {
            LogoThumb.Source = null;
            BackgroundThumb.Source = null;
            _logoThumbBitmap?.Dispose();
            _logoThumbBitmap = null;
            _backgroundThumbBitmap?.Dispose();
            _backgroundThumbBitmap = null;
        };
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }
        _viewModel = DataContext as SettingsViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
        RefreshAccentVisuals();
        RefreshLogoThumbnail();
        RefreshBackgroundThumbnail();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SettingsViewModel.AccentColorHex):
                ApplyAccentPreview();
                break;
            case nameof(SettingsViewModel.SplashLogoPath):
                RefreshLogoThumbnail();
                break;
            case nameof(SettingsViewModel.SplashBackgroundImagePath):
                RefreshBackgroundThumbnail();
                break;
        }
    }

    // --- Accent ---
    private void BuildSwatches()
    {
        foreach (var hex in AccentSwatches)
        {
            var color = Color.Parse(hex);
            var check = new ShapePath
            {
                Data = CheckGeometry,
                Stroke = new ImmutableSolidColorBrush(
                    AccentPalette.UseBlackForeground(color) ? Colors.Black : Colors.White),
                StrokeThickness = 2.4,
                StrokeLineCap = PenLineCap.Round,
                StrokeJoin = PenLineJoin.Round,
                Width = 15,
                Height = 15,
                Stretch = Stretch.Uniform,
                IsVisible = false,
            };
            var button = new Button
            {
                Width = 34,
                Height = 34,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(4),
                // Local value, so the shared :pointerover style can't wash it out.
                Background = new ImmutableSolidColorBrush(color),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Content = check,
                Tag = hex,
            };
            ToolTip.SetTip(button, hex);
            button.Click += OnSwatchClick;
            _swatches.Add((button, color, check));
            SwatchPanel.Children.Add(button);
        }
    }

    private void OnSwatchClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not null && sender is Button { Tag: string hex })
        {
            _viewModel.AccentColorHex = hex;
        }
    }

    private void OnAccentPickerColorChanged(object? sender, ColorChangedEventArgs e)
    {
        if (_syncingAccent || _viewModel is null)
        {
            return;
        }
        var c = e.NewColor;
        _viewModel.AccentColorHex = $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
    }

    /// <summary>Live process-local accent preview: a parsable hex re-colors the
    /// running UI immediately (Save persists it for every process). A half-typed
    /// value changes nothing rather than flashing the fallback accent.</summary>
    private void ApplyAccentPreview()
    {
        if (_viewModel is null || !Color.TryParse(_viewModel.AccentColorHex, out var color))
        {
            return;
        }
        if (Application.Current is { } app)
        {
            AccentPalette.Apply(app, color);
        }
        RefreshAccentVisuals();
    }

    private void RefreshAccentVisuals()
    {
        if (_viewModel is null || !Color.TryParse(_viewModel.AccentColorHex, out var color))
        {
            return;
        }
        foreach (var (_, swatchColor, check) in _swatches)
        {
            check.IsVisible = swatchColor == color;
        }
        _syncingAccent = true;
        try
        {
            AccentPicker.Color = color;
        }
        finally
        {
            _syncingAccent = false;
        }
    }

    // --- Splash colors ---
    private void OnSplashBackgroundSwatchClick(object? sender, RoutedEventArgs e) =>
        ShowSplashColorFlyout(sender, static vm => vm.SplashBackgroundColorHex, static (vm, hex) => vm.SplashBackgroundColorHex = hex);

    private void OnSplashTextSwatchClick(object? sender, RoutedEventArgs e) =>
        ShowSplashColorFlyout(sender, static vm => vm.SplashTextColorHex, static (vm, hex) => vm.SplashTextColorHex = hex);

    private void OnSplashCaptionSwatchClick(object? sender, RoutedEventArgs e) =>
        ShowSplashColorFlyout(sender, static vm => vm.SplashCaptionColorHex, static (vm, hex) => vm.SplashCaptionColorHex = hex);

    private void OnSplashSpinnerSwatchClick(object? sender, RoutedEventArgs e) =>
        ShowSplashColorFlyout(sender, static vm => vm.SplashSpinnerColorHex, static (vm, hex) => vm.SplashSpinnerColorHex = hex);

    /// <summary>Opens a color-picker flyout on a splash swatch button — the
    /// controller path to these colors, because gamepad navigation deliberately
    /// skips the paired hex TextBoxes. The picker starts on the row's current
    /// color and writes every change back through <paramref name="setHex"/>, so
    /// the swatch and TextBox update live; alpha is disabled to match the
    /// "#RRGGBB" splash color format. The flyout hosts the full picker panel
    /// (<see cref="ColorView"/>, the accent <see cref="ColorPicker"/>'s base
    /// class) directly — a nested ColorPicker would put a second drop-down
    /// button inside the flyout.</summary>
    private void ShowSplashColorFlyout(
        object? sender, Func<SettingsViewModel, string> getHex, Action<SettingsViewModel, string> setHex)
    {
        if (_viewModel is not { } viewModel || sender is not Button anchor)
        {
            return;
        }
        var picker = new ColorView { IsAlphaEnabled = false };
        if (Color.TryParse(getHex(viewModel), out var current))
        {
            picker.Color = current;
        }
        picker.ColorChanged += (_, args) =>
            setHex(viewModel, $"#{args.NewColor.R:X2}{args.NewColor.G:X2}{args.NewColor.B:X2}");
        new Flyout { Content = picker }.ShowAt(anchor);
    }

    // --- Splash presets ---
    private void OnApplyPreset(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }
        var index = PresetCombo.SelectedIndex;
        if (index < 0 || index >= SplashPresets.All.Count)
        {
            return;
        }
        var preset = SplashPresets.All[index];
        _viewModel.LoadSplash(SplashPresets.Create(preset));
        _viewModel.StatusText = $"Preset '{SplashPresets.DisplayName(preset)}' applied — Save changes to keep it.";
    }

    // --- Splash images ---
    private void RefreshLogoThumbnail() =>
        _logoThumbBitmap = RefreshThumbnail(_viewModel?.SplashLogoPath, LogoThumb, LogoNone, _logoThumbBitmap);

    private void RefreshBackgroundThumbnail() =>
        _backgroundThumbBitmap = RefreshThumbnail(
            _viewModel?.SplashBackgroundImagePath, BackgroundThumb, BackgroundNone, _backgroundThumbBitmap);

    /// <summary>Loads one inline thumbnail; a missing or unreadable file shows the
    /// "NONE" placeholder instead. The previous bitmap is disposed only after the
    /// Image stopped referencing it.</summary>
    private static Bitmap? RefreshThumbnail(string? path, Image image, TextBlock placeholder, Bitmap? previous)
    {
        Bitmap? bitmap = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                bitmap = new Bitmap(path);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Appearance: couldn't load image thumbnail '{path}': {ex.Message}");
        }
        image.Source = bitmap;
        image.IsVisible = bitmap is not null;
        placeholder.IsVisible = bitmap is null;
        previous?.Dispose();
        return bitmap;
    }

    private async void OnBrowseLogo(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not null && await PickImageAsync("Select logo image") is { } path)
        {
            _viewModel.SplashLogoPath = path;
        }
    }

    private void OnClearLogo(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.SplashLogoPath = "";
        }
    }

    private async void OnBrowseBackground(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not null && await PickImageAsync("Select background image") is { } path)
        {
            _viewModel.SplashBackgroundImagePath = path;
        }
    }

    private void OnClearBackground(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.SplashBackgroundImagePath = "";
        }
    }

    private async Task<string?> PickImageAsync(string title)
    {
        if (TopLevel.GetTopLevel(this) is not { } topLevel)
        {
            return null;
        }
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Images") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp"] },
            ],
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    // --- Actions ---
    private void OnPreviewSplash(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }
        try
        {
            if (TopLevel.GetTopLevel(this) is SettingsWindow window)
            {
                window.ShowSplashPreview(_viewModel.BuildSplashConfig());
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Splash preview failed: {ex.Message}");
            _viewModel.StatusText = $"Preview failed: {ex.Message}";
        }
    }

    private async void OnExportSplash(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || TopLevel.GetTopLevel(this) is not { } topLevel)
        {
            return;
        }
        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export splash theme",
            SuggestedFileName = "my-splash.wsgmsplash",
            DefaultExtension = "wsgmsplash",
            FileTypeChoices =
            [
                new FilePickerFileType("WSGM splash theme") { Patterns = ["*.wsgmsplash"] },
            ],
        });
        var path = file?.TryGetLocalPath();
        if (path is null)
        {
            return;
        }
        _viewModel.StatusText = SplashTheme.Export(_viewModel.BuildSplashConfig(), path)
            ? $"Splash theme exported to {path}"
            : "Splash theme export failed — see wsgm.log for details.";
    }

    private async void OnImportSplash(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || TopLevel.GetTopLevel(this) is not { } topLevel)
        {
            return;
        }
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import splash theme",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("WSGM splash theme") { Patterns = ["*.wsgmsplash"] },
            ],
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path is null)
        {
            return;
        }
        // Imported images land in a per-import staging directory; Save's ordinary
        // SplashAssets.Materialize copies them into the stable splash assets — the
        // live copies stay untouched until the user actually saves.
        var imported = SplashTheme.Import(path);
        if (imported is null)
        {
            _viewModel.StatusText = "Couldn't import: not a readable splash theme (see wsgm.log).";
            return;
        }
        _viewModel.LoadSplash(imported);
        _viewModel.StatusText = "Splash theme imported — Save changes to keep it.";
    }
}
