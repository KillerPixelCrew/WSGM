using System;
using System.Collections.Generic;
using Avalonia.Controls;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>
///     The Quick access pins drawn from the Device, performance and graphics snapshots: published groups, the
///     shared performance group, the GPU groups and WSGM's composite views.
/// </summary>
/// <remarks>
///     Each snapshot is read at most once for a pass, and only when one of its pins is resolved, so a pin
///     list without Device pins never projects the device.
/// </remarks>
internal sealed class DevicePins
{
    private readonly Func<DeviceOverlaySnapshot?> _device;
    private readonly Func<GraphicsOverlaySnapshot?> _graphics;
    private readonly Func<PerformanceOverlaySnapshot?> _performance;
    private readonly DeviceGroupRenderer _renderer;
    private readonly IReadOnlyDictionary<string, Func<Control>> _views;

    /// <summary>Creates the source over the window's snapshot readers.</summary>
    /// <param name="renderer">Draws every pinned group.</param>
    /// <param name="views">WSGM's composite views by pin id.</param>
    /// <param name="device">Reads the Device snapshot, or null without a device source.</param>
    /// <param name="performance">Reads the performance snapshot, or null without one.</param>
    /// <param name="graphics">Reads the graphics snapshot, or null without one.</param>
    internal DevicePins(DeviceGroupRenderer renderer, IReadOnlyDictionary<string, Func<Control>> views,
        Func<DeviceOverlaySnapshot?> device,
        Func<PerformanceOverlaySnapshot?> performance, Func<GraphicsOverlaySnapshot?> graphics)
    {
        _renderer = renderer;
        _views = views;
        _device = device;
        _performance = performance;
        _graphics = graphics;
    }

    /// <summary>Starts one pass over the pin list with the snapshots its caller already read.</summary>
    /// <param name="device">The Device snapshot of this pass, or null to read it when a pin needs it.</param>
    /// <param name="performance">The performance snapshot of this pass, or null to read it when a pin needs it.</param>
    /// <param name="graphics">The graphics snapshot of this pass, or null to read it when a pin needs it.</param>
    /// <returns>The pass, which resolves this source's ids.</returns>
    internal Pass Begin(DeviceOverlaySnapshot? device = null, PerformanceOverlaySnapshot? performance = null,
        GraphicsOverlaySnapshot? graphics = null)
    {
        return new Pass(this, device, performance, graphics);
    }

    /// <summary>One pass over the pin list, reading each snapshot at most once.</summary>
    internal sealed class Pass
    {
        private readonly DevicePins _owner;
        private DeviceOverlaySnapshot? _device;
        private GraphicsOverlaySnapshot? _graphics;
        private PerformanceOverlaySnapshot? _performance;
        private bool _deviceRead;
        private bool _graphicsRead;
        private bool _performanceRead;

        internal Pass(DevicePins owner, DeviceOverlaySnapshot? device, PerformanceOverlaySnapshot? performance,
            GraphicsOverlaySnapshot? graphics)
        {
            _owner = owner;
            _device = device;
            _deviceRead = device is not null;
            _performance = performance;
            _performanceRead = performance is not null;
            _graphics = graphics;
            _graphicsRead = graphics is not null;
        }

        /// <summary>Resolves a pin id this source owns.</summary>
        /// <param name="id">The pin id.</param>
        /// <returns>The pin, an unavailable one for a <c>section.</c> id nothing publishes now, or null.</returns>
        internal QuickAccessPin? Resolve(string id)
        {
            var group = DevicePages.Pinned(id, NeedsDevice(id) ? Device() : null,
                id == DevicePages.PerformanceId ? Performance() : null,
                id.StartsWith(GraphicsSectionPins.Prefix, StringComparison.Ordinal) ? Graphics() : null,
                _owner._views.ContainsKey);
            var available = group is not null;
            group ??= DevicePages.Unavailable(id);
            return group is null
                ? null
                : new QuickAccessPin(group.Title, available, false,
                    drawn => _owner._renderer.Render(drawn, group, true));
        }

        /// <summary>Whether an id can name a Device group, the one kind drawn from the Device snapshot.</summary>
        private bool NeedsDevice(string id)
        {
            return id.StartsWith("section.", StringComparison.Ordinal)
                   && id != DevicePages.PerformanceId
                   && !id.StartsWith(GraphicsSectionPins.Prefix, StringComparison.Ordinal)
                   && !_owner._views.ContainsKey(id);
        }

        private DeviceOverlaySnapshot? Device()
        {
            if (!_deviceRead)
            {
                _deviceRead = true;
                _device = _owner._device();
            }

            return _device;
        }

        private PerformanceOverlaySnapshot? Performance()
        {
            if (!_performanceRead)
            {
                _performanceRead = true;
                _performance = _owner._performance();
            }

            return _performance;
        }

        private GraphicsOverlaySnapshot? Graphics()
        {
            if (!_graphicsRead)
            {
                _graphicsRead = true;
                _graphics = _owner._graphics();
            }

            return _graphics;
        }
    }
}
