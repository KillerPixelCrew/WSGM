using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>
///     Creates the row controls of every described Device, GPU, performance and pinned group, each with the
///     commands of the source it came from.
/// </summary>
/// <remarks>
///     The sources are the window's borrowed device, graphics and performance projections, read when a command
///     runs, so a row always acts through the source attached now. A command acts only on a row that is still
///     published and invokable; one that throws never takes the sheet with it, and one cancelled by the sheet
///     closing is not logged as a failure.
/// </remarks>
internal sealed class DeviceRowFactory
{
    private readonly Func<bool> _closed;
    private readonly Func<IDeviceOverlaySource?> _device;
    private readonly Func<IGraphicsOverlaySource?> _graphics;
    private readonly CancellationToken _lifetime;
    private readonly Action<IDeviceOverlaySource, DeviceOverlayCapability> _openColor;
    private readonly Func<PerformanceOverlayBridge?> _performance;

    /// <summary>Creates the factory over the window's sources.</summary>
    /// <param name="device">The device source, or null without one.</param>
    /// <param name="graphics">The graphics source, or null without one.</param>
    /// <param name="performance">The performance source, or null without one.</param>
    /// <param name="openColor">Opens the colour editor for a device colour capability.</param>
    /// <param name="lifetime">Ends with the sheet, which cancels running commands.</param>
    /// <param name="closed">Whether the sheet has closed, after which no command starts.</param>
    internal DeviceRowFactory(Func<IDeviceOverlaySource?> device, Func<IGraphicsOverlaySource?> graphics,
        Func<PerformanceOverlayBridge?> performance, Action<IDeviceOverlaySource, DeviceOverlayCapability> openColor,
        CancellationToken lifetime, Func<bool> closed)
    {
        _device = device;
        _graphics = graphics;
        _performance = performance;
        _openColor = openColor;
        _lifetime = lifetime;
        _closed = closed;
    }

    /// <summary>The Controller page's glyph preview, which holds the input test's sample lease.</summary>
    internal GlyphInputPreview GlyphPreview { get; } = new();

    /// <summary>WSGM's composite views by pin id: each call makes a new view for a page or a pinned copy.</summary>
    internal Dictionary<string, Func<Control>> Views { get; } = [];

    /// <summary>Creates one row of a described group with the commands of its source.</summary>
    /// <param name="row">The row.</param>
    /// <param name="key">Its focus key, already scoped for a pinned copy.</param>
    /// <returns>The row control.</returns>
    internal Control Create(DeviceRow row, string key)
    {
        switch (row)
        {
            case DeviceRow.Capability { Graphics: true } graphics:
                return CreateGraphicsCapabilityRow(graphics.Value, key);
            case DeviceRow.Capability capability:
                return CreateDeviceCapabilityRow(capability.Value, key, capability.Marker);
            case DeviceRow.Host host:
                return CreateHostRow(host, key);
            case DeviceRow.Performance performance:
                return CreatePerformanceRow(performance.Value, key);
            case DeviceRow.GlyphPreview preview:
                return GlyphPreview.Show(preview.Value);
            case DeviceRow.View view:
                var control = Views[view.Id]();
                control.Tag = key;
                return control;
            default:
                throw new ArgumentOutOfRangeException(nameof(row));
        }
    }

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
        if (source is null || _closed())
        {
            return;
        }

        try
        {
            await command(source, _lifetime);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
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
        return snapshot.Visible ? DeviceGroupRenderer.CurrentInvokable(snapshot.Capabilities, seen) : null;
    }

    private void WriteDeviceValue(DeviceOverlayCapability capability, CapabilityValue value)
    {
        if (_device() is not { } bridge || _closed()
                                        || CurrentDeviceCapability(bridge, capability) is not { } current)
        {
            return;
        }

        _ = RunCommandAsync(bridge, $"Device value write {capability.CapabilityId}",
            (source, token) => source.InvokeAsync(current with { NextValue = value }, token));
    }

    /// <summary>A host-owned Device row: its selection's choice, the AutoTDP target slider, or an action or reading.</summary>
    private DeviceCapabilityControl CreateHostRow(DeviceRow.Host host, string key)
    {
        var descriptor = host.Value;
        if (host.Selection is { } selection)
        {
            return new DeviceCapabilityControl(descriptor, selection, key, value => _ = RunCommandAsync(
                _device(), descriptor.Title,
                (source, token) => source.SetHostSelectionAsync(descriptor.Id, value, token)), UseGlobalOnDevice);
        }

        return descriptor.Id switch
        {
            // The target slider follows the switch, directly under it, while AutoTDP is on.
            DeviceHostRowIds.AutoTdpTarget => new DeviceCapabilityControl(descriptor, key, _ => Task.CompletedTask,
                value => _ = RunCommandAsync(_device(), $"AutoTDP target set to {value} FPS",
                    (source, token) => source.SetAutoTdpTargetAsync(value, token)), UseGlobalOnDevice),
            // Recovery is an action on the device cycle itself rather than on the device. It appears only
            // while there is something to recover.
            DeviceHostRowIds.Retry => new DeviceCapabilityControl(descriptor, key, _ => RunCommandAsync(_device(),
                "Device integration retry", (source, token) => source.RetryDeviceCycleAsync(token))),
            _ => new DeviceCapabilityControl(descriptor, key, _ => Task.CompletedTask, null, UseGlobalOnDevice)
        };
    }

    private Task UseGlobalOnDevice(string overrideId)
    {
        return RunCommandAsync(_device(), "Use global",
            (source, token) => source.UseGlobalAsync(overrideId, token));
    }

    private DeviceCapabilityControl CreatePerformanceRow(DescriptorRow descriptor, string key)
    {
        return new DeviceCapabilityControl(descriptor, key, async seen =>
            {
                // The row may have been republished since it was drawn; act only on a row that is still
                // published and invokable.
                if (_performance() is not { } source
                    || source.Snapshot() is not { Visible: true } snapshot
                    || snapshot.ProfileRows.Concat(snapshot.Rows)
                        .FirstOrDefault(row => row.Id == seen.Id) is not { CanInvoke: true } current)
                {
                    return;
                }

                await RunCommandAsync(source, $"Performance command {current.Id}",
                    (performance, token) => performance.InvokeAsync(current, token));
            },
            value => _ = RunCommandAsync(_performance(), $"Performance row '{descriptor.Id}' set to {value}",
                (source, token) => source.SetValueAsync(descriptor.Id, value, token)),
            overrideId => RunCommandAsync(_performance(), $"Use global for '{overrideId}'",
                (source, token) => source.UseGlobalAsync(overrideId, token)));
    }

    private DeviceCapabilityControl CreateGraphicsCapabilityRow(DeviceOverlayCapability capability, string key)
    {
        return new DeviceCapabilityControl(capability, key, WriteGraphicsValue, seen =>
                seen.ValueKind == CapabilityValueKind.Color
                || _graphics() is not { } source || CurrentGraphicsCapability(source, seen) is not { } current
                    ? Task.CompletedTask
                    : RunCommandAsync(source, $"Graphics action {current.CapabilityId}",
                        (graphics, token) => graphics.WriteAsync(current,
                            current.SupportsAction ? null : current.NextValue, token)),
            null,
            id => RunCommandAsync(_graphics(), "Graphics use global",
                (source, token) => source.UseGlobalAsync(id, token)));
    }

    /// <summary>The published graphics row a commit or action may use, or null when the user's row is gone.</summary>
    private static DeviceOverlayCapability? CurrentGraphicsCapability(IGraphicsOverlaySource source,
        DeviceOverlayCapability seen)
    {
        return DeviceGroupRenderer.CurrentInvokable(
            source.Snapshot().Sections.SelectMany(section => section.Capabilities), seen);
    }

    private void WriteGraphicsValue(DeviceOverlayCapability capability, CapabilityValue value)
    {
        // The editor may report after the row was republished; it belongs to the descriptor the user saw.
        if (_graphics() is not { } source || _closed()
                                          || CurrentGraphicsCapability(source, capability) is not { } current)
        {
            return;
        }

        _ = RunCommandAsync(source, $"Graphics value write {capability.CapabilityId}",
            (graphics, token) => graphics.WriteAsync(current, value, token));
    }

    private DeviceCapabilityControl CreateDeviceCapabilityRow(DeviceOverlayCapability capability, string key,
        int? temperature)
    {
        return new DeviceCapabilityControl(capability, key, WriteDeviceValue, async seen =>
        {
            if (_device() is not { } bridge || _closed()
                                            || CurrentDeviceCapability(bridge, seen) is not { } current)
            {
                return;
            }

            if (current.ValueKind == CapabilityValueKind.Color)
            {
                _openColor(bridge, current);
                return;
            }

            await RunCommandAsync(bridge, "Device capability action",
                (source, token) => source.InvokeAsync(current, token));
        }, temperature, UseGlobalOnDevice);
    }
}
