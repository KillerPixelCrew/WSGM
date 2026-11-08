using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using WSGM.Input;

namespace WSGM.Overlay;

/// <summary>
///     A column of letters beside the Overlay's scrollbar that jumps a sorted list to a letter. It stays in view
///     while the pane scrolls. A press or drag scrubs the list; a controller or keyboard press also moves focus
///     to the first row of the letter.
/// </summary>
/// <remarks>
///     The pane's one ScrollViewer keeps owning scrolling, because a nested one would swallow its
///     focus-follows-scroll. The bar follows that scroller with a transform, which leaves layout alone.
/// </remarks>
internal sealed class AlphabetJumpBar : Border, IOverlayRefreshable
{
    private const string Letters = "#ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const double PanePadding = 12;
    private const double MinimumLetterHeight = 14;

    private readonly Button[] _buttons;
    private readonly TranslateTransform _follow = new();
    private readonly UniformGrid _letters = new() { Columns = 1 };
    private ScrollViewer? _scroller;
    private IReadOnlyDictionary<char, string> _targets;

    /// <summary>Creates the bar for one list.</summary>
    /// <param name="targets">Each letter that has rows, with the semantic key of its first row.</param>
    internal AlphabetJumpBar(IReadOnlyDictionary<char, string> targets)
    {
        _targets = targets;
        Width = 40;
        VerticalAlignment = VerticalAlignment.Top;
        // A fill makes the gaps between letters part of the drag surface.
        Background = Brushes.Transparent;
        RenderTransform = _follow;
        _buttons = Letters.Select(NewLetter).ToArray();
        _letters.Children.AddRange(_buttons);
        Child = _letters;
        ApplyTargets();
        AddHandler(PointerPressedEvent, OnPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnMoved, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnReleased, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    /// <summary>The letter a name files under: accents removed, anything but A to Z under #.</summary>
    /// <param name="name">The row's name.</param>
    /// <returns>A letter of the bar.</returns>
    internal static char Bucket(string name)
    {
        var first = name.Normalize(NormalizationForm.FormD)
            .FirstOrDefault(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark
                                 && !char.IsWhiteSpace(c));
        var upper = char.ToUpperInvariant(first);
        return upper is >= 'A' and <= 'Z' ? upper : '#';
    }

    /// <inheritdoc />
    public void RefreshFrom(Control replacement)
    {
        _targets = ((AlphabetJumpBar)replacement)._targets;
        ApplyTargets();
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _scroller = this.FindAncestorOfType<ScrollViewer>();
        if (_scroller is not null)
        {
            _scroller.ScrollChanged += OnScrollChanged;
            Follow();
        }
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_scroller is not null)
        {
            _scroller.ScrollChanged -= OnScrollChanged;
            _scroller = null;
        }
    }

    private Button NewLetter(char letter)
    {
        var button = new Button
        {
            Content = letter.ToString(),
            Classes = { "alphabet-letter" },
            Tag = "alphabet:" + letter
        };
        AutomationProperties.SetName(button, letter == '#' ? "Jump to numbers and symbols" : "Jump to " + letter);
        button.Click += (_, _) => Jump(letter, true);
        return button;
    }

    private void ApplyTargets()
    {
        for (var index = 0; index < _buttons.Length; index++)
        {
            _buttons[index].IsEnabled = _targets.ContainsKey(Letters[index]);
        }
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        Follow();
    }

    private void Follow()
    {
        if (_scroller is not { } scroller)
        {
            return;
        }

        _follow.Y = scroller.Offset.Y;
        Height = Math.Max(scroller.Viewport.Height - (2 * PanePadding), Letters.Length * MinimumLetterHeight);
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Pointer.Capture(this);
        e.Handled = true;
        JumpAt(e.GetPosition(this).Y);
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (ReferenceEquals(e.Pointer.Captured, this))
        {
            e.Handled = true;
            JumpAt(e.GetPosition(this).Y);
        }
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (ReferenceEquals(e.Pointer.Captured, this))
        {
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    private void JumpAt(double y)
    {
        if (Bounds.Height <= 0)
        {
            return;
        }

        var index = Math.Clamp((int)(y / Bounds.Height * Letters.Length), 0, Letters.Length - 1);
        Jump(Letters[index], false);
    }

    private void Jump(char letter, bool moveFocus)
    {
        if (!_targets.TryGetValue(letter, out var key) || _scroller is not { } scroller
                                                       || scroller.Content is not Visual content
                                                       || this.GetVisualParent() is not { } list
                                                       || FocusSearch.Keyed(list, key) is not { } row
                                                       || row.TranslatePoint(default, content) is not { } top)
        {
            return;
        }

        scroller.Offset = new Vector(scroller.Offset.X, Math.Max(0, top.Y - PanePadding));
        if (moveFocus)
        {
            row.Focus(NavigationMethod.Directional);
        }
    }
}
