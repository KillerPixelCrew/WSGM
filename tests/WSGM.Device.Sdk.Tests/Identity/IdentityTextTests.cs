using WSGM.Device.Sdk.Identity;

namespace WSGM.Device.Sdk.Tests.Identity;

public sealed class IdentityTextTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    public void MissingIdentityNeverMatchesAnotherMissingIdentity(string? value)
    {
        Assert.Null(IdentityText.Normalize(value));
        Assert.False(IdentityText.Matches(value, value));
        Assert.False(IdentityText.Matches(value, "ROG Ally"));
    }

    [Fact]
    public void VendorWhitespaceAndCasingDoNotChangeIdentity()
    {
        Assert.Equal("ROG Ally", IdentityText.Normalize(" \tROG\r\n  Ally\t "));
        Assert.True(IdentityText.Matches(" ROG\t  Ally ", "rog ally"));
        Assert.False(IdentityText.Matches("ROG Ally", "ROG Ally X"));
        var longIdentity = new string('A', 300) + "  Board";
        Assert.Equal(new string('A', 300) + " Board", IdentityText.Normalize(longIdentity));
    }
}
