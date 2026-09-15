using WSGM.Core;

namespace WSGM.Tests;

public sealed class SelfElevationTests
{
    [Theory]
    [InlineData("C:\\Tools\\", "C:\\Tools\\")]
    [InlineData("C:\\Program Files\\", "\"C:\\Program Files\\\\\"")]
    [InlineData("say \"hello\"", "\"say \\\"hello\\\"\"")]
    public void QuotePreservesTrailingBackslashesAndEmbeddedQuotes(string argument, string expected)
        => Assert.Equal(expected, SelfElevation.Quote(argument));

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("two words", "\"two words\"")]
    [InlineData("", "\"\"")]
    [InlineData("a\"b", "\"a\\\"b\"")]
    public void QuoteUsesCommandLineToArgvWCompatibleEscaping(string argument, string expected)
        => Assert.Equal(expected, SelfElevation.Quote(argument));
}
