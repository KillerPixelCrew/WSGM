using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Shell;

internal sealed partial class SteamNativeSettingsService
{
    // Quick Access lists every descriptor control in one section, so no plugin section is declared to it.
    private static readonly IReadOnlySet<string> NoDeclaredSections = new HashSet<string>();

    /// <summary>The device's Quick Settings rows: the charge limit, the lighting brightness and every other descriptor.</summary>
    /// <param name="offer">The read's offered command set and Quick Access sections.</param>
    /// <remarks>
    ///     Controls are selected by their SDK semantic roles, never by a device package's private ids. Every write
    ///     re-resolves its descriptor and passes through the coordinator's validation and readback path, as the overlay
    ///     and profiles do. The lighting zones' colours are the inline editor's, published by
    ///     <see cref="DeviceCoordinatorLightingZonesService" />.
    /// </remarks>
    private void AddDeviceQuickAccess(OfferedRows offer)
    {
        if (coordinator is null)
        {
            return;
        }

        foreach (var (section, row, write) in DeviceQuickAccessRows(coordinator.PresentedCapabilities(),
                     coordinator.FanPresets))
        {
            offer.Quick(section, offer.Add(null, row, write switch
            {
                { Role: { } role } => (value, token) => SetPercentAsync(role, Integer(value), token),
                { Capability: { } capability } => (value, token) => WriteDeviceAsync(capability, value, token),
                _ => (_, _) => Task.FromResult(SteamUiCommandResult.Refused)
            }, true));
        }
    }

    /// <summary>Projects the device's Quick Settings rows from its capability views.</summary>
    /// <param name="views">The presented capability views; ambiguous role or instance matches are not writable.</param>
    /// <param name="presets">The matched model's fan curve preset shapes, as the coordinator resolved them.</param>
    /// <returns>
    ///     Each row with the Quick Access section it belongs to (<c>charging</c>, <c>lighting</c> or
    ///     <c>deviceDescriptors</c>) and what writes it: a percent role, or the descriptor itself.
    /// </returns>
    internal static IReadOnlyList<(string Section, SteamSettingsRow Row, DeviceRowWrite Write)> DeviceQuickAccessRows(
        IReadOnlyList<DeviceCapabilityView> views, FanCurvePresetTable presets)
    {
        List<(string, SteamSettingsRow, DeviceRowWrite)> rows = [];
        var charge = IntegerRangeProjection.Percent(views, CapabilityRole.ChargeLimit);
        var brightness = IntegerRangeProjection.Percent(views, CapabilityRole.LightingBrightness);
        var zones = DeviceCoordinatorLightingZonesService.Project(views, presets);
        Percent("charging", "device.charge-limit", "Battery charge limit", "percent", charge,
            CapabilityRole.ChargeLimit, value => $"Limit {value}%");
        Percent("lighting", "device.lighting-brightness", "Lighting brightness", "bulb", brightness,
            CapabilityRole.LightingBrightness, value => $"{value}%");
        foreach (var view in views)
        {
            var descriptor = view.Descriptor;
            var covered = descriptor.Role is CapabilityRole.ChargeLimit && Covers(charge, descriptor)
                          || descriptor.Role is CapabilityRole.LightingBrightness && Covers(brightness, descriptor)
                          || descriptor.Role is CapabilityRole.LightingZoneColor
                          && zones.Any(zone => zone.Id == descriptor.InstanceId);
            if (covered)
            {
                continue;
            }

            var capability = CapabilityRowProjection.ToOverlayCapability(view, NoDeclaredSections, presets);
            var row = SteamCapabilityRows.Row(capability, "quick/" + DeviceRow(capability).Key);
            // One flat list, so the instance is named in the label, and a row holds while its command applies.
            rows.Add(("deviceDescriptors", row with
            {
                Label = row.Label + (descriptor.InstanceId is { } instance ? $" ({instance})" : string.Empty),
                Disabled = row.Disabled || view.Projection.Progress == CommandProgress.Pending
            }, new DeviceRowWrite(null, capability)));
        }

        return rows;

        void Percent(string section, string key, string label, string icon, IntegerRangeProjection range,
            CapabilityRole role, Func<int, string> summary)
        {
            if (range is not { Available: true, Minimum: { } minimum, Maximum: { } maximum, Step: { } step })
            {
                return;
            }

            var value = range.Observed ?? range.Desired;
            rows.Add((section, new SteamSettingsRow(key, SteamSettingsRowKind.Range, label,
                Marked(range.StatusText, range.GameOverride), Number: value, Minimum: minimum, Maximum: maximum,
                Step: step, Suffix: "%", Disabled: Busy(range.Progress), Accent: range.GameOverride, Icon: icon,
                Summary: value is { } shown ? summary(shown) : null), new DeviceRowWrite(role, null)));
        }
    }

    // Whether the dedicated percent row already draws this control.
    private static bool Covers(IntegerRangeProjection range, CapabilityDescriptor descriptor)
    {
        return range.Available
               && range.CapabilityId == descriptor.CapabilityId
               && range.InstanceId == descriptor.InstanceId;
    }

    /// <summary>Writes one percent role after re-resolving it against the current descriptor.</summary>
    /// <param name="role">The charge limit or the lighting brightness.</param>
    /// <param name="value">The requested percentage.</param>
    /// <param name="cancellationToken">Cancels the device command.</param>
    /// <returns>Applied, or the device's refusal.</returns>
    private async Task<SteamUiCommandResult> SetPercentAsync(CapabilityRole role, int value,
        CancellationToken cancellationToken)
    {
        if (coordinator is null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new SteamUiCommandResult(false, NoDevice);
        }

        var range = IntegerRangeProjection.Percent(coordinator.Capabilities.Snapshot(), role);
        if (range is not
            {
                Available: true, CapabilityId: { } capabilityId, Minimum: { } minimum, Maximum: { } maximum,
                Step: { } step
            }
            || CapabilityProjection.ValidInteger(CapabilityValue.Integer(value), minimum, maximum, step) is null)
        {
            Log.Warn(string.Create(CultureInfo.InvariantCulture,
                $"Native QAM device range refused: role={role}, value={value}, status='{range.StatusText}'."));
            return new SteamUiCommandResult(false, "That device control is unavailable or outside its descriptor.");
        }

        var result = await coordinator.Commands.ExecuteAsync(capabilityId, range.InstanceId,
            CapabilityValue.Integer(value), NativeQamUi.CommandTimeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return NativeQamUi.CommandResult(result, $"The device command ended as {result.Outcome}.");
    }

    /// <summary>What one device row writes: a percent role, or its descriptor.</summary>
    /// <param name="Role">The percent role the dedicated row writes, or null.</param>
    /// <param name="Capability">The descriptor row as offered, or null.</param>
    internal sealed record DeviceRowWrite(CapabilityRole? Role, DeviceOverlayCapability? Capability);
}
