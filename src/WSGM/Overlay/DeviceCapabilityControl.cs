using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;
using WSGM.Controls;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>One stable capability instance. Observations update its editor without replacing focus or drafts.</summary>
internal sealed class DeviceCapabilityControl : ContentControl
{
    private readonly Control _body;
    private readonly ProfileOverrideMarker _marker;
    private readonly Action<DeviceOverlayCapability, CapabilityValue> _write;
    private DeviceOverlayCapability _capability;
    private bool _invoking;

    internal DeviceCapabilityControl(DeviceOverlayCapability capability, string key,
        Action<DeviceOverlayCapability, CapabilityValue> write, Func<DeviceOverlayCapability, Task> invoke, int? marker,
        Func<string, Task>? useGlobal = null)
    {
        _capability = capability;
        _write = write;
        Tag = key;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        Focusable = false;
        if (capability.Writable && capability.ValueKind == CapabilityValueKind.Integer
                                && capability.Minimum is { } min && capability.Maximum is { } max && max >= min)
        {
            _body = new DeviceSliderRow(key, capability.Title, capability.Description, min, max,
                capability.Step ?? 1, capability.Unit, capability.CurrentValue?.IntegerValue ?? min,
                capability.CanInvoke, value => Commit(CapabilityValue.Integer(value)));
        }
        else if (capability.Writable && capability.ValueKind == CapabilityValueKind.Boolean)
        {
            _body = DeviceControlRows.Toggle(key, capability.Title, capability.Description,
                capability.CurrentValue?.BooleanValue ?? false, capability.CanInvoke,
                value => Commit(new CapabilityValue { Kind = CapabilityValueKind.Boolean, BooleanValue = value }));
        }
        else if (capability.Writable && capability.ValueKind == CapabilityValueKind.Choice &&
                 capability.Choices.Count > 0)
        {
            _body = DeviceControlRows.Choice(key, capability.Title, capability.Description, capability.Choices,
                capability.CurrentValue?.ChoiceValue, capability.CanInvoke,
                value => Commit(CapabilityValue.Choice(value)));
        }
        else if (capability.Writable && capability.ValueKind == CapabilityValueKind.Text)
        {
            _body = DeviceControlRows.Text(key, capability.Title, capability.CurrentValue?.TextValue,
                capability.MaximumLength, capability.CanInvoke,
                value => Commit(new CapabilityValue { Kind = CapabilityValueKind.Text, TextValue = value }));
        }
        else if (capability.Writable && capability.ValueKind == CapabilityValueKind.Curve)
        {
            _body = new DeviceCurveRow(key, capability.Title, capability.Description,
                capability.CurrentValue?.CurveValue ?? [], marker, capability.CanInvoke,
                value => Commit(new CapabilityValue { Kind = CapabilityValueKind.Curve, CurveValue = value }));
        }
        else if (capability.Writable || capability.SupportsAction ||
                 (capability.ValueKind == CapabilityValueKind.None && capability.CanInvoke))
        {
            var button = new Button
                { Content = capability.ValueKind == CapabilityValueKind.Color ? "Choose colour" : "Run" };
            button.Click += async (_, _) =>
            {
                if (_invoking || !_capability.CanInvoke)
                {
                    return;
                }

                var restoreFocus = button.IsFocused;
                var root = TopLevel.GetTopLevel(button);
                _invoking = true;
                button.IsEnabled = false;
                try
                {
                    await invoke(_capability);
                }
                finally
                {
                    _invoking = false;
                    button.IsEnabled = _capability.CanInvoke;
                    if (restoreFocus && button.IsEnabled && button.IsEffectivelyVisible
                        && ReferenceEquals(root, TopLevel.GetTopLevel(button))
                        && root?.FocusManager?.GetFocusedElement() is null)
                    {
                        button.Focus(NavigationMethod.Directional);
                    }
                }
            };
            _body = new DeviceSettingRow(key, capability.Title, capability.Description, button, _ => { });
        }
        else
        {
            _body = new DeviceStatisticRow(key);
        }

        _marker = new ProfileOverrideMarker(_body, useGlobal);
        Content = _marker;
        Refresh(capability, marker);
    }

    internal string CapabilityId => _capability.CapabilityId;
    internal string? InstanceId => _capability.InstanceId;

    private void Commit(CapabilityValue value)
    {
        _write(_capability, value);
    }

    internal void Refresh(DeviceOverlayCapability capability, int? marker)
    {
        _capability = capability;
        ToolTip.SetTip(this, capability.Description);
        _marker.Refresh(capability.OverrideId);
        switch (_body)
        {
            case DeviceSliderRow slider:
                slider.RefreshDescription(capability.Description);
                slider.RefreshReadback(capability.Minimum ?? 0, capability.Maximum ?? 0, capability.Step ?? 1,
                    capability.CurrentValue?.IntegerValue ?? capability.Minimum ?? 0, capability.CanInvoke);
                break;
            case DeviceSettingRow setting:
                setting.Refresh(capability.CurrentValue, capability.CanInvoke && !_invoking, capability.Description);
                break;
            case DeviceCurveRow curve:
                curve.RefreshReadback(capability.CurrentValue?.CurveValue ?? [], marker, capability.CanInvoke);
                break;
            case DeviceStatisticRow statistic:
                statistic.Refresh(capability.Title, capability.TrailingText, capability.Description, capability.Status);
                break;
        }
    }
}

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
