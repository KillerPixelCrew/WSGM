using WSGM.Core;
using WSGM.Testing;

namespace WSGM.Tests.Core;

/// <summary>The Quick Access folds survive a tab being rebuilt, in a file of their own.</summary>
public sealed class QuickAccessFoldsTests : IDisposable
{
    private readonly TemporaryDirectory _temporary = new();

    private string Folder => _temporary.GetPath("folds");

    public void Dispose()
    {
        _temporary.Dispose();
    }

    [Fact]
    public void ASectionStartsFoldedAndAnOpenedOneIsKeptAcrossInstances()
    {
        var path = Path.Combine(Folder, "quick-access-folds.json");
        var folds = new QuickAccessFolds(path);

        Assert.DoesNotContain("wsgm.themes", folds.Open);
        Assert.Null(folds.SetOpen("wsgm.themes", true));
        Assert.Contains("wsgm.themes", folds.Open);
        Assert.Equal(["wsgm.themes"], folds.Open);

        var reread = new QuickAccessFolds(path);
        Assert.Contains("wsgm.themes", reread.Open);
        Assert.Null(reread.SetOpen("wsgm.themes", false));
        Assert.DoesNotContain("wsgm.themes", new QuickAccessFolds(path).Open);
    }

    [Fact]
    public void AnUnreadableFileIsAnEmptySetRatherThanAFailure()
    {
        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, "quick-access-folds.json");
        File.WriteAllText(path, "not json");

        var folds = new QuickAccessFolds(path);

        Assert.DoesNotContain("x", folds.Open);
        Assert.Null(folds.SetOpen("x", true));
        Assert.Contains("x", new QuickAccessFolds(path).Open);
    }
}
