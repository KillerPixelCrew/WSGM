using WSGM.Core;

namespace WSGM.Tests.Core;

public sealed class SelfElevationTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("two words", "\"two words\"")]
    [InlineData("", "\"\"")]
    [InlineData("a\"b", "\"a\\\"b\"")]
    [InlineData(@"C:\Tools\", @"C:\Tools\")]
    [InlineData(@"C:\Program Files\", @"""C:\Program Files\\""")]
    [InlineData("say \"hello\"", "\"say \\\"hello\\\"\"")]
    public void QuoteUsesCommandLineToArgvWCompatibleEscaping(string argument, string expected)
    {
        Assert.Equal(expected, SelfElevation.Quote(argument));
    }
}
