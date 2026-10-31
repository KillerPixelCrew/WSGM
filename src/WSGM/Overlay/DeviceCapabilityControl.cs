using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using WSGM.Controls;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>
///     The one row control of the Device, GPU, performance and pinned groups: a capability, a host-owned row or a
///     performance row, drawn as an editor, an action or a reading. Observations update it without replacing focus
///     or drafts.
/// </summary>
internal sealed class DeviceCapabilityControl : ContentControl
{
    private readonly Control _body;
    private readonly ProfileOverrideMarker _marker;
    private readonly InvokeButtonRow? _run;
    private readonly Action<DeviceOverlayCapability, CapabilityValue>? _write;
    private DeviceOverlayCapability? _capability;
    private DescriptorRow? _descriptor;

    /// <summary>Draws a device or graphics capability.</summary>
    /// <param name="capability">The published row.</param>
    /// <param name="key">The scoped focus key.</param>
    /// <param name="write">Commits an edited value against the row the user saw.</param>
    /// <param name="invoke">Runs the row's action against the row the user saw.</param>
    /// <param name="marker">The temperature marker for a curve row, if any.</param>
    /// <param name="useGlobal">Clears the running game's override, or null where none applies.</param>
    internal DeviceCapabilityControl(DeviceOverlayCapability capability, string key,
        Action<DeviceOverlayCapability, CapabilityValue> write, Func<DeviceOverlayCapability, Task> invoke, int? marker,
        Func<string, Task>? useGlobal = null)
    {
        _capability = capability;
        _write = write;
        Initialize(key);
        if (capability.Writable && capability.ValueKind == CapabilityValueKind.Integer
                                && capability.Minimum is { } min && capability.Maximum is { } max && max >= min)
        {
            _body = new DeviceSliderRow(key, capability.Title, capability.Description, min, max,
                capability.Step ?? 1, capability.Unit, capability.CurrentValue?.IntegerValue,
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
                value => Commit(new CapabilityValue { Kind = CapabilityValueKind.Curve, CurveValue = value }),
                capability.Presets);
        }
        else if (capability.Writable || capability.SupportsAction ||
                 (capability.ValueKind == CapabilityValueKind.None && capability.CanInvoke))
        {
            _run = new InvokeButtonRow(capability.ValueKind == CapabilityValueKind.Color ? "Choose colour" : "Run",
                () => _capability!.CanInvoke, () => invoke(_capability!));
            _body = new DeviceSettingRow(key, capability.Title, capability.Description, _run.Button, _ => { });
        }
        else
        {
            _body = new DeviceStatisticRow(key);
        }

        _marker = new ProfileOverrideMarker(_body, useGlobal);
        Content = _marker;
        Refresh(capability, marker);
    }

    /// <summary>Draws a host-owned or performance row: a choice or slider when it has a setter, else an action or reading.</summary>
    /// <param name="descriptor">Initial immutable row state.</param>
    /// <param name="key">The scoped focus key.</param>
    /// <param name="invoke">Current-row command callback; service validation remains authoritative.</param>
    /// <param name="setValue">Committed integer editor callback; null uses command or read-only presentation.</param>
    /// <param name="useGlobal">Clears the running game's override, or null where none applies.</param>
    internal DeviceCapabilityControl(DescriptorRow descriptor, string key, Func<DescriptorRow, Task> invoke,
        Action<int>? setValue = null, Func<string, Task>? useGlobal = null)
    {
        _descriptor = descriptor;
        Initialize(key);
        if (descriptor.Options.Count > 0 && setValue is not null)
        {
            _body = DeviceControlRows.Choice(key, descriptor.Title, descriptor.Description,
                descriptor.Options.Select(option => DeviceHostRows.HostChoice(
                    option.Value.ToString(CultureInfo.InvariantCulture), option.Label)).ToArray(),
                descriptor.Value?.ToString(CultureInfo.InvariantCulture), descriptor.CanInvoke,
                value => setValue(int.Parse(value, CultureInfo.InvariantCulture)));
        }
        else if (descriptor.Range is { } range && setValue is not null)
        {
            _body = new DeviceSliderRow(key, descriptor.Title, descriptor.Description, range.Minimum, range.Maximum,
                range.Step, CapabilityUnit.None, descriptor.Value ?? range.Minimum, descriptor.CanInvoke,
                value => setValue(value < range.OffBelow ? 0 : value),
                range.Label);
        }
        else if (descriptor.CanInvoke)
        {
            _run = new InvokeButtonRow(
                string.IsNullOrWhiteSpace(descriptor.TrailingText) ? "Run" : descriptor.TrailingText,
                () => _descriptor!.CanInvoke, () => invoke(_descriptor!));
            _body = new DeviceSettingRow(key, descriptor.Title, descriptor.Description, _run.Button, _ => { });
        }
        else
        {
            _body = new DeviceStatisticRow(key);
        }

        _marker = new ProfileOverrideMarker(_body, useGlobal);
        Content = _marker;
        Refresh(descriptor);
    }

    /// <summary>Draws a host-owned row as the choice its host selection offers.</summary>
    /// <param name="descriptor">The published row, which carries the title, availability and override.</param>
    /// <param name="selection">The named choices and the selected value.</param>
    /// <param name="key">The scoped focus key.</param>
    /// <param name="select">Receives a chosen value.</param>
    /// <param name="useGlobal">Clears the running game's override.</param>
    internal DeviceCapabilityControl(DescriptorRow descriptor, DeviceHostSelection selection, string key,
        Action<string> select, Func<string, Task>? useGlobal)
    {
        _descriptor = descriptor;
        Initialize(key);
        _body = DeviceControlRows.Choice(key, descriptor.Title, descriptor.Description, selection.Choices,
            selection.Value, descriptor.CanInvoke, select);
        _marker = new ProfileOverrideMarker(_body, useGlobal);
        Content = _marker;
        Refresh(descriptor, selection);
    }

    internal string? CapabilityId => _capability?.CapabilityId;
    internal string? InstanceId => _capability?.InstanceId;
    internal string? GpuPluginId => _capability?.GpuPluginId;

    /// <summary>Refreshes the row from a newly published row whose layout matches the drawn one.</summary>
    /// <param name="row">The row as published now.</param>
    internal void Refresh(DeviceRow row)
    {
        switch (row)
        {
            case DeviceRow.Capability capability:
                Refresh(capability.Value, capability.Marker);
                break;
            case DeviceRow.Host { Selection: { } selection } host:
                Refresh(host.Value, selection);
                break;
            case DeviceRow.Host host:
                Refresh(host.Value);
                break;
            case DeviceRow.Performance performance:
                Refresh(performance.Value);
                break;
        }
    }

    private void Initialize(string key)
    {
        Tag = key;
        Focusable = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
    }

    private void Commit(CapabilityValue value)
    {
        _write?.Invoke(_capability!, value);
    }

    private void Refresh(DeviceOverlayCapability capability, int? marker)
    {
        _capability = capability;
        ToolTip.SetTip(this, capability.Description);
        _marker.Refresh(capability.OverrideId);
        switch (_body)
        {
            case DeviceSliderRow slider:
                slider.RefreshDescription(capability.Description);
                slider.RefreshReadback(capability.Minimum ?? 0, capability.Maximum ?? 0, capability.Step ?? 1,
                    capability.CurrentValue?.IntegerValue, capability.CanInvoke);
                break;
            case DeviceSettingRow setting:
                setting.Refresh(capability.CurrentValue, capability.CanInvoke && _run?.Invoking != true,
                    capability.Description);
                break;
            case DeviceCurveRow curve:
                curve.RefreshReadback(capability.CurrentValue?.CurveValue ?? [], marker, capability.CanInvoke);
                break;
            case DeviceStatisticRow statistic:
                statistic.Refresh(capability.Title, capability.TrailingText, capability.Description, capability.Status);
                break;
        }
    }

    private void Refresh(DescriptorRow descriptor)
    {
        _descriptor = descriptor;
        _marker.Refresh(descriptor.OverrideId);
        switch (_body)
        {
            case DeviceSettingRow setting:
                setting.Refresh(new CapabilityValue
                    {
                        Kind = CapabilityValueKind.Choice,
                        ChoiceValue = descriptor.Value?.ToString(CultureInfo.InvariantCulture)
                    }, descriptor.CanInvoke && _run?.Invoking != true,
                    descriptor.Description);
                break;
            case DeviceSliderRow slider when descriptor.Range is { } range:
                // The description names the layer the value comes from, so it follows the value.
                slider.RefreshDescription(descriptor.Description);
                slider.RefreshReadback(range.Minimum, range.Maximum, range.Step, descriptor.Value ?? range.Minimum,
                    descriptor.CanInvoke);
                break;
            case DeviceStatisticRow statistic:
                statistic.Refresh(descriptor.Title, descriptor.TrailingText, descriptor.Description, descriptor.Status);
                break;
        }
    }

    private void Refresh(DescriptorRow descriptor, DeviceHostSelection selection)
    {
        _descriptor = descriptor;
        ((DeviceSettingRow)_body).Refresh(
            new CapabilityValue { Kind = CapabilityValueKind.Choice, ChoiceValue = selection.Value },
            descriptor.CanInvoke, descriptor.Description);
        _marker.Refresh(descriptor.OverrideId);
    }
}
