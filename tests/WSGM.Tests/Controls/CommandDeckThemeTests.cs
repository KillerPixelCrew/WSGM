using System.Xml.Linq;
using WSGM.Device.Tests;

namespace WSGM.Tests.Controls;

/// <summary>Command-deck controls share standard button templates and visible controller focus.</summary>
public sealed class CommandDeckThemeTests
{
    [Fact]
    public void ProductionResourceGraphLoadsCommandDeckStylesExactlyOnce()
    {
        var app = XDocument.Load(Path.Combine(RepositoryFiles.Root, "src", "WSGM", "App.axaml"));
        var includes = app.Descendants().Where(element => element.Name.LocalName == "StyleInclude")
            .Select(element => element.Attribute("Source")?.Value).ToArray();
        Assert.Single(includes, source => source == "/Themes/CommandDeck.axaml");
    }

    [Fact]
    public void ButtonFocusVisualsOnlyAppearForFocusVisible()
    {
        var uiRoot = Path.Combine(RepositoryFiles.Root, "src", "WSGM");
        var selectors = Directory.EnumerateFiles(uiRoot, "*.axaml", SearchOption.AllDirectories)
            .SelectMany(path => XDocument.Load(path).Descendants())
            .Select(element => element.Attribute("Selector")?.Value)
            .Where(selector => selector is not null
                               && selector.Contains("Button", StringComparison.Ordinal)
                               && selector.Contains(":focus", StringComparison.Ordinal))
            .Cast<string>()
            .ToArray();

        Assert.NotEmpty(selectors);
        Assert.All(selectors, selector =>
            Assert.Contains(":focus-visible", selector, StringComparison.Ordinal));
    }

    [Fact]
    public void EveryRowStyleClassSetInCodeIsActuallyStyled()
    {
        var uiRoot = Path.Combine(RepositoryFiles.Root, "src", "WSGM");
        var selectors = Directory.EnumerateFiles(uiRoot, "*.axaml", SearchOption.AllDirectories)
            .SelectMany(path => XDocument.Load(path).Descendants())
            .Select(element => element.Attribute("Selector")?.Value)
            .Where(selector => selector is not null)
            .Cast<string>()
            .ToArray();

        // Semantic classes must apply to their actual control type.
        (string Class, string Type)[] required =
        [
            ("deck-action", "Button"),
            ("deck-section", "Button"),
            ("glyph-tile", "Border")
        ];

        Assert.All(required, entry =>
        {
            var styled = selectors.Any(selector =>
                selector.Contains($".{entry.Class}", StringComparison.Ordinal)
                && selector.Contains(entry.Type, StringComparison.Ordinal));
            Assert.True(
                styled,
                $"No selector styles .{entry.Class} for {entry.Type}; rows carrying it render bare.");
        });
    }
}
