using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Shell;

/// <summary>Projects the device's lighting zones into Quick Settings' inline colour editor and writes a zone's colour.</summary>
/// <remarks>
///     Zones are selected by their SDK semantic role, never by a device package's private ids. A write
///     re-resolves the zone at execution time and passes through the same coordinator validation and
///     readback path as the overlay and profiles. Publication follows the coordinator's capability
///     changes, which the Steam UI host hears once for every device row.
/// </remarks>
/// <param name="coordinator">Borrowed coordinator, or null when device integration is inactive.</param>
internal sealed class DeviceCoordinatorLightingZonesService(DeviceCoordinator? coordinator) : ISteamLightingZonesBackend
{
    // The zone's colour is read through the overlay row, which declares no plugin section.
    private static readonly IReadOnlySet<string> NoSections = new HashSet<string>();

    /// <summary>The zones the editor shows now, from the capability snapshot without reading hardware.</summary>
    internal SteamLightingZonesState Current => new(Project(coordinator?.PresentedCapabilities() ?? [],
        coordinator?.FanPresets ?? FanCurvePresetTable.HandheldCompanion));

    /// <inheritdoc />
    public async Task<SteamUiCommandResult> SetLightingColorAsync(
        string zone,
        int color,
        CancellationToken cancellationToken)
    {
        if (coordinator is null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new SteamUiCommandResult(false, "Device Integration is not active in this session.");
        }

        var matches = coordinator.Capabilities.Snapshot()
            .Where(view => view.Descriptor.Role is CapabilityRole.LightingZoneColor
                           && string.Equals(view.Descriptor.InstanceId, zone, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length == 1 && WritableColor(matches[0]))
        {
            var result = await coordinator.Commands.ExecuteAsync(
                matches[0].Descriptor.CapabilityId,
                matches[0].Descriptor.InstanceId,
                CapabilityValue.Color(color),
                NativeQamUi.CommandTimeout,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return NativeQamUi.CommandResult(result, $"The device command ended as {result.Outcome}.");
        }

        Log.Warn($"Native QAM lighting color refused: zone='{zone}', matches={matches.Length}.");
        return new SteamUiCommandResult(false, "That lighting zone is unavailable or incompatible.");
    }

    /// <summary>Projects every independently writable lighting zone.</summary>
    /// <param name="views">Current device capability views; a zone id more than one view claims is dropped.</param>
    /// <param name="presets">The matched model's fan curve preset shapes, which the overlay row projection needs.</param>
    /// <returns>The zones, sharing the overlay's values, status and profile overrides.</returns>
    internal static IReadOnlyList<SteamLightingZoneState> Project(
        IReadOnlyList<DeviceCapabilityView> views,
        FanCurvePresetTable presets)
    {
        ArgumentNullException.ThrowIfNull(presets);
        return
        [
            .. views
                .Where(candidate => candidate.Descriptor.Role is CapabilityRole.LightingZoneColor
                                    && !string.IsNullOrWhiteSpace(candidate.Descriptor.InstanceId))
                .GroupBy(candidate => candidate.Descriptor.InstanceId!, StringComparer.Ordinal)
                .Where(group => group.Count() == 1)
                .Select(group =>
                {
                    var view = group.Single();
                    var descriptor = view.Descriptor;
                    var compatible = WritableColor(view);
                    return new SteamLightingZoneState(
                        group.Key,
                        descriptor.Display.Key is DisplayKey.Custom
                        && !string.IsNullOrWhiteSpace(descriptor.Display.CustomLabel)
                            ? NativeQamUi.Text(descriptor.Display.CustomLabel)
                            : group.Key,
                        compatible,
                        ValidColor(view.Projection.DesiredValue),
                        // The overlay row's value; white until something was read or written, as its editor starts.
                        ValidColor(CapabilityRowProjection.ToOverlayCapability(view, NoSections, presets)
                            .CurrentValue) ?? 0xFFFFFF,
                        NativeQamUi.ProgressText(view.Projection.Progress),
                        NativeQamUi.StatusText(view, compatible, "The device control is not currently available.",
                            "The desired value is outside the current descriptor."),
                        CapabilityProjection.DeviceOverrideId(view) is not null);
                })
        ];
    }

    /// <summary>Whether a view is a zone colour the editor can write now.</summary>
    /// <param name="view">The capability view.</param>
    /// <returns>True for a writable, commandable lighting-zone colour.</returns>
    internal static bool WritableColor(DeviceCapabilityView view)
    {
        var descriptor = view.Descriptor;
        return descriptor is
               {
                   Role: CapabilityRole.LightingZoneColor,
                   ValueKind: CapabilityValueKind.Color,
                   SupportsWrite: true
               }
               && CapabilityFreshness.CanCommand(view.Projection.State);
    }

    private static int? ValidColor(CapabilityValue? value)
    {
        return value is
        {
            Kind: CapabilityValueKind.Color,
            ColorValue: >= 0 and <= 0xFFFFFF
        }
            ? value.ColorValue
            : null;
    }
}
