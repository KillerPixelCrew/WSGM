using System.Xml.Linq;
using WSGM.Controls;

namespace WSGM.Tests;

/// <summary>
/// The style-key contract every themed overlay row depends on.
/// </summary>
/// <remarks>
/// Avalonia resolves a <c>ControlTheme</c> by the control's actual runtime type. The theme in
/// <c>Themes\CardButtonTheme.axaml</c> is keyed <c>{x:Type c:CardButton}</c>, so a subclass that
/// does not resolve to that key finds no theme, gets no template, and lays out at zero size. It is
/// still in the tree and still counted by its parent, so the symptom is an empty panel while every
/// diagnostic honestly reports the rows exist — which is exactly how the overlay's Device page
/// showed nothing under its heading with sixteen live capabilities behind it.
/// </remarks>
public sealed class CardButtonThemeTests
{
    [Fact]
    public void ASubclassResolvesTheCardButtonThemeRatherThanItsOwnType()
    {
        ThemeProbeRow row = new();

        Assert.Equal(typeof(CardButton), row.ResolvedStyleKey);
    }

    [Fact]
    public void ButtonFocusVisualsOnlyAppearForFocusVisible()
    {
        string uiRoot = Path.Combine(RepositoryRoot, "src", "WSGM");
        string[] selectors = Directory.EnumerateFiles(uiRoot, "*.axaml", SearchOption.AllDirectories)
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

    /// <summary>
    /// Every style class an overlay row sets in code has a selector that matches it.
    /// </summary>
    /// <remarks>
    /// A class naming no style is silent: the control renders, lays out and takes focus, it just has
    /// no card behind it. That is what happened to <c>tile</c> — the only selector carrying it was
    /// <c>c|CardButton.tile</c>, the unrelated quick-access grid variant, so the slider, toggle,
    /// dropdown and curve rows (all Borders) drew as bare text between properly carded rows and the
    /// Device page read as two designs stacked.
    /// <para>
    /// Matching is by class name rather than by selector shape, because the point is only that
    /// somebody styled it — the type prefix and pseudo-classes are the theme's business.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryRowStyleClassSetInCodeIsActuallyStyled()
    {
        string uiRoot = Path.Combine(RepositoryRoot, "src", "WSGM");
        string[] selectors = Directory.EnumerateFiles(uiRoot, "*.axaml", SearchOption.AllDirectories)
            .SelectMany(path => XDocument.Load(path).Descendants())
            .Select(element => element.Attribute("Selector")?.Value)
            .Where(selector => selector is not null)
            .Cast<string>()
            .ToArray();

        // The classes the overlay's value rows and cards set on themselves. Each has to be styled
        // for the element type that carries it, which is why "tile" appears twice.
        (string Class, string Type)[] required =
        [
            ("tile", "Border"),
            ("tile", "CardButton"),
            ("glyph-tile", "Border"),
        ];

        Assert.All(required, entry =>
        {
            bool styled = selectors.Any(selector =>
                selector.Contains($".{entry.Class}", StringComparison.Ordinal)
                && selector.Contains(entry.Type, StringComparison.Ordinal));
            Assert.True(
                styled,
                $"No selector styles .{entry.Class} for {entry.Type}; rows carrying it render bare.");
        });
    }

    /// <summary>Stands in for <c>DescriptorStatusRow</c>: a subclass that adds behaviour only.</summary>
    /// <remarks>
    /// A subclass is the only way to read the protected key, which also means this probe is the
    /// case under test rather than a stand-in for it — <c>CardButton</c> itself cannot regress here
    /// without the theme's own key changing with it.
    /// </remarks>
    private sealed class ThemeProbeRow : CardButton
    {
        internal Type ResolvedStyleKey => StyleKeyOverride;
    }

    private static string RepositoryRoot
    {
        get
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            while (directory is not null
                && !File.Exists(Path.Combine(directory.FullName, "WSGM.slnx")))
            {
                directory = directory.Parent;
            }

            return Assert.IsType<DirectoryInfo>(directory).FullName;
        }
    }
}
