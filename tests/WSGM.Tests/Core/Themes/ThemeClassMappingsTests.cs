using System.Text.Json;
using WSGM.Core;

namespace WSGM.Tests.Core.Themes;

/// <summary>The class translations map older spellings to the current one, in selectors the way CSS Loader rewrites them.</summary>
public sealed class ThemeClassMappingsTests
{
    private const string Table = """
                                 { "1": ["old_Title", "mid_Title", "new_Title"], "2": ["one_Only"], "3": ["a_Row", "b_Row"] }
                                 """;

    [Fact]
    public void EveryOlderSpellingMapsToTheLatest()
    {
        var mappings = ThemeClassMappings.Parse(Table);

        Assert.Equal(3, mappings.Count);
        Assert.Equal("new_Title", mappings.Translate("old_Title"));
        Assert.Equal("new_Title", mappings.Translate("mid_Title"));
        Assert.Equal("new_Title", mappings.Translate("new_Title"));
        Assert.Equal("one_Only", mappings.Translate("one_Only"));
        Assert.Equal("b_Row", mappings.Translate("a_Row"));
    }

    [Fact]
    public void RewritesClassAndAttributeSelectorsAndLeavesTheRest()
    {
        var mappings = ThemeClassMappings.Parse(Table);

        var rewritten = mappings.Rewrite(
            """.old_Title .a_Row { color: red } [class*="old_Title"] [class^="a_Row"] { top: .5px; background: url(x.old_Title) }""");

        Assert.Equal(
            """.new_Title .b_Row { color: red } [class*="new_Title"] [class^="b_Row"] { top: .5px; background: url(x.new_Title) }""",
            rewritten);
    }

    [Fact]
    public void AnEmptyTableChangesNothing()
    {
        const string css = ".old_Title { x: 1 }";

        Assert.Same(css, ThemeClassMappings.Empty.Rewrite(css));
    }

    [Fact]
    public void RefusesADocumentThatIsNotATable()
    {
        Assert.Throws<JsonException>(() => ThemeClassMappings.Parse("[1, 2]"));
    }
}
