using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;

namespace WSGM.Controls;

/// <summary>Gives an editor that has no accessible name the title of the row it sits in.</summary>
/// <remarks>
///     <para>
///         Settings and Overlay rows show their title as a separate <c>setting-title</c> TextBlock beside the
///         editor, so a switch or dropdown alone has nothing to say but "toggle button, off". Each editor is
///         labelled once as it loads: its name follows the row title and its help text the row's
///         <c>caption</c>, both bound so a changed title is read as changed. An editor that already has a
///         name, a labelling element or text content of its own keeps it.
///     </para>
///     <para>
///         The row is the nearest ancestor holding a title, never past a <c>setting-row</c> border, a list
///         or a page, so an editor in a row without a title cannot take a neighbouring row's. A text button
///         such as "Open" keeps its own name and takes the row as its help text.
///     </para>
/// </remarks>
internal static class AccessibleLabels
{
    private const int MaximumRowDepth = 4;
    private static bool _registered;

    /// <summary>Labels every editor of the application as it loads. Idempotent.</summary>
    internal static void Register()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;
        Control.LoadedEvent.AddClassHandler<Control>(static (control, _) => Label(control),
            RoutingStrategies.Direct);
    }

    /// <summary>Labels one editor from its row, when it is an unlabelled editor in a row with a title.</summary>
    /// <param name="control">The loaded control.</param>
    internal static void Label(Control control)
    {
        if (!IsEditor(control) || control.IsSet(AutomationProperties.NameProperty)
                               || AutomationProperties.GetLabeledBy(control) is not null)
        {
            return;
        }

        // A label placed just before its editor ("Recording device" above a dropdown) names it.
        if (control is not ContentControl { Content: string } && LabelBefore(control) is { } label)
        {
            control.Bind(AutomationProperties.NameProperty, label.GetObservable(TextBlock.TextProperty));
            return;
        }

        if (FindRow(control) is not var (title, caption))
        {
            return;
        }

        // A control whose own text names it ("Open", "Show in Steam") keeps that name. A text button
        // still says which row it acts on.
        if (control is ContentControl { Content: string own } && !string.IsNullOrWhiteSpace(own))
        {
            if (control is Button and not ToggleButton && !control.IsSet(AutomationProperties.HelpTextProperty))
            {
                control.Bind(AutomationProperties.HelpTextProperty, title.GetObservable(TextBlock.TextProperty));
            }

            return;
        }

        control.Bind(AutomationProperties.NameProperty, title.GetObservable(TextBlock.TextProperty));
        if (caption is not null && !control.IsSet(AutomationProperties.HelpTextProperty))
        {
            control.Bind(AutomationProperties.HelpTextProperty, caption.GetObservable(TextBlock.TextProperty));
        }
    }

    private static bool IsEditor(Control control)
    {
        return control is ToggleButton or Button or ComboBox or RangeBase or NumericUpDown or TextBox
                   or ListBox
               && control is not RepeatButton && control.TemplatedParent is null;
    }

    private static TextBlock? LabelBefore(Control editor)
    {
        if (editor.Parent is not Panel panel)
        {
            return null;
        }

        var index = panel.Children.IndexOf(editor);
        return index > 0 && panel.Children[index - 1] is TextBlock { Text: { Length: > 0 } } label
                         && !label.Classes.Contains("setting-title")
            ? label
            : null;
    }

    private static (TextBlock Title, TextBlock? Caption)? FindRow(Control editor)
    {
        ILogical? row = editor.GetLogicalParent();
        for (var depth = 0; row is Control ancestor && depth < MaximumRowDepth; depth++)
        {
            if (ancestor is ItemsControl or ScrollViewer or UserControl or TopLevel or ContentPresenter)
            {
                return null;
            }

            var isRow = ancestor is Border border && border.Classes.Contains("setting-row");
            var text = ancestor.GetLogicalDescendants().OfType<TextBlock>()
                .Where(block => !IsInside(block, editor)).ToArray();
            var titles = text.Where(static block => block.Classes.Contains("setting-title")).ToArray();
            // Two titles above an untitled editor belong to two rows: this is a group, not the editor's row.
            if (titles.Length == 1 || (titles.Length > 1 && isRow))
            {
                var caption = text.FirstOrDefault(static block => block.Classes.Contains("caption"));
                return (titles[0], caption);
            }

            if (titles.Length > 1 || isRow)
            {
                return null;
            }

            row = ancestor.GetLogicalParent();
        }

        return null;
    }

    private static bool IsInside(ILogical block, ILogical editor)
    {
        for (var parent = block.GetLogicalParent(); parent is not null; parent = parent.GetLogicalParent())
        {
            if (ReferenceEquals(parent, editor))
            {
                return true;
            }
        }

        return false;
    }
}
