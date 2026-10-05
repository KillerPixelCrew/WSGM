// SPDX-License-Identifier: MIT

using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Settings;

namespace WSGM.Plugin.Gpu;

internal static class DriverDescriptors
{
    internal static CapabilitySection Section(string id, string title, bool display = false)
    {
        return new CapabilitySection
        {
            SectionId = id, Key = SettingSectionKey.Custom, CustomTitle = Label(title),
            Icon = display ? SectionIcon.Display : SectionIcon.Gauge
        };
    }

    internal static CapabilityDescriptor Toggle(string id, string instance, string label, string section,
        CapabilityProfileScope scope)
    {
        return Base(id, instance, label, section, scope, CapabilityValueKind.Boolean,
            CapabilityRole.GenericToggle);
    }

    internal static CapabilityDescriptor Choice(string id, string? instance, string label, string section,
        CapabilityProfileScope scope, IEnumerable<(string Id, string Label)> options)
    {
        return Base(id, instance, label, section, scope, CapabilityValueKind.Choice, CapabilityRole.GenericChoice) with
        {
            Choices = options.Select(option => new CapabilityChoice(option.Id,
                new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = Label(option.Label) })).ToArray()
        };
    }

    internal static CapabilityDescriptor Range(string id, string instance, string label, string section,
        CapabilityProfileScope scope, int minimum, int maximum, int step)
    {
        return Base(id, instance, label, section, scope, CapabilityValueKind.Integer, CapabilityRole.GenericRange) with
        {
            Minimum = minimum, Maximum = maximum, Step = step, Unit = CapabilityUnit.None
        };
    }

    internal static CapabilityDescriptor ReadOnly(string id, string instance, string label, string section,
        IEnumerable<(string Id, string Label)> options)
    {
        return Choice(id, instance, label, section,
                CapabilityProfileScope.GlobalOnly, options) with
            {
                Role = CapabilityRole.GenericReadOnly, SupportsWrite = false
            };
    }

    private static CapabilityDescriptor Base(string id, string? instance, string label, string section,
        CapabilityProfileScope scope, CapabilityValueKind kind, CapabilityRole role)
    {
        return new CapabilityDescriptor
        {
            CapabilityId = id, InstanceId = instance, SectionId = section, Role = role, ValueKind = kind,
            Display = new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = Label(label) },
            SupportsRead = true, SupportsWrite = true, ProfileScope = scope,
            Persistence = CapabilityPersistence.DevicePersistent,
            ApplyTiming = scope == CapabilityProfileScope.NativePerApplication
                ? CapabilityApplyTiming.NextApplicationStart
                : CapabilityApplyTiming.Immediate
        };
    }

    private static string Label(string text)
    {
        return new string(text.Where(character => !PlainText.IsUnsafe(character)).ToArray());
    }
}
