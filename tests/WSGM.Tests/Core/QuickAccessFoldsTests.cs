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

        Assert.False(folds.IsOpen("wsgm.themes"));
        Assert.Null(folds.SetOpen("wsgm.themes", true));
        Assert.True(folds.IsOpen("wsgm.themes"));
        Assert.Equal(["wsgm.themes"], folds.Open);

        var reread = new QuickAccessFolds(path);
        Assert.True(reread.IsOpen("wsgm.themes"));
        Assert.Null(reread.SetOpen("wsgm.themes", false));
        Assert.False(new QuickAccessFolds(path).IsOpen("wsgm.themes"));
    }

    [Fact]
    public void AnUnreadableFileIsAnEmptySetRatherThanAFailure()
    {
        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, "quick-access-folds.json");
        File.WriteAllText(path, "not json");

        var folds = new QuickAccessFolds(path);

        Assert.False(folds.IsOpen("x"));
        Assert.Null(folds.SetOpen("x", true));
        Assert.True(new QuickAccessFolds(path).IsOpen("x"));
    }
}
