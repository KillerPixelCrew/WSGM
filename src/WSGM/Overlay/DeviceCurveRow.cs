using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Overlay;

/// <summary>
/// A curve capability rendered as the editor it needs, with the presets a fan curve is usually set
/// from rather than drawn by hand.
/// </summary>
/// <remarks>
/// Modelled on HandheldCompanion's fan editor, which is the shape a handheld user already knows: a
/// filled graph with a draggable node per breakpoint, the live temperature marked against it, and
/// one row of preset buttons underneath. The differences are both device facts rather than design
/// choices — the nodes sit at the breakpoints the firmware actually has (six on the reference Claw,
/// not HC's fixed eleven), and their temperatures are pinned while the duties move, because those
/// breakpoints are what the fan table stores.
/// <para>
/// Writes are debounced for the same reason the slider's are: dragging a node streams values, and
/// a fan table write is a firmware round trip with a readback. The curve commits once it settles.
/// </para>
/// </remarks>
internal sealed class DeviceCurveRow : Border
{
    private static readonly TimeSpan CommitDelay = TimeSpan.FromMilliseconds(400);

    private readonly CurveEditor _editor;
    private readonly DispatcherTimer _commit;
    private readonly Action<IReadOnlyList<CurvePoint>> _onCommit;

    /// <summary>Builds the row for one curve capability.</summary>
    /// <param name="key">Stable focus key, mirrored onto the editor for focus restore.</param>
    /// <param name="title">Row heading.</param>
    /// <param name="description">Supporting line under the heading.</param>
    /// <param name="points">The curve the device currently holds.</param>
    /// <param name="markerInput">The live input to mark, or null when none is published.</param>
    /// <param name="enabled">Whether the editor accepts input.</param>
    /// <param name="onCommit">Invoked with the settled curve to write.</param>
    internal DeviceCurveRow(
        string key,
        string title,
        string description,
        IReadOnlyList<CurvePoint> points,
        int? markerInput,
        bool enabled,
        Action<IReadOnlyList<CurvePoint>> onCommit)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(onCommit);
        _onCommit = onCommit;
        _commit = new DispatcherTimer { Interval = CommitDelay };
        _commit.Tick += OnCommitTick;

        Classes.Add("tile");
        Tag = key;

        var header = new TextBlock { Text = title };
        header.Classes.Add("setting-title");

        _editor = new CurveEditor
        {
            Points = points,
            MarkerInput = markerInput,
            RisingOutput = true,
            IsEnabled = enabled,
            Focusable = enabled,
            Tag = key,
            Height = 180,
            Margin = new Thickness(0, 8, 0, 0),
        };
        _editor.CurveChanged += OnCurveChanged;

        var body = new StackPanel { Spacing = 2 };
        body.Children.Add(header);
        if (!string.IsNullOrWhiteSpace(description))
        {
            var caption = new TextBlock
            {
                Text = description,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            };
            caption.Classes.Add("caption");
            body.Children.Add(caption);
        }

        body.Children.Add(_editor);
        body.Children.Add(BuildPresets(key, enabled));
        Child = body;
    }

    /// <summary>The editor is the focus target so gamepad focus restore lands on the control.</summary>
    internal Control FocusTarget => _editor;

    /// <summary>Applies one preset to the editor and starts the commit window.</summary>
    /// <remarks>
    /// Sampled onto the curve's own breakpoints rather than replacing them: the temperatures are
    /// the firmware's, and a preset is a shape to put on them, not a different table.
    /// </remarks>
    private void ApplyPreset(FanCurvePreset preset)
    {
        IReadOnlyList<CurvePoint> sampled = FanCurvePresets.SampleOnto(preset, _editor.Points);
        if (sampled.Count == 0)
        {
            Log.Warn($"Fan preset {preset} not applied: the device published no curve to shape.");
            return;
        }

        _editor.Points = sampled;
        _commit.Stop();
        _commit.Start();
    }

    private StackPanel BuildPresets(string key, bool enabled)
    {
        var presets = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        foreach (FanCurvePreset preset in Enum.GetValues<FanCurvePreset>())
        {
            var button = new Button
            {
                Content = FanCurvePresets.Label(preset),
                IsEnabled = enabled,
                Focusable = enabled,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Tag = $"{key}.preset.{preset}",
            };
            FanCurvePreset captured = preset;
            button.Click += (_, _) => ApplyPreset(captured);
            presets.Children.Add(button);
        }

        foreach (Control child in presets.Children.OfType<Control>())
        {
            child.Width = double.NaN;
        }

        return presets;
    }

    private void OnCurveChanged(IReadOnlyList<CurvePoint> _)
    {
        // Restart the settle window on every change so a drag or a held direction commits once.
        _commit.Stop();
        _commit.Start();
    }

    private void OnCommitTick(object? sender, EventArgs e)
    {
        _commit.Stop();
        _onCommit(_editor.Points);
    }
}
