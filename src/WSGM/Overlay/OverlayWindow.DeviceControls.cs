using System;
using System.Linq;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Shell;

namespace WSGM.Overlay;

public partial class OverlayWindow
{
    private void WriteDeviceValue(DeviceOverlayCapability capability, CapabilityValue value)
    {
        var bridge = _deviceBridge;
        if (bridge is null || _closed)
        {
            return;
        }

        var snapshot = bridge.Snapshot();
        var current = snapshot.Capabilities.FirstOrDefault(candidate =>
            candidate.CapabilityId == capability.CapabilityId && candidate.InstanceId == capability.InstanceId);
        if (!snapshot.Visible || current is not { CanInvoke: true }
                              || current.DescriptorGeneration != capability.DescriptorGeneration
                              || current.CycleGeneration != capability.CycleGeneration)
        {
            return;
        }

        _ = CommitDeviceValueAsync(bridge, current with { NextValue = value });
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

    private DeviceCapabilityControl CreateDeviceCapabilityRow(DeviceOverlayCapability capability, string key)
    {
        return new DeviceCapabilityControl(capability, key, WriteDeviceValue, async current =>
        {
            if (_deviceBridge is not { } bridge || _closed || !current.CanInvoke)
            {
                return;
            }

            if (current.ValueKind == CapabilityValueKind.Color)
            {
                DeviceColorHost.Open(bridge, current);
                EnterSubView(OverlayPage.DeviceColor);
                return;
            }

            await RunDeviceCommandAsync("Device capability action",
                (source, token) => source.InvokeAsync(current, token));
        }, DeviceTemperature());
    }

    private int? DeviceTemperature()
    {
        return _deviceBridge?.Snapshot().Capabilities.FirstOrDefault(capability =>
                capability.Role == CapabilityRole.Telemetry && capability.Unit == CapabilityUnit.Celsius)?.CurrentValue
            ?.IntegerValue;
    }

    private DeviceSettingRow CreateGlyphSelectionRow(DescriptorRow descriptor, DeviceGlyphSelection selected)
    {
        return DeviceControlRows.Choice(descriptor.Id, descriptor.Title, descriptor.Description,
            Enum.GetValues<DeviceGlyphSelection>().Select(value => new CapabilityChoice(((int)value).ToString(),
                new CapabilityDisplay
                {
                    Key = DisplayKey.Custom, CustomLabel = value switch
                    {
                        DeviceGlyphSelection.Automatic => "Automatic",
                        DeviceGlyphSelection.NativeSteam => "Steam native",
                        _ => "Reviewed device profile"
                    }
                })).ToArray(), ((int)selected).ToString(), descriptor.CanInvoke,
            value => _ = RunDeviceCommandAsync("Glyph selection", (source, token) =>
                source.SetPhysicalGlyphSelectionAsync((DeviceGlyphSelection)int.Parse(value), token)));
    }
}
