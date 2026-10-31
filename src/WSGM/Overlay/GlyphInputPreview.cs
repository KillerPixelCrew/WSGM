using System;
using System.Collections.Generic;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using WSGM.Controls;
using WSGM.Device.Sdk.Glyphs;
using WSGM.Shell;
using CanonicalControllerSample = LibHandheld.Contracts.CanonicalControllerSample;

namespace WSGM.Overlay;

/// <summary>
///     The Controller page's glyph preview and input test: the plugin's own glyphs as tiles, with
///     the one being pressed lit.
/// </summary>
/// <remarks>
///     The preview answers the two questions a glyph profile can fail at, and it answers them with
///     the same picture: whether the artwork resolves at all, and whether pressing a control reaches
///     WSGM as the control the artwork claims. Neither is answerable from a list of names.
///     <para>
///         The control holds the high-rate physical sample lease itself. The window keeps one
///         instance, places it on the Controller page when that page renders and asks it to observe
///         only while a Controller page is open.
///     </para>
///     <para>
///         The tiles are not focusable. This is something to look at while pressing buttons on the
///         device, so making it a focus stop would put a wall of stops between the selection row above
///         it and whatever follows, for controls that do nothing when activated.
///     </para>
/// </remarks>
internal sealed class GlyphInputPreview : StackPanel
{
    private readonly HashSet<GlyphControlId> _oemPressed = [];

    /// <summary>Preview tiles by control, rebuilt with each preview.</summary>
    /// <remarks>
    ///     Held so the input test can light a tile without re-rendering the page on every sample.
    /// </remarks>
    private readonly Dictionary<GlyphControlId, Border> _tiles = [];

    private IDeviceOverlaySource? _bridge;
    private long _inputKey;
    private IDisposable? _observation;

    // Advanced on release and reset; an OEM event posted under an older epoch is dropped.
    private int _oemEpoch;

    /// <inheritdoc />
    protected override Type StyleKeyOverride => typeof(StackPanel);

    /// <summary>Draws a preview for a group's body, taking the control out of any earlier group.</summary>
    /// <param name="preview">The resolved preview.</param>
    /// <returns>This control, for the group to place after the glyph selection.</returns>
    internal GlyphInputPreview Show(DeviceOverlayGlyphPreview preview)
    {
        (Parent as Panel)?.Children.Remove(this);
        // A group body's own gap, so caption, hint and tiles keep the spacing they would have as its rows.
        Spacing = 8;
        Children.Clear();
        _tiles.Clear();
        TextBlock caption = new()
        {
            Text = $"{preview.ProfileName} · {preview.Detail}",
            Margin = new Thickness(2, 6, 2, 2),
            TextWrapping = TextWrapping.Wrap
        };
        caption.Classes.Add("caption");
        Children.Add(caption);

        TextBlock hint = new()
        {
            Text = preview.InputTestAvailable
                ? "Press a control on the device to light it here."
                : "Input test unavailable · WSGM is not reading this device's controls.",
            Margin = new Thickness(2, 0, 2, 4),
            TextWrapping = TextWrapping.Wrap
        };
        hint.Classes.Add("caption");
        Children.Add(hint);

        WrapPanel tiles = new() { Margin = new Thickness(2, 0, 2, 4) };
        foreach (var item in preview.Items)
        {
            Border tile = new()
            {
                Width = 64,
                Height = 72,
                Margin = new Thickness(0, 0, 6, 6),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(4)
            };
            tile.Classes.Add("glyph-tile");
            StackPanel face = new() { Spacing = 2 };
            face.Children.Add(new PhysicalGlyphImage
            {
                Plan = item.Plan,
                Width = 40,
                Height = 40,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            face.Children.Add(new TextBlock
            {
                Text = item.Label,
                FontSize = 10,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 2,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            tile.Child = face;
            tiles.Children.Add(tile);
            _tiles[item.Control] = tile;
        }

        Children.Add(tiles);
        Apply();
        return this;
    }

    /// <summary>Starts or stops the input test's sample observation.</summary>
    /// <param name="bridge">The device projection to observe, or null to release the lease.</param>
    /// <remarks>
    ///     Idempotent in both directions, because a Controller page can be entered and left by several
    ///     paths — the section card, Back, a destination change, another bridge and the overlay
    ///     closing — and each of them calls this without knowing what the others did. The lease is
    ///     released against the bridge that granted it.
    /// </remarks>
    internal void Observe(IDeviceOverlaySource? bridge)
    {
        if (ReferenceEquals(bridge, _bridge))
        {
            return;
        }

        if (_bridge is { } previous)
        {
            Interlocked.Increment(ref _oemEpoch);
            previous.PhysicalSampleReceived -= OnSample;
            previous.PhysicalOemControlReceived -= OnOemControl;
            previous.PhysicalInputReset -= OnReset;
            _observation?.Dispose();
            _observation = null;
            _bridge = null;
            Volatile.Write(ref _inputKey, 0);
            _oemPressed.Clear();
        }

        if (bridge is null)
        {
            return;
        }

        bridge.PhysicalSampleReceived += OnSample;
        bridge.PhysicalOemControlReceived += OnOemControl;
        bridge.PhysicalInputReset += OnReset;
        _observation = bridge.ObservePhysicalSamples();
        _bridge = bridge;
    }

    /// <summary>Applies the last physical sample to the preview tiles.</summary>
    /// <remarks>
    ///     Class-based rather than by setting a brush, so the lit appearance lives in the theme with
    ///     every other visual state instead of as a literal colour here.
    /// </remarks>
    private void Apply()
    {
        var key = Volatile.Read(ref _inputKey);
        foreach (var (control, tile) in _tiles)
        {
            tile.Classes.Set("pressed", GlyphInputTestMap.Lights(key, control) || _oemPressed.Contains(control));
        }
    }

    private void OnOemControl(GlyphControlId control, bool pressed)
    {
        var epoch = Volatile.Read(ref _oemEpoch);
        Dispatcher.UIThread.Post(() =>
        {
            if (_observation is null || epoch != Volatile.Read(ref _oemEpoch))
            {
                return;
            }

            if (pressed)
            {
                _oemPressed.Add(control);
            }
            else
            {
                _oemPressed.Remove(control);
            }

            Apply();
        });
    }

    private void OnReset()
    {
        var epoch = Interlocked.Increment(ref _oemEpoch);
        Dispatcher.UIThread.Post(() =>
        {
            if (_observation is null || epoch != Volatile.Read(ref _oemEpoch))
            {
                return;
            }

            _oemPressed.Clear();
            Volatile.Write(ref _inputKey, 0);
            Apply();
        });
    }

    /// <summary>Marshals one physical sample onto the UI thread and lights what it presses.</summary>
    /// <param name="sample">The unfiltered sample the plugin reported.</param>
    /// <remarks>
    ///     The sample's pressed-input key is compared with the last one before posting, so a controller
    ///     sitting still, which is most samples, costs one integer comparison on the sampling thread and
    ///     nothing on the UI thread. Without that, a 250 Hz stream would post 250 dispatcher items a
    ///     second to change nothing.
    /// </remarks>
    private void OnSample(CanonicalControllerSample sample)
    {
        var key = GlyphInputTestMap.Key(sample);
        if (key == Volatile.Read(ref _inputKey))
        {
            return;
        }

        Volatile.Write(ref _inputKey, key);
        Dispatcher.UIThread.Post(() =>
        {
            if (_observation is not null)
            {
                Apply();
            }
        });
    }
}
