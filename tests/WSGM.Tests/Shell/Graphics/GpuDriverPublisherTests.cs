using LibGPUDriverInteract;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Settings;
using WSGM.Shell;

namespace WSGM.Tests.Shell;

public sealed class GpuDriverPublisherTests
{
    [Fact]
    public void MappedDescriptorSetKeepsVendorVrrLabelsAndSectionIcons()
    {
        var mapped = GpuDriverPublisher.ToHost(new GpuDescriptorSet
        {
            Sections =
            [
                new GpuSection
                {
                    SectionId = "display", Title = "Vendor Display", Description = "Vendor display controls",
                    Kind = GpuSectionKind.Display,
                    Categories = [new GpuCategory { CategoryId = "sync", Title = "Vendor Sync" }]
                },
                new GpuSection
                {
                    SectionId = "adapter", Title = "Vendor Adapter", Kind = GpuSectionKind.Adapter,
                    Categories = [new GpuCategory { CategoryId = "graphics", Title = "Vendor Graphics" }]
                }
            ],
            Descriptors =
            [
                new GpuDescriptor
                {
                    CapabilityId = "display.vrr", InstanceId = "display0", Label = "Vendor Adaptive Sync",
                    Role = GpuRole.VariableRefreshRate, ValueKind = GpuValueKind.Boolean,
                    SectionId = "display", CategoryId = "sync", SupportsRead = true, SupportsWrite = true
                },
                new GpuDescriptor
                {
                    CapabilityId = "graphics.toggle", InstanceId = "adapter0", Label = "Vendor Feature",
                    Role = GpuRole.Toggle, ValueKind = GpuValueKind.Boolean,
                    SectionId = "adapter", CategoryId = "graphics", SupportsRead = true, SupportsWrite = true
                },
                new GpuDescriptor
                {
                    CapabilityId = "graphics.mode", InstanceId = "adapter0", Label = "Vendor Mode",
                    Role = GpuRole.Choice, ValueKind = GpuValueKind.Choice,
                    SectionId = "adapter", CategoryId = "graphics", SupportsRead = true, SupportsWrite = true,
                    Choices = [new GpuChoice("auto", "Vendor Automatic"), new GpuChoice("fast", "Vendor Fast")]
                }
            ]
        });

        Assert.True(DeviceCapabilityValidation.TryValidateDescriptorSet(
            mapped with { }, out var error), error);
        var vrr = mapped.Descriptors[0];
        Assert.Equal(DisplayKey.Custom, vrr.Display.Key);
        Assert.Equal("Vendor Adaptive Sync", vrr.Display.CustomLabel);
        Assert.All(mapped.Descriptors.Skip(1), descriptor =>
        {
            Assert.Equal(DisplayKey.Custom, descriptor.Display.Key);
            Assert.True(descriptor.Display.TryValidate(out var displayError), displayError);
        });
        Assert.Equal("Vendor Feature", mapped.Descriptors[1].Display.CustomLabel);
        Assert.Equal("Vendor Mode", mapped.Descriptors[2].Display.CustomLabel);
        Assert.All(mapped.Descriptors[2].Choices, choice =>
        {
            Assert.Equal(DisplayKey.Custom, choice.Display.Key);
            Assert.True(choice.Display.TryValidate(out var displayError), displayError);
        });
        Assert.Equal(new[] { "Vendor Automatic", "Vendor Fast" },
            mapped.Descriptors[2].Choices.Select(choice => choice.Display.CustomLabel));
        Assert.Equal(new[] { "Vendor Display", "Vendor Adapter" },
            mapped.Sections.Select(section => section.CustomTitle));
        Assert.Equal("Vendor display controls", mapped.Sections[0].CustomDescription);
        Assert.Equal(SectionIcon.Display, mapped.Sections[0].Icon);
        Assert.Equal(SectionIcon.Wrench, mapped.Sections[1].Icon);
        Assert.All(mapped.Sections, section =>
        {
            Assert.Equal(SettingSectionKey.Custom, section.Key);
            Assert.True(section.TryValidate(out var sectionError), sectionError);
            Assert.All(section.Categories, category => Assert.Equal(SettingSectionKey.Custom, category.Key));
        });
        Assert.Equal(new[] { "Vendor Sync", "Vendor Graphics" },
            mapped.Sections.SelectMany(section => section.Categories).Select(category => category.CustomTitle));
    }
}
