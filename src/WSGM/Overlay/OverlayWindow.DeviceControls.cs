using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Shell;

namespace WSGM.Overlay;

public partial class OverlayWindow
{
    /// <summary>
    ///     Runs one Device, Graphics or Performance command with the shared cancellation and logging.
    /// </summary>
    /// <param name="source">The source the command runs against; nothing runs without one.</param>
    /// <param name="description">What the command is, for the log line if it fails.</param>
    /// <param name="command">The command to run against the source.</param>
    /// <returns>A task completing once the command has run or failed.</returns>
    /// <remarks>
    ///     A command that throws must never take the overlay with it, and one that is cancelled by the
    ///     overlay closing is not a failure worth logging.
    /// </remarks>
    private async Task RunCommandAsync<TSource>(TSource? source, string description,
        Func<TSource, CancellationToken, Task> command) where TSource : class
    {
        if (source is null || _closed)
        {
            return;
        }

        try
        {
            await command(source, _deviceLifetime.Token);
        }
        catch (OperationCanceledException) when (_deviceLifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Warn($"{description} failed: {ex.Message}");
        }
    }

    /// <summary>The published device row a commit or action may use, or null when the user's row is gone.</summary>
    private static DeviceOverlayCapability? CurrentDeviceCapability(IDeviceOverlaySource bridge,
        DeviceOverlayCapability seen)
    {
        var snapshot = bridge.Snapshot();
        return snapshot.Visible ? CapabilityRowRenderer.CurrentInvokable(snapshot.Capabilities, seen) : null;
    }

    private void WriteDeviceValue(DeviceOverlayCapability capability, CapabilityValue value)
    {
        if (_deviceBridge is not { } bridge || _closed
                                            || CurrentDeviceCapability(bridge, capability) is not { } current)
        {
            return;
        }

        _ = RunCommandAsync(bridge, $"Device value write {capability.CapabilityId}",
            (source, token) => source.InvokeAsync(current with { NextValue = value }, token));
    }

    private DeviceCapabilityControl CreateDeviceCapabilityRow(DeviceOverlayCapability capability, string key)
    {
        return new DeviceCapabilityControl(capability, key, WriteDeviceValue, async seen =>
        {
            if (_deviceBridge is not { } bridge || _closed
                                                || CurrentDeviceCapability(bridge, seen) is not { } current)
            {
                return;
            }

            if (current.ValueKind == CapabilityValueKind.Color)
            {
                DeviceColorHost.Open(bridge, current);
                EnterSubView(OverlayPage.DeviceColor);
                return;
            }

            await RunCommandAsync(bridge, "Device capability action",
                (source, token) => source.InvokeAsync(current, token));
        }, DeviceTemperature(), id => RunCommandAsync(_deviceBridge, "Use global",
            (source, token) => source.UseGlobalAsync(id, token)));
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
            value => _ = RunCommandAsync(_deviceBridge, "Glyph selection", (source, token) =>
                source.SetPhysicalGlyphSelectionAsync((DeviceGlyphSelection)int.Parse(value), token)));
    }
}
