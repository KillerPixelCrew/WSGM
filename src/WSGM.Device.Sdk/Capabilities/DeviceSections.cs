using System.Collections.Generic;
using System.Linq;
using WSGM.Device.Sdk.Settings;

namespace WSGM.Device.Sdk.Capabilities;

/// <summary>Shared Device pages to which WSGM and plugins contribute controls.</summary>
public static class DeviceSections
{
    /// <summary>Power limits, profiles, cooling and Windows energy controls.</summary>
    public const string PowerId = "power";
    /// <summary>RGB lighting controls.</summary>
    public const string RgbId = "rgb";
    /// <summary>Controller, motion and input controls.</summary>
    public const string ControllerId = "controller";
    /// <summary>Readings, ownership and diagnostics.</summary>
    public const string InfoId = "info";

    /// <summary>The predefined Power page. A plugin may add categories using a record copy.</summary>
    public static CapabilitySection Power { get; } = new()
    { SectionId = PowerId, Key = SettingSectionKey.Power, Icon = SectionIcon.Power, SortOrder = 0 };
    /// <summary>The predefined RGB page.</summary>
    public static CapabilitySection Rgb { get; } = new()
    { SectionId = RgbId, Key = SettingSectionKey.Lighting, Icon = SectionIcon.Lighting, SortOrder = 1 };
    /// <summary>The predefined Controller page.</summary>
    public static CapabilitySection Controller { get; } = new()
    { SectionId = ControllerId, Key = SettingSectionKey.Controller, Icon = SectionIcon.Controller, SortOrder = 2 };
    /// <summary>The predefined Info page.</summary>
    public static CapabilitySection Info { get; } = new()
    { SectionId = InfoId, Key = SettingSectionKey.Diagnostics, Icon = SectionIcon.Gauge, SortOrder = 3 };

    /// <summary>The four shared page declarations in their default presentation order.</summary>
    public static IReadOnlyList<CapabilitySection> All { get; } =
        System.Array.AsReadOnly(new[] { Power, Rgb, Controller, Info });

    /// <summary>Adds predefined sections omitted by a plugin, preserving its category declarations.</summary>
    /// <param name="declared">A validated plugin section list.</param>
    /// <returns>The complete layout, including empty shared pages a host may populate.</returns>
    /// <remarks>Descriptors may reference these predefined IDs without declaring their sections.
    /// Empty pages need not be rendered. Custom sections still require explicit declarations.</remarks>
    public static IReadOnlyList<CapabilitySection> IncludePredefined(IReadOnlyList<CapabilitySection> declared) =>
        All.Select(section => section with
        { Categories = declared.FirstOrDefault(item => item.SectionId == section.SectionId)?.Categories ?? section.Categories })
            .Concat(declared.Where(item => !All.Any(section => section.SectionId == item.SectionId))).ToArray();
}
