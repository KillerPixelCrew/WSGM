using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using WSGM.Core;

namespace WSGM.Controls;

/// <summary>How touch, pen and mouse act on a <see cref="NotesCanvas" />.</summary>
public enum NotesCanvasMode
{
    /// <summary>Select, move and resize elements; drag empty page to scroll.</summary>
    Navigate,

    /// <summary>Tap a text block to edit it, or empty page to add one there; drag to scroll.</summary>
    Text,

    /// <summary>Every contact draws or erases; nothing scrolls and no overlay gesture starts.</summary>
    Ink
}

/// <summary>The ink tool in <see cref="NotesCanvasMode.Ink" />.</summary>
public enum NotesInkTool
{
    /// <summary>An opaque stroke.</summary>
    Pen,

    /// <summary>A translucent stroke.</summary>
    Highlighter,

    /// <summary>Removes every stroke it touches.</summary>
    Eraser
}

/// <summary>
///     Draws one Notes page — text blocks, images and vector ink — and turns touch, pen and mouse into
///     editing intents.
/// </summary>
/// <remarks>
///     <para>
///         Presentation only: the control never changes <see cref="Page" />. A finished stroke, an erase, a
///         move or a text request is raised as an event, and the owner saves it and supplies the new page.
///     </para>
///     <para>
///         In <see cref="NotesCanvasMode.Ink" /> the control captures each contact and stops gesture
///         recognition, so a stroke can neither scroll the overlay nor start any of its gestures. Leaving Ink
///         mode gives touch back to scrolling and navigation. Strokes are captured at the digitizer's rate,
///         including intermediate points, and simplified once when the contact lifts.
///     </para>
/// </remarks>
public sealed class NotesCanvas : Control
{
    /// <summary>The page shown.</summary>
    public static readonly StyledProperty<NotePage?> PageProperty =
        AvaloniaProperty.Register<NotesCanvas, NotePage?>(nameof(Page));

    /// <summary>How input acts.</summary>
    public static readonly StyledProperty<NotesCanvasMode> ModeProperty =
        AvaloniaProperty.Register<NotesCanvas, NotesCanvasMode>(nameof(Mode));

    /// <summary>The ink tool.</summary>
    public static readonly StyledProperty<NotesInkTool> InkToolProperty =
        AvaloniaProperty.Register<NotesCanvas, NotesInkTool>(nameof(InkTool));

    /// <summary>The ink color as <c>#RRGGBB</c>.</summary>
    public static readonly StyledProperty<string> InkColorProperty =
        AvaloniaProperty.Register<NotesCanvas, string>(nameof(InkColor), "#FFFFFF");

    /// <summary>The ink width in page units.</summary>
    public static readonly StyledProperty<double> InkWidthProperty =
        AvaloniaProperty.Register<NotesCanvas, double>(nameof(InkWidth), 4);

    /// <summary>Zoom over the page's fit-to-width scale, 1 to 4.</summary>
    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<NotesCanvas, double>(nameof(Zoom), 1, coerce: (_, value) =>
            double.IsFinite(value) ? Math.Clamp(value, 1, 4) : 1);

    /// <summary>The selected element, or null.</summary>
    public static readonly StyledProperty<string?> SelectedElementProperty =
        AvaloniaProperty.Register<NotesCanvas, string?>(nameof(SelectedElement));

    private const double HandleSize = 28;
    private const double EraserRadius = 14;
    private const double TapSlop = 8;
    private static readonly TimeSpan DoubleTap = TimeSpan.FromMilliseconds(450);

    private readonly Dictionary<string, Geometry> _geometry = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _textHeights = new(StringComparer.Ordinal);
    private readonly List<float> _livePoints = [];
    private readonly List<float> _livePressure = [];
    private readonly HashSet<string> _erased = new(StringComparer.Ordinal);
    private Drag _drag;
    private string? _dragId;
    private Rect _dragRect;
    private Point _dragStart;
    private Point _scrollStart;
    private bool _moved;
    private DateTime _lastTapAt;
    private string? _lastTapId;
    private NotePage? _geometryPage;
    private Point _scroll;

    static NotesCanvas()
    {
        AffectsRender<NotesCanvas>(PageProperty, ModeProperty, ZoomProperty, SelectedElementProperty);
        ClipToBoundsProperty.OverrideDefaultValue<NotesCanvas>(true);
    }

    /// <summary>The page shown.</summary>
    public NotePage? Page
    {
        get => GetValue(PageProperty);
        set => SetValue(PageProperty, value);
    }

    /// <summary>How input acts.</summary>
    public NotesCanvasMode Mode
    {
        get => GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    /// <summary>The ink tool.</summary>
    public NotesInkTool InkTool
    {
        get => GetValue(InkToolProperty);
        set => SetValue(InkToolProperty, value);
    }

    /// <summary>The ink color as <c>#RRGGBB</c>.</summary>
    public string InkColor
    {
        get => GetValue(InkColorProperty);
        set => SetValue(InkColorProperty, value);
    }

    /// <summary>The ink width in page units.</summary>
    public double InkWidth
    {
        get => GetValue(InkWidthProperty);
        set => SetValue(InkWidthProperty, value);
    }

    /// <summary>Zoom over the fit-to-width scale.</summary>
    public double Zoom
    {
        get => GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, value);
    }

    /// <summary>The selected element, or null.</summary>
    public string? SelectedElement
    {
        get => GetValue(SelectedElementProperty);
        set => SetValue(SelectedElementProperty, value);
    }

    /// <summary>Supplies the decoded image for an asset id, or null while it loads or when it is missing.</summary>
    public Func<string, IImage?>? ImageSource { get; set; }

    /// <summary>The scroll position in page units.</summary>
    public Point Scroll
    {
        get => _scroll;
        set
        {
            _scroll = ClampScroll(value);
            InvalidateVisual();
        }
    }

    /// <summary>Raised with a finished pen or highlighter stroke in page units.</summary>
    public event Action<NoteStroke>? StrokeCompleted;

    /// <summary>Raised with the strokes one eraser contact touched.</summary>
    public event Action<IReadOnlyCollection<string>>? StrokesErased;

    /// <summary>Raised when a move or resize ends: element id and its new rectangle in page units.</summary>
    public event Action<string, Rect>? ElementPlaced;

    /// <summary>Raised when the selection changes by touch or mouse.</summary>
    public event Action<string?>? ElementSelected;

    /// <summary>Raised to edit a text block, or with null to add one at a page point.</summary>
    public event Action<string?, Point>? TextRequested;

    /// <summary>Raised after scrolling, so the owner can keep the place.</summary>
    public event Action<Point>? Scrolled;

    /// <summary>Forgets drawn stroke geometry, for example after images or the page identity changed.</summary>
    public void Refresh()
    {
        InvalidateVisual();
    }

    /// <summary>Scrolls by a number of page units, for controller and button paging.</summary>
    /// <param name="delta">The distance.</param>
    public void ScrollBy(Vector delta)
    {
        Scroll = _scroll + delta;
        Scrolled?.Invoke(_scroll);
    }

    /// <summary>The page point at the middle of the visible area, where new content goes.</summary>
    /// <returns>The point in page units.</returns>
    public Point VisibleCenter()
    {
        var scale = Scale();
        return scale <= 0
            ? new Point(40, 40)
            : new Point(_scroll.X + Bounds.Width / scale / 2, _scroll.Y + Bounds.Height / scale / 3);
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PageProperty && !ReferenceEquals(change.OldValue, change.NewValue))
        {
            // A new revision is a new object: rebuild stroke geometry and text extents lazily against it.
            _geometryPage = null;
            _textHeights.Clear();
            if (change.NewValue is NotePage page && _dragId is not null
                                                 && page.Elements.TrueForAll(element => element.Id != _dragId))
            {
                CancelDrag();
            }
        }
        else if (change.Property == ModeProperty)
        {
            CancelDrag();
        }
        else if (change.Property == ZoomProperty)
        {
            _scroll = ClampScroll(_scroll);
        }
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Resolve("DeckCanvasBrush", Brushes.Black), bounds);
        if (Page is not { } page)
        {
            return;
        }

        var scale = Scale();
        if (scale <= 0)
        {
            return;
        }

        var text = Resolve("DeckTextBrush", Brushes.White);
        var group = Resolve("DeckGroupBrush", Brushes.DimGray);
        var accent = Resolve("HcAccentBrush", Brushes.DodgerBlue);
        var secondary = Resolve("DeckSecondaryBrush", Brushes.Gray);
        using (context.PushTransform(Matrix.CreateTranslation(-_scroll.X, -_scroll.Y) * Matrix.CreateScale(scale, scale)))
        {
            context.FillRectangle(Resolve("DeckSurfaceBrush", Brushes.Black),
                new Rect(0, 0, NotePage.PageWidth, NoteDocumentRules.ContentHeight(page)));
            foreach (var element in page.Elements)
            {
                var rect = element.Id == _dragId && _drag is Drag.Move or Drag.Resize
                    ? _dragRect
                    : Extent(element);
                if (element.Kind == NoteElement.ImageKind)
                {
                    DrawImage(context, element, rect, group, secondary);
                }
                else
                {
                    rect = DrawText(context, element, rect, text, group);
                }

                if (element.Id == SelectedElement)
                {
                    context.DrawRectangle(new Pen(accent, 3 / scale), rect.Inflate(4 / scale));
                    var handle = HandleSize / scale;
                    context.FillRectangle(accent, new Rect(rect.Right - handle / 2, rect.Bottom - handle / 2,
                        handle, handle));
                }
            }

            DrawStrokes(context, page);
            DrawLiveStroke(context);
        }
    }

    /// <inheritdoc />
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Page is not { } page || _drag != Drag.None)
        {
            return;
        }

        var point = e.GetCurrentPoint(this);
        var position = ToPage(point.Position);
        _dragStart = point.Position;
        _scrollStart = _scroll;
        _moved = false;
        if (Mode == NotesCanvasMode.Ink)
        {
            if (InkTool == NotesInkTool.Eraser)
            {
                _drag = Drag.Erase;
                _erased.Clear();
                Erase(page, position);
            }
            else
            {
                _drag = Drag.Draw;
                _livePoints.Clear();
                _livePressure.Clear();
                AddPoint(point);
            }
        }
        else if (Mode == NotesCanvasMode.Navigate && HitHandle(page, point.Position) is { } resized)
        {
            Begin(Drag.Resize, resized);
        }
        else if (Mode == NotesCanvasMode.Navigate && HitElement(page, position) is { } moved)
        {
            Begin(Drag.Move, moved);
            if (moved.Id != SelectedElement)
            {
                SelectedElement = moved.Id;
                ElementSelected?.Invoke(moved.Id);
            }
        }
        else
        {
            _drag = Drag.Pan;
            _dragId = Mode == NotesCanvasMode.Text ? HitElement(page, position)?.Id : null;
        }

        // The contact belongs to the page: the overlay's scroller and gestures never see it.
        e.Pointer.Capture(this);
        e.PreventGestureRecognition();
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_drag == Drag.None || Page is not { } page)
        {
            return;
        }

        var position = e.GetPosition(this);
        var delta = position - _dragStart;
        _moved |= Math.Abs(delta.X) > TapSlop || Math.Abs(delta.Y) > TapSlop;
        var scale = Scale();
        switch (_drag)
        {
            case Drag.Draw:
                foreach (var point in e.GetIntermediatePoints(this))
                {
                    AddPoint(point);
                }

                break;
            case Drag.Erase:
                Erase(page, ToPage(position));
                break;
            case Drag.Move:
                _dragRect = new Rect(_dragRect.Position.WithX(Original().X + delta.X / scale)
                    .WithY(Math.Max(0, Original().Y + delta.Y / scale)), _dragRect.Size);
                break;
            case Drag.Resize:
                var original = Original();
                var width = Math.Max(48, original.Width + delta.X / scale);
                var height = Original().Height * width / Math.Max(1, original.Width);
                var element = page.Elements.Find(item => item.Id == _dragId);
                if (element?.Kind == NoteElement.TextKind)
                {
                    height = Math.Max(48, original.Height + delta.Y / scale);
                }

                _dragRect = new Rect(original.X, original.Y, width, height);
                break;
            case Drag.Pan:
                _scroll = ClampScroll(_scrollStart - delta / scale);
                break;
        }

        InvalidateVisual();
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag == Drag.None)
        {
            return;
        }

        var drag = _drag;
        var id = _dragId;
        var rect = _dragRect;
        var position = ToPage(e.GetPosition(this));
        _drag = Drag.None;
        _dragId = null;
        e.Pointer.Capture(null);
        e.Handled = true;
        switch (drag)
        {
            case Drag.Draw:
                FinishStroke();
                break;
            case Drag.Erase when _erased.Count > 0:
                StrokesErased?.Invoke([.. _erased]);
                _erased.Clear();
                break;
            case Drag.Move or Drag.Resize when _moved && id is not null:
                ElementPlaced?.Invoke(id, rect);
                break;
            case Drag.Move when id is not null:
                // A tap on the selected text block twice edits it, as a double click does.
                var now = DateTime.UtcNow;
                if (_lastTapId == id && now - _lastTapAt < DoubleTap
                                     && Page?.Elements.Find(item => item.Id == id)?.Kind == NoteElement.TextKind)
                {
                    TextRequested?.Invoke(id, position);
                    _lastTapId = null;
                }
                else
                {
                    _lastTapId = id;
                    _lastTapAt = now;
                }

                break;
            case Drag.Pan when !_moved && Mode == NotesCanvasMode.Text:
                TextRequested?.Invoke(id, position);
                break;
            case Drag.Pan when !_moved && Mode == NotesCanvasMode.Navigate && SelectedElement is not null:
                SelectedElement = null;
                ElementSelected?.Invoke(null);
                break;
            case Drag.Pan when _moved:
                Scrolled?.Invoke(_scroll);
                break;
        }

        InvalidateVisual();
    }

    /// <inheritdoc />
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        // A capture taken away mid-stroke (the overlay closing, another surface) keeps nothing half-drawn.
        CancelDrag();
    }

    /// <inheritdoc />
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            Zoom += e.Delta.Y * 0.25;
        }
        else
        {
            ScrollBy(new Vector(-e.Delta.X, -e.Delta.Y) * 80 / Math.Max(0.01, Scale()));
        }

        e.Handled = true;
    }

    private enum Drag
    {
        None,
        Draw,
        Erase,
        Move,
        Resize,
        Pan
    }

    private double Scale()
    {
        return Bounds.Width <= 0 ? 0 : Bounds.Width / NotePage.PageWidth * Zoom;
    }

    private Point ToPage(Point screen)
    {
        var scale = Math.Max(0.01, Scale());
        return new Point(screen.X / scale + _scroll.X, screen.Y / scale + _scroll.Y);
    }

    private Point ClampScroll(Point scroll)
    {
        var scale = Scale();
        if (scale <= 0 || Page is not { } page)
        {
            return new Point(0, Math.Max(0, scroll.Y));
        }

        var maxX = Math.Max(0, NotePage.PageWidth - Bounds.Width / scale);
        var maxY = Math.Max(0, NoteDocumentRules.ContentHeight(page) - Bounds.Height / scale);
        return new Point(Math.Clamp(scroll.X, 0, maxX), Math.Clamp(scroll.Y, 0, maxY));
    }

    private void Begin(Drag drag, NoteElement element)
    {
        _drag = drag;
        _dragId = element.Id;
        _dragRect = Extent(element);
    }

    private Rect Original()
    {
        return Page?.Elements.Find(item => item.Id == _dragId) is { } element ? Extent(element) : _dragRect;
    }

    private void CancelDrag()
    {
        _drag = Drag.None;
        _dragId = null;
        _livePoints.Clear();
        _livePressure.Clear();
        _erased.Clear();
        InvalidateVisual();
    }

    private NoteElement? HitElement(NotePage page, Point position)
    {
        for (var index = page.Elements.Count - 1; index >= 0; index--)
        {
            var element = page.Elements[index];
            if (Extent(element).Contains(position))
            {
                return element;
            }
        }

        return null;
    }

    private NoteElement? HitHandle(NotePage page, Point screen)
    {
        if (SelectedElement is not { } selected || page.Elements.Find(item => item.Id == selected) is not { } element)
        {
            return null;
        }

        var scale = Scale();
        var extent = Extent(element);
        var corner = new Point((extent.Right - _scroll.X) * scale, (extent.Bottom - _scroll.Y) * scale);
        return Math.Abs(screen.X - corner.X) <= HandleSize && Math.Abs(screen.Y - corner.Y) <= HandleSize
            ? element
            : null;
    }

    private void AddPoint(PointerPoint point)
    {
        var position = ToPage(point.Position);
        _livePoints.Add((float)position.X);
        _livePoints.Add((float)position.Y);
        // A mouse reports a constant 0.5; only a pen's pressure is kept.
        if (point.Pointer.Type == PointerType.Pen)
        {
            _livePressure.Add(Math.Clamp(point.Properties.Pressure, 0f, 1f));
        }
    }

    private void FinishStroke()
    {
        if (_livePoints.Count < 2)
        {
            return;
        }

        var (points, pressure) = NoteDocumentRules.Simplify(_livePoints,
            _livePressure.Count * 2 == _livePoints.Count ? _livePressure : [], InkWidth);
        _livePoints.Clear();
        _livePressure.Clear();
        StrokeCompleted?.Invoke(new NoteStroke
        {
            Id = NoteDocumentRules.NewId(),
            Tool = InkTool == NotesInkTool.Highlighter ? NoteStroke.HighlighterTool : NoteStroke.PenTool,
            Color = NoteDocumentRules.IsColor(InkColor) ? InkColor : "#FFFFFF",
            Width = InkTool == NotesInkTool.Highlighter ? InkWidth * 3 : InkWidth,
            Points = points,
            Pressure = pressure
        });
    }

    private void Erase(NotePage page, Point position)
    {
        foreach (var id in NoteDocumentRules.StrokesAt(page, position.X, position.Y, EraserRadius / Math.Max(0.01, Scale())))
        {
            _erased.Add(id);
        }
    }

    private void DrawStrokes(DrawingContext context, NotePage page)
    {
        if (!ReferenceEquals(_geometryPage, page))
        {
            _geometry.Clear();
            _geometryPage = page;
        }

        foreach (var stroke in page.Strokes)
        {
            if (_erased.Contains(stroke.Id))
            {
                continue;
            }

            if (!_geometry.TryGetValue(stroke.Id, out var geometry))
            {
                geometry = Polyline(stroke.Points);
                _geometry[stroke.Id] = geometry;
            }

            context.DrawGeometry(null, StrokePen(stroke.Tool, stroke.Color, stroke.Width), geometry);
        }
    }

    private void DrawLiveStroke(DrawingContext context)
    {
        if (_drag != Drag.Draw || _livePoints.Count < 2)
        {
            return;
        }

        var tool = InkTool == NotesInkTool.Highlighter ? NoteStroke.HighlighterTool : NoteStroke.PenTool;
        var width = InkTool == NotesInkTool.Highlighter ? InkWidth * 3 : InkWidth;
        context.DrawGeometry(null, StrokePen(tool, InkColor, width), Polyline(_livePoints));
    }

    private static Pen StrokePen(string tool, string color, double width)
    {
        var parsed = Color.TryParse(color, out var value) ? value : Colors.White;
        var opacity = tool == NoteStroke.HighlighterTool ? 0.4 : 1;
        return new Pen(new ImmutableSolidColorBrush(parsed, opacity), width, lineCap: PenLineCap.Round,
            lineJoin: PenLineJoin.Round);
    }

    private static Geometry Polyline(IReadOnlyList<float> points)
    {
        StreamGeometry geometry = new();
        using var figure = geometry.Open();
        figure.BeginFigure(new Point(points[0], points[1]), false);
        if (points.Count == 2)
        {
            // A dot: a zero-length segment still draws the round cap.
            figure.LineTo(new Point(points[0] + 0.01, points[1]));
        }

        for (var index = 2; index + 1 < points.Count; index += 2)
        {
            figure.LineTo(new Point(points[index], points[index + 1]));
        }

        figure.EndFigure(false);
        return geometry;
    }

    private void DrawImage(DrawingContext context, NoteElement element, Rect rect, IBrush placeholder, IBrush caption)
    {
        if (element.Asset is { } asset && ImageSource?.Invoke(asset) is { } image)
        {
            context.DrawImage(image, rect);
            return;
        }

        context.FillRectangle(placeholder, rect);
        DrawLabel(context, "Image", rect, caption);
    }

    // A text block is at least its stored height and grows to show all of its text; the drawn extent is
    // what hit testing and the resize handle use.
    private Rect DrawText(DrawingContext context, NoteElement element, Rect rect, IBrush text, IBrush group)
    {
        var body = element.Text is { Length: > 0 } value ? value : " ";
        FormattedText formatted = new(body, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            Typeface.Default, 24, text) { MaxTextWidth = Math.Max(1, rect.Width - 24) };
        var extent = rect.WithHeight(Math.Max(rect.Height, formatted.Height + 16));
        _textHeights[element.Id] = extent.Height;
        context.FillRectangle(group, extent, 6);
        context.DrawText(formatted, rect.TopLeft + new Vector(12, 8));
        return extent;
    }

    private Rect Extent(NoteElement element)
    {
        var height = element.Kind == NoteElement.TextKind && _textHeights.TryGetValue(element.Id, out var measured)
            ? Math.Max(element.Height, measured)
            : element.Height;
        return new Rect(element.X, element.Y, element.Width, height);
    }

    private static void DrawLabel(DrawingContext context, string label, Rect rect, IBrush brush)
    {
        FormattedText formatted = new(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default,
            22, brush);
        context.DrawText(formatted, rect.Center - new Vector(formatted.Width / 2, formatted.Height / 2));
    }

    private IBrush Resolve(string key, IBrush fallback)
    {
        return this.TryFindResource(key, out var value) && value is IBrush brush ? brush : fallback;
    }
}
