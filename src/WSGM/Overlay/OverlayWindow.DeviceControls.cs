using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Shell;

namespace WSGM.Overlay;

public partial class OverlayWindow
{
    /// <summary>Builds the proper control for a writable capability — slider, toggle, dropdown or
    /// text editor — or null when it has no dedicated control and should render as a plain row (an
    /// action, a colour swatch, or a read-only value). Sets the row's focus target so gamepad
    /// focus restore lands on the interactive control.</summary>
    private Control? TryCreateDeviceControl(DeviceOverlayCapability capability, string key)
    {
        if (RendersAsSlider(capability))
        {
            return CreateDeviceSliderRow(capability, key);
        }

        if (!capability.Writable)
        {
            return null;
        }

        if (capability.ValueKind is CapabilityValueKind.Curve)
        {
            return CreateDeviceCurveRow(capability, key);
        }

        switch (capability.ValueKind)
        {
            case CapabilityValueKind.Boolean:
                return DeviceControlRows.Toggle(
                    key,
                    capability.Title,
                    capability.Description,
                    capability.CurrentValue?.BooleanValue ?? false,
                    capability.CanInvoke,
                    value => WriteDeviceValue(capability, new CapabilityValue
                    {
                        Kind = CapabilityValueKind.Boolean,
                        BooleanValue = value
                    }));

            case CapabilityValueKind.Choice when capability.Choices.Count > 0:
                return DeviceControlRows.Choice(
                    key,
                    capability.Title,
                    capability.Description,
                    capability.Choices,
                    capability.CurrentValue?.ChoiceValue,
                    capability.CanInvoke,
                    value => WriteDeviceValue(capability, new CapabilityValue
                    {
                        Kind = CapabilityValueKind.Choice,
                        ChoiceValue = value
                    }));

            case CapabilityValueKind.Text:
                return DeviceControlRows.Text(
                    key,
                    capability.Title,
                    capability.CurrentValue?.TextValue,
                    capability.MaximumLength,
                    capability.CanInvoke,
                    value => WriteDeviceValue(capability, new CapabilityValue
                    {
                        Kind = CapabilityValueKind.Text,
                        TextValue = value
                    }));

            case CapabilityValueKind.None:
            case CapabilityValueKind.Integer:
            case CapabilityValueKind.Choice:
            case CapabilityValueKind.Color:
            case CapabilityValueKind.Curve:
            default:
                return null;
        }
    }

    private void WriteDeviceValue(DeviceOverlayCapability capability, CapabilityValue value)
    {
        var bridge = _deviceBridge;
        if (bridge is null || _closed)
        {
            return;
        }

        _ = CommitDeviceValueAsync(bridge, capability with { NextValue = value });
    }

    /// <summary>Builds the labelled slider for an integer-range capability and wires its debounced
    /// commit to the device write path — the same <c>InvokeAsync(with NextValue)</c> the colour
    /// editor uses.</summary>
    private DeviceSliderRow CreateDeviceSliderRow(DeviceOverlayCapability capability, string key)
    {
        var min = capability.Minimum!.Value;
        var max = capability.Maximum!.Value;
        var current = capability.CurrentValue?.IntegerValue ?? min;
        var row = new DeviceSliderRow(
            key,
            capability.Title,
            capability.Description,
            min,
            max,
            capability.Step ?? 1,
            capability.Unit,
            current,
            capability.CanInvoke,
            value => WriteDeviceValue(
                capability,
                new CapabilityValue { Kind = CapabilityValueKind.Integer, IntegerValue = value }));
        return row;
    }

    /// <summary>Builds the fan-curve editor for a writable curve capability.</summary>
    /// <remarks>
    /// The live temperature marker comes from whichever capability reports one, which is why it is
    /// looked up rather than passed in: the reading is published as its own descriptor and may not
    /// exist at all, and a curve without a marker is still a curve worth editing.
    /// </remarks>
    private DeviceCurveRow CreateDeviceCurveRow(DeviceOverlayCapability capability, string key)
    {
        var marker = _deviceBridge?.Snapshot().Capabilities
            .FirstOrDefault(candidate => candidate.Role is CapabilityRole.Telemetry
                && candidate.Unit is CapabilityUnit.Celsius)
            ?.CurrentValue?.IntegerValue;
        return new DeviceCurveRow(
            key,
            capability.Title,
            capability.Description,
            capability.CurrentValue?.CurveValue ?? [],
            marker,
            capability.CanInvoke,
            curve => WriteDeviceValue(
                capability,
                new CapabilityValue { Kind = CapabilityValueKind.Curve, CurveValue = curve }));
    }

    private async Task CommitDeviceValueAsync(
        IDeviceOverlaySource bridge,
        DeviceOverlayCapability capability)
    {
        try
        {
            await bridge.InvokeAsync(capability, _deviceLifetime.Token);
        }
        catch (OperationCanceledException) when (_deviceLifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Warn($"Device value write failed: {capability.CapabilityId}, {ex.Message}");
        }
    }

    private DescriptorStatusRow CreateDeviceCapabilityRow(
        DeviceOverlayCapability capability,
        string key)
    {
        DescriptorStatusRow button = new();
        button.Apply(new DescriptorRow(
            key,
            capability.Title,
            capability.Description,
            capability.TrailingText,
            capability.CanInvoke,
            capability.Status));
        if (capability.CurrentValue is { Kind: CapabilityValueKind.Color, ColorValue: { } packedColor })
        {
            // The row wears its current color: swatch instead of icon (mock detail).
            button.IconGeometry = null;
            button.SwatchBrush = new SolidColorBrush(Color.FromRgb(
                (byte)((packedColor >> 16) & 0xFF),
                (byte)((packedColor >> 8) & 0xFF),
                (byte)(packedColor & 0xFF)));
        }

        button.Click += async (_, _) =>
        {
            var bridge = _deviceBridge;
            if (bridge is null || _closed)
            {
                return;
            }

            if (capability.CurrentValue is
                { Kind: CapabilityValueKind.Color, ColorValue: not null })
            {
                DeviceColorHost.Open(bridge, capability);
                EnterSubView(OverlayPage.DeviceColor);
                return;
            }

            await RunRowCommandAsync(
                button,
                capability.CanInvoke,
                restoreFocus: true,
                token => bridge.InvokeAsync(capability, token),
                $"Device overlay command failed: {capability.CapabilityId}");
        };
        return button;
    }

    private StackPanel CreateGlyphSelectionRow(DescriptorRow descriptor, DeviceGlyphSelection selected)
    {
        var choice = new ComboBox
        {
            ItemsSource = new[] { "Automatic", "Steam native", "Reviewed device profile" },
            SelectedIndex = (int)selected,
            IsEnabled = descriptor.CanInvoke,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Tag = descriptor.Id
        };
        AutomationProperties.SetName(choice, "Button glyph style");
        var detail = new TextBlock { Text = descriptor.Description, Classes = { "caption" }, TextWrapping = TextWrapping.Wrap };
        var panel = new StackPanel { Spacing = 8, Children = { choice, detail } };
        choice.SelectionChanged += async (_, _) =>
        {
            if (choice.SelectedIndex < 0 || _deviceBridge is not { } bridge || _closed) { return; }
            choice.IsEnabled = false;
            try { await bridge.SetPhysicalGlyphSelectionAsync((DeviceGlyphSelection)choice.SelectedIndex, _deviceLifetime.Token); }
            catch (OperationCanceledException) when (_deviceLifetime.IsCancellationRequested) { }
            catch (Exception ex) { detail.Text = "Glyph selection could not be saved: " + ex.Message; }
            finally { if (!_closed) { choice.IsEnabled = descriptor.CanInvoke; } }
        };
        return panel;
    }
}
