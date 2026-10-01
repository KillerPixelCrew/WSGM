using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace WSGM.Controls;

/// <summary>A focusable section heading whose mounted body can be folded away.</summary>
public sealed class CollapsibleSection : Grid
{
    /// <summary>Whether the section body is displayed and available to navigation.</summary>
    public static readonly StyledProperty<bool> IsExpandedProperty =
        AvaloniaProperty.Register<CollapsibleSection, bool>(nameof(IsExpanded));

    /// <summary>Optional compact description or current-value text beneath the title.</summary>
    public static readonly StyledProperty<string?> SummaryProperty =
        AvaloniaProperty.Register<CollapsibleSection, string?>(nameof(Summary));

    private readonly TextBlock _caret = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _summary = new() { Classes = { "caption" }, TextWrapping = TextWrapping.Wrap };
    private readonly string _title;

    /// <summary>Creates a section, optionally with a separate heading action such as pinning.</summary>
    /// <param name="title">The accessible section title.</param>
    /// <param name="body">Mounted controls retained while collapsed.</param>
    /// <param name="action">An optional independent header action.</param>
    public CollapsibleSection(string title, Control body, Control? action = null)
    {
        _title = title;
        Body = body;
        RowDefinitions = new RowDefinitions("Auto,Auto");
        RowSpacing = 8;
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        Heading = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(0, 8), MinHeight = 44
        };
        var labels = new StackPanel { Spacing = 2 };
        labels.Children.Add(new TextBlock
        {
            Text = title, FontSize = 18, FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        labels.Children.Add(_summary);
        var headingContent = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        headingContent.Children.Add(labels);
        SetColumn(_caret, 1);
        headingContent.Children.Add(_caret);
        Heading.Content = headingContent;
        Heading.Click += (_, _) => IsExpanded = !IsExpanded;
        header.Children.Add(Heading);
        if (action is not null)
        {
            SetColumn(action, 1);
            header.Children.Add(action);
        }

        Children.Add(header);
        SetRow(body, 1);
        Children.Add(body);
        UpdateExpansion();
    }

    /// <summary>Whether the section is expanded. Changing it preserves focus before hiding children.</summary>
    public bool IsExpanded
    {
        get => GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    /// <summary>The section's focus and activation target.</summary>
    public Button Heading { get; }

    /// <summary>The mounted body, retained for row reconciliation.</summary>
    public Control Body { get; }

    /// <summary>Optional current-value, status or description text.</summary>
    public string? Summary
    {
        get => GetValue(SummaryProperty);
        set => SetValue(SummaryProperty, value);
    }

    /// <summary>Raised when expansion changes; persistence belongs to the surface owner.</summary>
    public event Action<bool>? ExpansionChanged;

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if ((change.Property == IsExpandedProperty || change.Property == SummaryProperty) && Heading is not null)
        {
            UpdateExpansion();
            if (change.Property == IsExpandedProperty)
            {
                ExpansionChanged?.Invoke(IsExpanded);
            }
        }
    }

    private void UpdateExpansion()
    {
        if (!IsExpanded && TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is Visual focused
                        && (ReferenceEquals(focused, Body) || Body.IsVisualAncestorOf(focused)))
        {
            Heading.Focus(NavigationMethod.Directional);
        }

        Body.IsVisible = IsExpanded;
        _caret.Text = IsExpanded ? "▾" : "▸";
        _summary.Text = Summary;
        _summary.IsVisible = !string.IsNullOrWhiteSpace(Summary);
        AutomationProperties.SetName(Heading, _title + (IsExpanded ? ", expanded" : ", collapsed"));
    }
}
