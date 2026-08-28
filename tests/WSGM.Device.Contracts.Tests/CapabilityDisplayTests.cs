using WSGM.Device.Contracts.Capabilities;

namespace WSGM.Device.Contracts.Tests;

/// <summary>
/// The executable specification of capability labelling: WSGM owns the words, and the one escape
/// hatch stays plain text.
/// </summary>
public class CapabilityDisplayTests
{
    [Fact]
    public void AWsgmOwnedKey_NeedsNoLabel()
    {
        CapabilityDisplay display = new() { Key = DisplayKey.Tdp };

        Assert.True(display.TryValidate(out string? error), error);
    }

    [Fact]
    public void AWsgmOwnedKeyCarryingALabelToo_IsRejected()
    {
        // Dead weight that some surface eventually renders instead of the localized string.
        CapabilityDisplay display = new() { Key = DisplayKey.Tdp, CustomLabel = "Power" };

        Assert.False(display.TryValidate(out _));
    }

    [Fact]
    public void CustomWithoutALabel_IsRejected()
    {
        Assert.False(new CapabilityDisplay { Key = DisplayKey.Custom }.TryValidate(out _));
    }

    [Fact]
    public void AReasonableCustomLabel_IsAccepted()
    {
        CapabilityDisplay display = new()
        {
            Key = DisplayKey.Custom,
            CustomLabel = "UMA frame buffer",
        };

        Assert.True(display.TryValidate(out string? error), error);
    }

    [Fact]
    public void AnOverlongCustomLabel_IsRejected()
    {
        CapabilityDisplay display = new()
        {
            Key = DisplayKey.Custom,
            CustomLabel = new string('x', CapabilityDisplay.MaxCustomLabelLength + 1),
        };

        Assert.False(display.TryValidate(out _));
    }

    [Theory]
    [InlineData("Fan\nspeed")]
    [InlineData("Fan\tspeed")]
    [InlineData("Fan\0speed")]
    public void ALabelWithControlCharacters_IsRejected(string label)
    {
        // These corrupt log lines and can hide the rest of a label from whoever reviews the package.
        CapabilityDisplay display = new() { Key = DisplayKey.Custom, CustomLabel = label };

        Assert.False(display.TryValidate(out _));
    }

    [Fact]
    public void ALabelWithABidirectionalOverride_IsRejected()
    {
        // U+202E makes text render right-to-left from that point, so a label can display as something
        // other than what a reviewer read in the manifest.
        CapabilityDisplay display = new()
        {
            Key = DisplayKey.Custom,
            CustomLabel = "Safe‮label",
        };

        Assert.False(display.TryValidate(out _));
    }
}
