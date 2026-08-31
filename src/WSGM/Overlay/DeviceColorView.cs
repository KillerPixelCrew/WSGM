using System;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using WSGM.Controls;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>A bounded, controller-driven editor for one device lighting-zone color.</summary>
/// <remarks>
/// The editor stages every change locally and writes only when Apply is pressed. That is required
/// for device lighting whose firmware persists every commit: navigating a picker must not stream
/// writes into non-volatile profile memory. Presets and coarse channel steps keep the page usable
/// with a gamepad; the overlay keyboard remains available for an exact hexadecimal value.
/// </remarks>
public sealed class DeviceColorView : OverlaySubView
{
    private static readonly (string Name, int Value)[] Presets =
    [
        ("White", 0xFFFFFF),
        ("Red", 0xFF0000),
        ("Orange", 0xFF8000),
        ("Yellow", 0xFFFF00),
        ("Green", 0x00FF00),
        ("Cyan", 0x00FFFF),
        ("Blue", 0x0000FF),
        ("Purple", 0x8000FF),
        ("Pink", 0xFF0080),
        ("Off", 0x000000),
    ];

    private IDeviceOverlaySource? _source;
    private DeviceOverlayCapability? _capability;
    private int _initialColor;
    private int _color;
    private bool _applying;

    /// <inheritdoc />
    protected override string LogScope => "Device color";

    /// <summary>Stages the capability's observed color and opens its editor.</summary>
    /// <param name="source">The device source that owns command execution.</param>
    /// <param name="capability">A writable color capability.</param>
    internal void Open(IDeviceOverlaySource source, DeviceOverlayCapability capability)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(capability);
        if (capability.CurrentValue is not
            { Kind: CapabilityValueKind.Color, ColorValue: { } color })
        {
            throw new ArgumentException("The capability has no observed color.", nameof(capability));
        }

        _source = source;
        _capability = capability;
        _initialColor = color & 0xFFFFFF;
        _color = _initialColor;
        _applying = false;
        _stack.Clear();
        _current = null;
        Navigate(Render);
    }

    private void Render()
    {
        DeviceOverlayCapability? capability = _capability;
        if (capability is null)
        {
            RenderMessage("Lighting color", "The device color is no longer available.");
            return;
        }

        var stack = NewStack(capability.Title);
        stack.Children.Add(new Border
        {
            Height = 48,
            Margin = new Thickness(2, 0, 2, 4),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(ToAvaloniaColor(_color)),
            BorderBrush = Brushes.White,
            BorderThickness = new Thickness(1),
        });
        stack.Children.Add(Caption(
            $"#{_color:X6} · changes are written only when Apply is pressed."));

        stack.Children.Add(SectionLabel("PRESETS"));
        foreach ((string name, int value) in Presets)
        {
            int selected = value;
            stack.Children.Add(Row(
                name,
                $"#{selected:X6}",
                Icons.Palette,
                _applying ? null : () => SetColor(selected)));
        }

        stack.Children.Add(SectionLabel("CHANNELS"));
        stack.Children.Add(CycleRow("Red", Channel(16).ToString(CultureInfo.CurrentCulture),
            () => CycleChannelAt(16)));
        stack.Children.Add(CycleRow("Green", Channel(8).ToString(CultureInfo.CurrentCulture),
            () => CycleChannelAt(8)));
        stack.Children.Add(CycleRow("Blue", Channel(0).ToString(CultureInfo.CurrentCulture),
            () => CycleChannelAt(0)));
        stack.Children.Add(Row(
            "Exact hexadecimal color",
            $"#{_color:X6}",
            Icons.Wrench,
            _applying ? null : EditHex));

        stack.Children.Add(SectionLabel(""));
        stack.Children.Add(PrimaryRow(
            _applying ? "Applying…" : "Apply",
            "Commit this color to the device",
            Icons.Play,
            () =>
            {
                if (!_applying)
                {
                    _ = RunSafelyAsync(ApplyAsync(), "apply");
                }
            }));
        stack.Children.Add(Row("Cancel", "Discard the staged color", Icons.ExitFullscreen,
            _applying ? null : () => Back()));
        SetContent(stack);
    }

    private int Channel(int shift) => (_color >> shift) & 0xFF;

    private void SetColor(int value)
    {
        _color = value & 0xFFFFFF;
        Replace(Render);
    }

    private void CycleChannelAt(int shift)
    {
        int mask = 0xFF << shift;
        int next = CycleChannel(Channel(shift));
        SetColor((_color & ~mask) | (next << shift));
    }

    private void EditHex() => EditText(
        "Lighting color (#RRGGBB)",
        $"#{_color:X6}",
        7,
        value =>
        {
            if (TryParseColor(value, out int color))
            {
                _color = color;
            }
            else
            {
                Toast("Enter six hexadecimal digits, for example #FF8000.");
            }
        });

    private async Task ApplyAsync()
    {
        IDeviceOverlaySource? source = _source;
        DeviceOverlayCapability? capability = _capability;
        if (source is null || capability is null)
        {
            return;
        }
        if (_color == _initialColor)
        {
            RequestClose();
            return;
        }

        _applying = true;
        Replace(Render);
        bool applied = false;
        try
        {
            await source.InvokeAsync(capability with
            {
                NextValue = new CapabilityValue
                {
                    Kind = CapabilityValueKind.Color,
                    ColorValue = _color,
                },
            }).ConfigureAwait(true);
            applied = true;
        }
        finally
        {
            _applying = false;
            if (applied)
            {
                RequestClose();
            }
            else
            {
                Replace(Render);
            }
        }
    }

    internal static int CycleChannel(int value) => value >= 255 ? 0 : Math.Min(255, value + 17);

    internal static bool TryParseColor(string? text, out int color)
    {
        color = 0;
        string candidate = (text ?? string.Empty).Trim();
        if (candidate.StartsWith('#'))
        {
            candidate = candidate[1..];
        }

        return candidate.Length == 6
            && int.TryParse(candidate, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture,
                out color)
            && color is >= 0 and <= 0xFFFFFF;
    }

    private static Color ToAvaloniaColor(int color) => Color.FromRgb(
        (byte)((color >> 16) & 0xFF),
        (byte)((color >> 8) & 0xFF),
        (byte)(color & 0xFF));
}
