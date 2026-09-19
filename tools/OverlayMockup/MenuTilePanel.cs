using Avalonia;
using Avalonia.Controls;
using Avalonia.Labs.Panels;

namespace WSGM.OverlayMockup;

// The ScrollViewer measures content with unbounded height. Use its actual viewport to size
// menu targets, keeping a small fixed gap instead of leaving the bottom of the page empty.
internal sealed class MenuTilePanel : Decorator
{
    private readonly Control? _banner;

    private readonly FlexPanel _panel = new()
    {
        Wrap = FlexWrap.Wrap,
        ColumnSpacing = 8,
        RowSpacing = 8,
        AlignItems = AlignItems.Stretch
    };

    private readonly ScrollViewer _viewport;

    public MenuTilePanel(ScrollViewer viewport, IEnumerable<Control> tiles, Control? banner = null)
    {
        _viewport = viewport;
        _banner = banner;
        Child = _panel;
        foreach (var tile in tiles)
        {
            Flex.SetGrow(tile, 1);
            _panel.Children.Add(tile);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _viewport.PropertyChanged += ViewportChanged;
        if (_banner is not null)
        {
            _banner.SizeChanged += BannerChanged;
        }

        ResizeTiles();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _viewport.PropertyChanged -= ViewportChanged;
        if (_banner is not null)
        {
            _banner.SizeChanged -= BannerChanged;
        }

        base.OnDetachedFromVisualTree(e);
    }

    private void ViewportChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == ScrollViewer.ViewportProperty)
        {
            ResizeTiles();
        }
    }

    private void BannerChanged(object? sender, SizeChangedEventArgs e)
    {
        ResizeTiles();
    }

    private void ResizeTiles()
    {
        var width = _viewport.Viewport.Width;
        var height = _viewport.Viewport.Height - (_banner is null ? 0 : _banner.Bounds.Height + 10);
        if (_panel.Children.Count == 0 || width <= 0 || height <= 0)
        {
            return;
        }

        var maxColumns = Math.Min(4, Math.Max(1, (int)((width + _panel.ColumnSpacing) / (280 + _panel.ColumnSpacing))));
        var columns = Math.Min(maxColumns, _panel.Children.Count);
        // Prefer complete rows, so directional navigation keeps the same column below it.
        while (columns > 2 && _panel.Children.Count % columns != 0)
        {
            columns--;
        }

        var rows = (int)Math.Ceiling((double)_panel.Children.Count / columns);
        var tileWidth = Math.Floor((width - (columns - 1) * _panel.ColumnSpacing) / columns);
        var tileHeight = Math.Max(104, Math.Floor((height - (rows - 1) * _panel.RowSpacing) / rows));
        foreach (var tile in _panel.Children)
        {
            tile.MinWidth = Math.Min(280, tileWidth);
            tile.Height = tileHeight;
            Flex.SetBasis(tile, new FlexBasis(tileWidth));
        }
    }
}
