using WSGM.Core;

namespace WSGM.Tests.Core.Animations;

/// <summary>The library: downloads with their listings, brought files, and the folder as the truth.</summary>
public sealed class AnimationLibraryTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "WSGM.Tests.animlib." + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    internal static AnimationListing Listing(string id, string name = "")
    {
        return new AnimationListing(id, name.Length > 0 ? name : id, "Author", "", "", "",
            $"https://r/post/download/{id}", 1, 2, "2025-01-01T00:00:00Z");
    }

    /// <summary>Stages a movie as a download does and lets the library adopt it.</summary>
    internal static string? Add(AnimationLibrary library, AnimationListing listing, byte[] movie)
    {
        var staged = library.StagingPath(listing.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
        File.WriteAllBytes(staged, movie);
        return library.Adopt(listing, staged);
    }

    [Fact]
    public void ADownloadIsKeptWithItsListingAndReadBackFromTheFolder()
    {
        var library = new AnimationLibrary(_root);
        library.Load();
        Assert.Empty(library.Entries);

        Assert.Null(Add(library, Listing("abc", "Neon"), [1, 2, 3]));
        Assert.Null(Add(library, Listing("sus", "Calm"), [4]));
        Assert.False(File.Exists(library.StagingPath("abc")), "the staged file became the movie");

        var reread = new AnimationLibrary(_root);
        reread.Load();
        Assert.Equal(["Calm", "Neon"], reread.Entries.Select(entry => entry.Name));
        var neon = reread.Find("abc")!;
        Assert.False(neon.IsCustom);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(neon.Path));
        Assert.Equal("Neon", neon.Listing!.Name);
    }

    [Fact]
    public void AFileRemovedByHandLeavesTheLibraryAndAFileDroppedInCustomJoinsIt()
    {
        var library = new AnimationLibrary(_root);
        Add(library, Listing("abc"), [1]);
        File.Delete(library.Find("abc")!.Path);
        Directory.CreateDirectory(library.CustomRoot);
        File.WriteAllBytes(Path.Combine(library.CustomRoot, "Mine.webm"), [9]);

        library.Load();

        var mine = Assert.Single(library.Entries);
        Assert.Equal(("custom:Mine.webm", "Mine", true), (mine.Id, mine.Name, mine.IsCustom));
    }

    [Fact]
    public void ImportCopiesOnlyAWebmAndRemoveDeletesTheFile()
    {
        var library = new AnimationLibrary(_root);
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "source.webm");
        File.WriteAllBytes(source, [1, 2]);
        var wrong = Path.Combine(_root, "source.mp4");
        File.WriteAllBytes(wrong, [1]);

        Assert.Contains(".webm", library.Import(wrong).Error, StringComparison.Ordinal);
        Assert.NotNull(library.Import(Path.Combine(_root, "absent.webm")).Error);
        var (id, error) = library.Import(source);
        Assert.Null(error);
        Assert.Equal("custom:source.webm", id);
        Assert.True(File.Exists(Path.Combine(library.CustomRoot, "source.webm")));
        library.Load();

        Assert.Null(library.Remove(id!));
        Assert.Empty(library.Entries);
        Assert.False(File.Exists(Path.Combine(library.CustomRoot, "source.webm")));
        Assert.NotNull(library.Remove("nope"));
    }

    [Fact]
    public void AnIdAFileCannotBeNamedByIsRefused()
    {
        var library = new AnimationLibrary(_root);
        Assert.NotNull(library.Adopt(Listing("../escape"), Path.Combine(_root, "staged")));
        Assert.False(Directory.Exists(_root));
    }
}
