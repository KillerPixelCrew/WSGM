using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>A compact read-only observation, with an InfoBar only when attention is required.</summary>
internal sealed class DeviceStatisticRow : StackPanel
{
    private readonly TextBlock _title = new() { Classes = { "caption" }, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _value = new() { FontSize = 20, FontWeight = FontWeight.SemiBold };
    private readonly FAInfoBar _warning = new() { IsClosable = false, IsIconVisible = true };

    internal DeviceStatisticRow(string key)
    {
        Tag = key;
        Spacing = 2;
        Margin = new Thickness(8, 6);
        Focusable = false;
        Children.Add(_title);
        Children.Add(_value);
        Children.Add(_warning);
    }

    internal void Refresh(string title, string value, string description, DescriptorStatus status)
    {
        _title.Text = title;
        _value.Text = value;
        ToolTip.SetTip(this, description);
        _warning.IsOpen = status is DescriptorStatus.Warning or DescriptorStatus.Faulted or DescriptorStatus.Stale;
        _warning.Message = description;
        _warning.Severity = status == DescriptorStatus.Faulted ? FAInfoBarSeverity.Error : FAInfoBarSeverity.Warning;
    }
}
