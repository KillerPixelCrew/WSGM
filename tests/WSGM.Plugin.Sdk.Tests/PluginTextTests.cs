using Xunit;

namespace WSGM.Plugin.Sdk.Tests;

public sealed class PluginTextTests
{
    [Theory]
    [InlineData("plain text")]
    [InlineData("نص عادي")]
    public void PlainSingleLineTextIsAccepted(string value)
    {
        Assert.True(PluginText.TryValidate(value, 128, "label", out var error));
        Assert.Null(error);
    }

    [Theory]
    [InlineData(null, "label is required.")]
    [InlineData(" ", "label is required.")]
    [InlineData("one\nline", "label contains a control or bidirectional-override character.")]
    [InlineData("safe‮txet", "label contains a control or bidirectional-override character.")]
    public void BlankControlAndBidirectionalTextIsRejected(string? value, string expectedError)
    {
        Assert.False(PluginText.TryValidate(value, 128, "label", out var error));
        Assert.Equal(expectedError, error);
    }

    [Fact]
    public void TextBeyondTheRequestedBoundIsRejected()
    {
        Assert.False(PluginText.TryValidate("long", 3, "name", out var error));
        Assert.Equal("name exceeds 3 characters.", error);
    }
}
