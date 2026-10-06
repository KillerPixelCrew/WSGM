using WSGM.Overlay;
using WSGM.Testing;

namespace WSGM.Tests.Overlay;

public sealed class OverlayMediaPreviewTests
{
    [Fact]
    public void StaleFilesCleanupRemovesOnlyPreviewFilesAndCanRunAgain()
    {
        using var directory = new TemporaryDirectory();
        var files = directory.GetPath("MediaPreview", "Files", "leftover");
        Directory.CreateDirectory(files);
        File.WriteAllText(Path.Combine(files, "movie.webm"), "preview");
        var profile = directory.GetPath("MediaPreview", "profile.dat");
        File.WriteAllText(profile, "webview profile");
        OverlayMediaPreview.DeleteStaleFiles(directory.Root);
        Assert.False(Directory.Exists(directory.GetPath("MediaPreview", "Files")));
        Assert.Equal("webview profile", File.ReadAllText(profile));
        OverlayMediaPreview.DeleteStaleFiles(directory.Root);
        Assert.True(File.Exists(profile));
    }

    [Fact]
    public void LockedStalePreviewDoesNotThrowOrRemoveTheWebviewProfile()
    {
        using var directory = new TemporaryDirectory();
        var files = directory.GetPath("MediaPreview", "Files");
        Directory.CreateDirectory(files);
        var path = Path.Combine(files, "locked.webm");
        var profile = directory.GetPath("MediaPreview", "profile.dat");
        File.WriteAllText(profile, "webview profile");
        using (File.Open(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            OverlayMediaPreview.DeleteStaleFiles(directory.Root);
            Assert.True(File.Exists(path));
            Assert.True(File.Exists(profile));
        }

        OverlayMediaPreview.DeleteStaleFiles(directory.Root);
        Assert.False(Directory.Exists(files));
        Assert.True(File.Exists(profile));
    }
}
