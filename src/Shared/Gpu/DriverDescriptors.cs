// SPDX-License-Identifier: MIT
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Settings;

namespace WSGM.Plugin.Gpu;

internal static class DriverDescriptors
{
    internal static CapabilitySection Section(string id, string title, bool display = false) => new()
    {
        SectionId = id, Key = SettingSectionKey.Custom, CustomTitle = Label(title, 48),
        Icon = display ? SectionIcon.Display : SectionIcon.Gauge
    };

    internal static CapabilityDescriptor Toggle(string id, string instance, string label, string section,
        CapabilityProfileScope scope) => Base(id, instance, label, section, scope, CapabilityValueKind.Boolean,
        CapabilityRole.GenericToggle);

    internal static CapabilityDescriptor Choice(string id, string? instance, string label, string section,
        CapabilityProfileScope scope, IEnumerable<(string Id, string Label)> options) =>
        Base(id, instance, label, section, scope, CapabilityValueKind.Choice, CapabilityRole.GenericChoice) with
        {
            Choices = options.Select(option => new CapabilityChoice(option.Id,
                new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = Label(option.Label, 64) })).ToArray()
        };

    internal static CapabilityDescriptor Range(string id, string instance, string label, string section,
        CapabilityProfileScope scope, int minimum, int maximum, int step) =>
        Base(id, instance, label, section, scope, CapabilityValueKind.Integer, CapabilityRole.GenericRange) with
        { Minimum = minimum, Maximum = maximum, Step = step, Unit = CapabilityUnit.None };

    private static CapabilityDescriptor Base(string id, string? instance, string label, string section,
        CapabilityProfileScope scope, CapabilityValueKind kind, CapabilityRole role) => new()
    {
        CapabilityId = id, InstanceId = instance, SectionId = section, Role = role, ValueKind = kind,
        Display = new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = Label(label, 64) },
        SupportsRead = true, SupportsWrite = true, ProfileScope = scope,
        Persistence = CapabilityPersistence.DevicePersistent,
        ApplyTiming = scope == CapabilityProfileScope.NativePerApplication
            ? CapabilityApplyTiming.NextApplicationStart : CapabilityApplyTiming.Immediate
    };

    private static string Label(string text, int max) => new(text.Where(character => !PlainText.IsUnsafe(character)).Take(max).ToArray());
}
