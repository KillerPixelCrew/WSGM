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
