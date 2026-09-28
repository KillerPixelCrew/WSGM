using WSGM.Core;

namespace WSGM.Tests.Core;

/// <summary>The Extensions tab's folds survive the tab being rebuilt, in a file of their own.</summary>
public sealed class ExtensionsTabFoldsTests : IDisposable
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
    public void AFoldIsKeptAcrossInstancesAndUnfoldingForgetsIt()
    {
        var path = Path.Combine(_directory, "extensions-tab.json");
        var folds = new ExtensionsTabFolds(path);

        Assert.False(folds.IsFolded("wsgm.themes"));
        Assert.Null(folds.SetFolded("wsgm.themes", true));
        Assert.True(folds.IsFolded("wsgm.themes"));

        var reread = new ExtensionsTabFolds(path);
        Assert.True(reread.IsFolded("wsgm.themes"));
        Assert.Null(reread.SetFolded("wsgm.themes", false));
        Assert.False(new ExtensionsTabFolds(path).IsFolded("wsgm.themes"));
    }

    [Fact]
    public void AnUnreadableFileIsAnEmptySetRatherThanAFailure()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "extensions-tab.json");
        File.WriteAllText(path, "not json");

        var folds = new ExtensionsTabFolds(path);

        Assert.False(folds.IsFolded("x"));
        Assert.Null(folds.SetFolded("x", true));
        Assert.True(new ExtensionsTabFolds(path).IsFolded("x"));
    }
}
