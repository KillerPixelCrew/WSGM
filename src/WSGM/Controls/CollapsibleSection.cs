using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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

    private readonly Expander _expander = new();
    private readonly TextBlock _summary = new() { Classes = { "caption" }, TextWrapping = TextWrapping.Wrap };

    /// <summary>Creates a section, optionally with a separate heading action such as pinning.</summary>
    /// <param name="title">The accessible section title.</param>
    /// <param name="body">Mounted controls retained while collapsed.</param>
    /// <param name="action">An optional independent header action.</param>
    public CollapsibleSection(string title, Control body, Control? action = null)
    {
        Body = body;
        var labels = new StackPanel { Spacing = 2 };
        labels.Children.Add(new TextBlock
        {
            Text = title, FontSize = 18, FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center
        });
        labels.Children.Add(_summary);
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        header.Children.Add(labels);
        if (action is not null)
        {
            SetColumn(action, 1);
            header.Children.Add(action);
        }

        _expander.Header = header;
        _expander.Content = body;
        _expander.HorizontalAlignment = HorizontalAlignment.Stretch;
        _expander.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        // Callers assign a semantic tag before the native template is attached.
        Heading = new Button();
        _expander.TemplateApplied += (_, _) =>
        {
            var toggle = _expander.GetVisualDescendants().OfType<ToggleButton>()
                .First(button => ReferenceEquals(button.TemplatedParent, _expander));
            toggle.Tag = Heading.Tag;
            Heading = toggle;
        };
        _expander.PropertyChanged += (_, change) =>
        {
            if (change.Property == Expander.IsExpandedProperty)
            {
                IsExpanded = _expander.IsExpanded;
            }
        };
        _expander.AttachedToVisualTree += (_, _) => _expander.ApplyTemplate();
        Children.Add(_expander);
        UpdateExpansion();
    }

    /// <summary>Whether the section is expanded. Changing it preserves focus before hiding children.</summary>
    public bool IsExpanded
    {
        get => GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    /// <summary>The section's focus and activation target.</summary>
    public Button Heading { get; private set; }

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

        _expander.IsExpanded = IsExpanded;
        _summary.Text = Summary;
        _summary.IsVisible = !string.IsNullOrWhiteSpace(Summary);
    }
}
