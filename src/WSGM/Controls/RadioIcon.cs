using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace WSGM.Controls;

/// <summary>What a radio icon is currently saying.</summary>
public enum RadioIconState
{
    /// <summary>The radio is off, or absent. Drawn with a slash through it.</summary>
    Off,

    /// <summary>The radio is on but nothing is connected. Drawn muted.</summary>
    Disconnected,

    /// <summary>Connected. Drawn in the accent color.</summary>
    Connected,
}

/// <summary>A Wi-Fi or Bluetooth status icon that shows its state at a glance.
///
/// Three states, because "is it off, is it just not connected, or is it working"
/// are three different problems and a single glyph cannot tell them apart: off
/// is struck through, disconnected is muted, connected takes the accent color.
/// Wi-Fi additionally fills its arcs by signal strength, the way Windows does —
/// a connection at 20% and one at 100% are not the same thing to a user standing
/// at the edge of range.
///
/// Drawn rather than assembled from <see cref="Icons"/> geometries because the
/// arcs have to be lit individually, which a single path cannot express.</summary>
public sealed class RadioIcon : Control
{
    /// <summary>Defines the <see cref="State"/> property.</summary>
    public static readonly StyledProperty<RadioIconState> StateProperty =
        AvaloniaProperty.Register<RadioIcon, RadioIconState>(nameof(State));

    /// <summary>Defines the <see cref="Signal"/> property.</summary>
    public static readonly StyledProperty<int> SignalProperty =
        AvaloniaProperty.Register<RadioIcon, int>(nameof(Signal));

    /// <summary>Defines the <see cref="Bluetooth"/> property.</summary>
    public static readonly StyledProperty<bool> BluetoothProperty =
        AvaloniaProperty.Register<RadioIcon, bool>(nameof(Bluetooth));

    /// <summary>Defines the <see cref="Accent"/> property.</summary>
    public static readonly StyledProperty<IBrush?> AccentProperty =
        AvaloniaProperty.Register<RadioIcon, IBrush?>(nameof(Accent));

    /// <summary>Defines the <see cref="Muted"/> property.</summary>
    public static readonly StyledProperty<IBrush?> MutedProperty =
        AvaloniaProperty.Register<RadioIcon, IBrush?>(nameof(Muted));

    static RadioIcon()
    {
        // Every one of these changes what is drawn, so each must invalidate.
        AffectsRender<RadioIcon>(
            StateProperty, SignalProperty, BluetoothProperty, AccentProperty, MutedProperty);
    }

    /// <summary>Gets or sets what the icon is reporting.</summary>
    public RadioIconState State
    {
        get => GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    /// <summary>Gets or sets the signal quality, 0-100. Wi-Fi only.</summary>
    public int Signal
    {
        get => GetValue(SignalProperty);
        set => SetValue(SignalProperty, value);
    }

    /// <summary>Gets or sets whether this is the Bluetooth rune rather than the
    /// Wi-Fi fan.</summary>
    public bool Bluetooth
    {
        get => GetValue(BluetoothProperty);
        set => SetValue(BluetoothProperty, value);
    }

    /// <summary>Gets or sets the brush used when connected.</summary>
    public IBrush? Accent
    {
        get => GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    /// <summary>Gets or sets the brush used when off or disconnected.</summary>
    public IBrush? Muted
    {
        get => GetValue(MutedProperty);
        set => SetValue(MutedProperty, value);
    }

    /// <summary>How many of the three Wi-Fi arcs are lit for a signal quality.
    ///
    /// Windows reports 0-100 and shows four levels; the arcs here are the outer
    /// three, so the thresholds split the range into thirds with a dead band at
    /// the bottom — a network at 5% should not look like a usable one.</summary>
    internal static int ArcsForSignal(int signal) => signal switch
    {
        >= 70 => 3,
        >= 40 => 2,
        >= 10 => 1,
        _ => 0,
    };

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0)
        {
            return;
        }
        var accent = Accent ?? Brushes.White;
        var muted = Muted ?? Brushes.Gray;
        var on = State == RadioIconState.Connected;
        var lit = on ? accent : muted;
        // A disconnected icon is dimmer than an off one is struck: the off state
        // keeps full contrast so the slash is legible.
        var dim = new ImmutableSolidColorBrush(
            (muted as ISolidColorBrush)?.Color ?? Colors.Gray,
            State == RadioIconState.Disconnected ? 0.45 : 1.0);

        var origin = new Point((Bounds.Width - size) / 2, (Bounds.Height - size) / 2);
        if (Bluetooth)
        {
            DrawBluetooth(context, origin, size, lit, dim, on);
        }
        else
        {
            DrawWifi(context, origin, size, accent, muted, dim, on);
        }

        if (State == RadioIconState.Off)
        {
            // The slash reads as "off" only if it clearly crosses the glyph.
            var pen = new Pen(muted, size * 0.09, lineCap: PenLineCap.Round);
            context.DrawLine(
                pen,
                new Point(origin.X + (size * 0.16), origin.Y + (size * 0.16)),
                new Point(origin.X + (size * 0.84), origin.Y + (size * 0.84)));
        }
    }

    private void DrawWifi(
        DrawingContext context,
        Point origin,
        double size,
        IBrush accent,
        IBrush muted,
        IBrush dim,
        bool connected)
    {
        var centre = new Point(origin.X + (size / 2), origin.Y + (size * 0.82));
        var arcs = State == RadioIconState.Off ? 0 : ArcsForSignal(Signal);
        // The dot is the base station: always drawn, so an icon with no bars is
        // still recognisably Wi-Fi rather than an empty box.
        var dotBrush = State == RadioIconState.Off ? muted : (connected ? accent : dim);
        context.DrawEllipse(dotBrush, null, centre, size * 0.07, size * 0.07);

        for (var i = 0; i < 3; i++)
        {
            var radius = size * (0.22 + (i * 0.17));
            var isLit = i < arcs;
            var brush = State == RadioIconState.Off
                ? muted
                : isLit ? (connected ? accent : muted) : dim;
            var pen = new Pen(brush, size * 0.085, lineCap: PenLineCap.Round);
            // A 120-degree fan centred on straight up, which is the Windows shape.
            var geometry = new StreamGeometry();
            using (var sink = geometry.Open())
            {
                var start = PointOnArc(centre, radius, 210);
                sink.BeginFigure(start, false);
                sink.ArcTo(
                    PointOnArc(centre, radius, 330),
                    new Size(radius, radius),
                    0,
                    false,
                    SweepDirection.Clockwise);
                sink.EndFigure(false);
            }
            context.DrawGeometry(null, pen, geometry);
        }
    }

    private static Point PointOnArc(Point centre, double radius, double degrees)
    {
        var radians = degrees * Math.PI / 180.0;
        return new Point(
            centre.X + (radius * Math.Cos(radians)),
            centre.Y + (radius * Math.Sin(radians)));
    }

    private void DrawBluetooth(
        DrawingContext context, Point origin, double size, IBrush lit, IBrush dim, bool connected)
    {
        var brush = State == RadioIconState.Off ? lit : (connected ? lit : dim);
        var pen = new Pen(brush, size * 0.09, lineCap: PenLineCap.Round,
            lineJoin: PenLineJoin.Round);
        // The standard rune: a vertical stroke with two bowties crossing it.
        var x = origin.X + (size / 2);
        var top = origin.Y + (size * 0.12);
        var bottom = origin.Y + (size * 0.88);
        var wing = size * 0.22;

        var geometry = new StreamGeometry();
        using (var sink = geometry.Open())
        {
            sink.BeginFigure(new Point(x - wing, origin.Y + (size * 0.30)), false);
            sink.LineTo(new Point(x + wing, origin.Y + (size * 0.70)));
            sink.LineTo(new Point(x, bottom));
            sink.LineTo(new Point(x, top));
            sink.LineTo(new Point(x + wing, origin.Y + (size * 0.30)));
            sink.LineTo(new Point(x - wing, origin.Y + (size * 0.70)));
            sink.EndFigure(false);
        }
        context.DrawGeometry(null, pen, geometry);
    }
}
