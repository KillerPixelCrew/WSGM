using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Overlay;

/// <summary>Host-owned presentation with an explicit editor, action or read-only observation.</summary>
internal sealed class DescriptorControlView : ContentControl
{
    private readonly Control _body;
    private DescriptorRow _descriptor;
    private bool _invoking;

    internal DescriptorControlView(DescriptorRow descriptor, string key, Func<DescriptorRow, Task> invoke,
        Action<int>? setValue = null)
    {
        _descriptor = descriptor;
        Tag = key;
        Focusable = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        if (descriptor.Options.Count > 0 && setValue is not null)
        {
            _body = DeviceControlRows.Choice(key, descriptor.Title, descriptor.Description,
                descriptor.Options.Select(option => new CapabilityChoice(
                    option.Value.ToString(CultureInfo.InvariantCulture),
                    new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = option.Label })).ToArray(),
                descriptor.Value?.ToString(CultureInfo.InvariantCulture), descriptor.CanInvoke,
                value => setValue(int.Parse(value, CultureInfo.InvariantCulture)));
        }
        else if (descriptor.Range is { } range && setValue is not null)
        {
            _body = new DeviceSliderRow(key, descriptor.Title, descriptor.Description, range.Minimum, range.Maximum,
                range.Step, CapabilityUnit.None, descriptor.Value ?? range.Minimum, descriptor.CanInvoke,
                value => setValue(value < range.OffBelow ? 0 : value),
                value => value < range.OffBelow || value == 0 ? "Off" : $"{value} FPS");
        }
        else if (descriptor.CanInvoke)
        {
            var button = new Button
                { Content = string.IsNullOrWhiteSpace(descriptor.TrailingText) ? "Run" : descriptor.TrailingText };
            button.Click += async (_, _) =>
            {
                if (_invoking || !_descriptor.CanInvoke)
                {
                    return;
                }

                var restoreFocus = button.IsFocused;
                var root = TopLevel.GetTopLevel(button);
                _invoking = true;
                button.IsEnabled = false;
                try
                {
                    await invoke(_descriptor);
                }
                finally
                {
                    _invoking = false;
                    button.IsEnabled = _descriptor.CanInvoke;
                    if (restoreFocus && button.IsEnabled && button.IsEffectivelyVisible
                        && ReferenceEquals(root, TopLevel.GetTopLevel(button))
                        && root?.FocusManager?.GetFocusedElement() is null)
                    {
                        button.Focus(NavigationMethod.Directional);
                    }
                }
            };
            _body = new DeviceSettingRow(key, descriptor.Title, descriptor.Description, button, _ => { });
        }
        else
        {
            _body = new DeviceStatisticRow(key);
        }

        Content = _body;
        Refresh(descriptor);
    }

    internal bool Matches(DescriptorRow descriptor)
    {
        return _descriptor.Range == descriptor.Range
               && _descriptor.Options.SequenceEqual(descriptor.Options)
               && (_descriptor.CanInvoke == descriptor.CanInvoke || _body is DeviceSettingRow or DeviceSliderRow);
    }

    internal void Refresh(DescriptorRow descriptor)
    {
        _descriptor = descriptor;
        switch (_body)
        {
            case DeviceSettingRow setting:
                setting.Refresh(new CapabilityValue
                    {
                        Kind = CapabilityValueKind.Choice,
                        ChoiceValue = descriptor.Value?.ToString(CultureInfo.InvariantCulture)
                    }, descriptor.CanInvoke && !_invoking,
                    descriptor.Description);
                break;
            case DeviceSliderRow slider when descriptor.Range is { } range:
                slider.RefreshReadback(range.Minimum, range.Maximum, range.Step, descriptor.Value ?? range.Minimum,
                    descriptor.CanInvoke);
                break;
            case DeviceStatisticRow statistic:
                statistic.Refresh(descriptor.Title, descriptor.TrailingText, descriptor.Description, descriptor.Status);
                break;
        }
    }
}
