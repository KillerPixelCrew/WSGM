using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Labs.Panels;
using Avalonia.Layout;
using Avalonia.Media;
using WSGM.Controls;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>
///     Draws <see cref="DeviceGroup" /> descriptions for the Device pages, the GPU page, Tools performance and Quick
///     Access, and keeps what is drawn in step with one keyed reconciliation.
/// </summary>
/// <remarks>
///     A group whose layout is unchanged keeps its controls and refreshes their values, so focus, slider and curve
///     drafts and fold state survive telemetry; a group whose layout changed is rebuilt alone. A pinned copy takes
///     its <c>pin:</c> scope as it is created, on its group, fold, rows and editors.
/// </remarks>
/// <param name="createRow">Creates a row's control with its commands, given the scoped focus key.</param>
/// <param name="createHeader">Creates a pinnable heading for a group id, title and whether it is pinned.</param>
/// <param name="expanded">The session's expanded fold keys.</param>
internal sealed class DeviceGroupRenderer(
    Func<DeviceRow, string, Control> createRow,
    Func<string, string, bool, SectionPinHeader> createHeader,
    ISet<string> expanded)
{
    // Marks the wrapping strip consecutive readings share. Not a string, so no focus or pin lookup sees it.
    private static readonly object ReadingsTag = new();

    /// <summary>
    ///     The published row a commit or action may use: the same row the user saw, still invokable and from the
    ///     same descriptor and device cycle; otherwise null.
    /// </summary>
    /// <remarks>
    ///     An editor can report after its row was republished, and the sources check only the row they are given,
    ///     so eligibility is rechecked at invocation.
    /// </remarks>
    /// <param name="published">Current authoritative rows.</param>
    /// <param name="seen">Descriptor captured when the user began the action.</param>
    /// <returns>The current invokable row with matching identity and generations, otherwise null.</returns>
    internal static DeviceOverlayCapability? CurrentInvokable(IEnumerable<DeviceOverlayCapability> published,
        DeviceOverlayCapability seen)
    {
        var current = published.FirstOrDefault(candidate => candidate.GpuPluginId == seen.GpuPluginId
                                                            && candidate.CapabilityId == seen.CapabilityId
                                                            && candidate.InstanceId == seen.InstanceId);
        return current is { CanInvoke: true } && current.SameLayoutAs(seen) ? current : null;
    }

    /// <summary>Makes <paramref name="target" /> show exactly <paramref name="groups" />, keeping every unchanged group.</summary>
    /// <param name="target">The panel the groups stack in.</param>
    /// <param name="groups">The page's groups, in order.</param>
    /// <param name="pinned">Whether these are Quick Access copies.</param>
    internal void Reconcile(Panel target, IReadOnlyList<DeviceGroup> groups, bool pinned)
    {
        var drawn = target.Children.OfType<DeviceGroupView>().ToDictionary(view => (string)view.Tag!);
        ReconcileChildren(target,
            [.. groups.Select(group => Render(drawn.GetValueOrDefault(Scoped(group.Id, pinned)), group, pinned))]);
    }

    /// <summary>Keeps <paramref name="drawn" /> with refreshed values when its layout still fits, else builds anew.</summary>
    /// <param name="drawn">The control drawn for the group's id last time, or null.</param>
    /// <param name="group">The group as described now.</param>
    /// <param name="pinned">Whether it is a Quick Access copy.</param>
    /// <returns>The group's control.</returns>
    internal Control Render(Control? drawn, DeviceGroup group, bool pinned)
    {
        if (drawn is DeviceGroupView view && view.Pinned == pinned && group.SameLayoutAs(view.Group))
        {
            view.Refresh(group);
            ReconcileNested(view, group.Groups, pinned);
            return view;
        }

        return Build(group, pinned);
    }

    /// <summary>
    ///     Makes <paramref name="panel" /> hold exactly <paramref name="rows" /> in that order, keeping every surviving
    ///     control in place so its focus, drafts and subscriptions survive.
    /// </summary>
    /// <param name="panel">The panel.</param>
    /// <param name="rows">Its children, in order.</param>
    internal static void ReconcileChildren(Panel panel, IReadOnlyList<Control> rows)
    {
        HashSet<Control> current = [.. rows];
        foreach (var stale in panel.Children.Where(child => !current.Contains(child)).ToArray())
        {
            panel.Children.Remove(stale);
        }

        for (var index = 0; index < rows.Count; index++)
        {
            var existing = panel.Children.IndexOf(rows[index]);
            if (existing < 0)
            {
                panel.Children.Insert(index, rows[index]);
            }
            else if (existing != index)
            {
                panel.Children.Move(existing, index);
            }
        }
    }

    /// <summary>Keeps every nested group whose own layout is unchanged, in place after the group's rows.</summary>
    private void ReconcileNested(DeviceGroupView view, IReadOnlyList<DeviceGroup> groups, bool pinned)
    {
        if (view.Body is not { } body || (groups.Count == 0 && view.Groups.Count == 0))
        {
            return;
        }

        var start = body.Children.Count - view.Groups.Count;
        var drawn = view.Groups.ToDictionary(group => group.Group.Id);
        List<DeviceGroupView> next =
            [.. groups.Select(group => (DeviceGroupView)Render(drawn.GetValueOrDefault(group.Id), group, pinned))];
        HashSet<DeviceGroupView> kept = [.. next];
        foreach (var stale in view.Groups.Where(group => !kept.Contains(group)))
        {
            body.Children.Remove(stale);
        }

        for (var index = 0; index < next.Count; index++)
        {
            var existing = body.Children.IndexOf(next[index]);
            if (existing < 0)
            {
                body.Children.Insert(start + index, next[index]);
            }
            else if (existing != start + index)
            {
                body.Children.Move(existing, start + index);
            }
        }

        view.Groups.Clear();
        view.Groups.AddRange(next);
    }

    private static string Scoped(string key, bool pinned)
    {
        return pinned ? QuickAccessPins.TagPrefix + key : key;
    }

    private DeviceGroupView Build(DeviceGroup group, bool pinned)
    {
        var id = Scoped(group.Id, pinned);
        DeviceGroupView view = new(group, pinned);
        StackPanel body = new() { Spacing = group.Spacing, VerticalAlignment = VerticalAlignment.Top };
        view.Body = body;
        switch (group.Fold)
        {
            case DeviceFold.Folded:
                body.Tag = id;
                AddContent(view, body, group, pinned);
                // The fold's heading carries the title; the header keeps only its pin action there.
                SectionPinHeader? action = null;
                if (group.PinTitle is not null)
                {
                    action = createHeader(group.Id, group.Title, pinned);
                    action.ShowPinOnly();
                }

                view.Fold = CollapsibleSection.Persisted(group.Title, body, expanded, id, id + ".expand", action);
                view.Fold.Summary = group.Summary;
                view.Child = view.Fold;
                break;
            case DeviceFold.Open:
            case DeviceFold.Card:
                body.Tag = id;
                body.Children.Add(createHeader(group.Id, group.Title, pinned));
                AddContent(view, body, group, pinned);
                if (group.Fold == DeviceFold.Card)
                {
                    view.Classes.Add("device-group");
                }

                view.Child = body;
                break;
            case DeviceFold.Shared:
                AddContent(view, body, group, pinned);
                view.Fold = CollapsibleSection.Persisted(group.Title, body, expanded, id, id + ".expand");
                view.Child = view.Fold;
                break;
            case DeviceFold.Section:
                body.Margin = new Thickness(0, 4, 0, 0);
                AddContent(view, body, group, pinned);
                view.Child = body;
                break;
            default:
                AddContent(view, body, group, pinned);
                view.Child = body;
                break;
        }

        return view;
    }

    private void AddContent(DeviceGroupView view, StackPanel body, DeviceGroup group, bool pinned)
    {
        foreach (var row in group.Rows)
        {
            var control = row is DeviceRow.Text text
                ? new TextBlock
                {
                    Text = text.Value, Classes = { "caption" }, TextWrapping = TextWrapping.Wrap,
                    Tag = Scoped(row.Key, pinned)
                }
                : createRow(row, Scoped(row.Key, pinned));
            view.Rows[row.Key] = control;
            AddRow(body, control, (row as DeviceRow.Capability)?.ReadingWidth);
        }

        foreach (var nested in group.Groups)
        {
            var child = Build(nested, pinned);
            view.Groups.Add(child);
            body.Children.Add(child);
        }
    }

    /// <summary>Adds a row; a reading joins the strip the previous reading started, and any other row ends it.</summary>
    private static void AddRow(Panel target, Control row, double? readingWidth)
    {
        if (readingWidth is not { } width)
        {
            target.Children.Add(row);
            return;
        }

        row.MinWidth = width;
        Flex.SetGrow(row, 1);
        var count = target.Children.Count;
        if (count == 0 || target.Children[count - 1] is not FlexPanel strip || strip.Tag != ReadingsTag)
        {
            strip = new FlexPanel { Wrap = FlexWrap.Wrap, ColumnSpacing = 12, RowSpacing = 4, Tag = ReadingsTag };
            target.Children.Add(strip);
        }

        strip.Children.Add(row);
    }
}
