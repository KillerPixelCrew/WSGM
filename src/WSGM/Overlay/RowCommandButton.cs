using System;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;

namespace WSGM.Overlay;

/// <summary>An icon command beside a list row, such as edit, pin or delete.</summary>
internal sealed class RowCommandButton : Button, IOverlayRefreshable
{
    private Action _activate;

    /// <inheritdoc />
    protected override Type StyleKeyOverride => typeof(Button);

    /// <summary>Creates the command.</summary>
    /// <param name="tag">The semantic key.</param>
    /// <param name="name">What the command does to which item, read aloud and shown as its tooltip.</param>
    /// <param name="icon">The stroke glyph.</param>
    /// <param name="activate">What pressing it does.</param>
    /// <param name="pinned">Whether to show the accent of something pinned.</param>
    /// <param name="danger">Whether it destroys something.</param>
    internal RowCommandButton(string tag, string name, Geometry icon, Action activate, bool pinned, bool danger)
    {
        _activate = activate;
        Tag = tag;
        Classes.Add("deck-action");
        Classes.Add("row-command");
        Classes.Set("danger", danger);
        Classes.Set("pinned", pinned);
        var path = new Path
        {
            Data = icon, Height = 22, Stretch = Stretch.Uniform, StrokeThickness = 1.6,
            StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        path.Bind(Shape.StrokeProperty, new Binding(nameof(Foreground)) { Source = this });
        Content = path;
        SetName(name);
        Click += (_, _) => _activate();
    }

    /// <inheritdoc />
    public void RefreshFrom(Control replacement)
    {
        var next = (RowCommandButton)replacement;
        _activate = next._activate;
        Classes.Set("pinned", next.Classes.Contains("pinned"));
        SetName(AutomationProperties.GetName(next) ?? "");
    }

    private void SetName(string name)
    {
        AutomationProperties.SetName(this, name);
        ToolTip.SetTip(this, name);
    }
}
