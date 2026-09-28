using WSGM.Core;

namespace WSGM.Tests.Core;

/// <summary>The Quick Access folds survive a tab being rebuilt, in a file of their own.</summary>
public sealed class QuickAccessFoldsTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "WSGM.Tests.folds." + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    [Fact]
    public void ASectionStartsFoldedAndAnOpenedOneIsKeptAcrossInstances()
    {
        var path = Path.Combine(_directory, "quick-access-folds.json");
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
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "quick-access-folds.json");
        File.WriteAllText(path, "not json");

        var folds = new QuickAccessFolds(path);

        Assert.False(folds.IsOpen("x"));
        Assert.Null(folds.SetOpen("x", true));
        Assert.True(new QuickAccessFolds(path).IsOpen("x"));
    }
}
