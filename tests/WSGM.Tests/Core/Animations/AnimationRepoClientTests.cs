using System.Net;
using WSGM.Core;
using WSGM.Tests.Core.Themes;

namespace WSGM.Tests.Core.Animations;

/// <summary>SteamDeckRepo's list and downloads, read the way Animation Changer reads them.</summary>
public sealed class AnimationRepoClientTests
{
    private const string Posts = """
                                 {"posts":[
                                   {"id":"abc123","title":"Neon Boot","thumbnail":"https://cdn/abc.jpg","video":"https://cdn/abc.webm",
                                    "user":{"steam_name":"Squishy"},"content":"Bright.","updated_at":"2025-03-04T10:00:00Z","url":"https://steamdeckrepo.com/post/abc123",
                                    "likes":12,"downloads":300,"type":"boot_video"},
                                   {"id":"def456","title":"Calm","thumbnail":"","video":"http://insecure/def.webm",
                                    "user":{"steam_name":"Emerald"},"content":"","updated_at":"2024-01-02T00:00:00Z",
                                    "likes":-3,"downloads":"many","type":"boot_video"},
                                   {"id":"sus1","title":"A suspend movie","type":"suspend_video"},
                                   {"id":"ghi789","title":"A wallpaper","type":"wallpaper"},
                                   {"id":"../evil","title":"Bad id","type":"boot_video"},
                                   "not a post"
                                 ]}
                                 """;

    [Fact]
    public void TheListKeepsOnlyBootMoviesAndReadsEveryFieldDefensively()
    {
        var listings = AnimationRepoClient.Parse(Posts, "https://steamdeckrepo.com/");

        Assert.Equal(["abc123", "def456"], listings.Select(listing => listing.Id));
        var boot = listings[0];
        Assert.Equal(("Neon Boot", "Squishy", "Bright.", 12, 300),
            (boot.Name, boot.Author, boot.Description, boot.Likes, boot.Downloads));
        Assert.Equal("https://cdn/abc.jpg", boot.ThumbnailUrl);
        Assert.Equal("https://cdn/abc.webm", boot.PreviewUrl);
        Assert.Equal("https://steamdeckrepo.com/post/download/abc123", boot.DownloadUrl);
        var calm = listings[1];
        Assert.Equal((0, 0, "", ""), (calm.Likes, calm.Downloads, calm.ThumbnailUrl, calm.PreviewUrl));
    }

    [Fact]
    public void AnAnswerThatIsNotTheRepositorysIsRefused()
    {
        Assert.Throws<AnimationRepoException>(() => AnimationRepoClient.Parse("{\"items\":[]}", "https://r"));
        Assert.Throws<AnimationRepoException>(() => AnimationRepoClient.Parse("not json", "https://r"));
    }

    [Fact]
    public async Task TheClientAsksForEveryPostAndDownloadsAMovieBounded()
    {
        var handler = new ThemeStoreClientTests.StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/posts/all")
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Posts) };
            }

            if (request.RequestUri.AbsolutePath == "/post/download/abc123")
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var client = new AnimationRepoClient(handler, "https://repo.example");

        var listings = await client.ListAsync(CancellationToken.None);
        Assert.Equal(2, listings.Count);
        Assert.Equal("WSGM", handler.LastUserAgent);
        var folder = Path.Combine(Path.GetTempPath(), "WSGM.Tests.animdl." + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(folder, "abc123.webm.part");
            await client.DownloadAsync(listings[0], path, CancellationToken.None);
            Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(path));
            var missing = Path.Combine(folder, "missing.webm.part");
            await Assert.ThrowsAsync<AnimationRepoException>(() =>
                client.DownloadAsync(listings[1] with { DownloadUrl = "https://repo.example/post/download/missing" },
                    missing, CancellationToken.None));
            Assert.False(File.Exists(missing), "a failed download leaves no file behind");
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, true);
            }
        }
    }

    [Fact]
    public async Task RateLimitingAndAnUnreachableRepositoryAreNamed()
    {
        var limited = new AnimationRepoClient(
            new ThemeStoreClientTests.StubHandler(_ => new HttpResponseMessage((HttpStatusCode)429)),
            "https://repo.example");
        var failure = await Assert.ThrowsAsync<AnimationRepoException>(() => limited.ListAsync(CancellationToken.None));
        Assert.Contains("rate limiting", failure.Message, StringComparison.Ordinal);

        var down = new AnimationRepoClient(
            new ThemeStoreClientTests.StubHandler(_ => throw new HttpRequestException("no route")),
            "https://repo.example");
        var unreachable =
            await Assert.ThrowsAsync<AnimationRepoException>(() => down.ListAsync(CancellationToken.None));
        Assert.Contains("could not be reached", unreachable.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("abc123", true)]
    [InlineData("a-b_c", true)]
    [InlineData("", false)]
    [InlineData("../x", false)]
    [InlineData("a b", false)]
    public void OnlyAnIdAFileCanBeNamedByIsAccepted(string id, bool valid)
    {
        Assert.Equal(valid, AnimationRepoClient.ValidId(id));
    }
}
