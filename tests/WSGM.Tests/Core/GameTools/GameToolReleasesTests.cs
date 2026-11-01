using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using WSGM.Core;
using WSGM.Testing;
using WSGM.Tests.Fakes;

namespace WSGM.Tests.Core;

public sealed class GameToolReleasesTests
{
    private const string StableReleases = """
                                          [{"tag_name":"v0.9.4","id":11,"html_url":"https://github.com/r","draft":false,"prerelease":false,
                                            "assets":[{"name":"Optiscaler_0.9.4-final.20260718._MM.7z","id":1,"updated_at":"x","size":10,
                                            "browser_download_url":"https://github.com/s.7z","digest":"sha256:STABLE"}]}]
                                          """;

    private const string NightlyReleases = """
                                           [{"tag_name":"nightly-20261031","id":22,"html_url":"https://github.com/n","draft":false,"prerelease":true,
                                             "assets":[{"name":"OptiScaler_v10.0.0-pre1_20261031.7z","id":2,"updated_at":"y","size":20,
                                             "browser_download_url":"https://github.com/n.7z","digest":"sha256:NIGHTLY"}]}]
                                           """;

    [Fact]
    public async Task EachChannelIsLookedUpAndCachedOnItsOwn()
    {
        using var directory = new TemporaryDirectory();
        List<string> requests = [];
        using var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(request.RequestUri!.AbsolutePath);
            var nightly = request.RequestUri.AbsolutePath.Contains("OptiScaler-nightly", StringComparison.Ordinal);
            return Json(nightly
                ? NightlyReleases.Replace("NIGHTLY", Hash('n'), StringComparison.Ordinal)
                : StableReleases.Replace("STABLE", Hash('s'), StringComparison.Ordinal));
        });
        using var releases = new GameToolReleases(directory.Root, handler);
        var tool = GameToolCatalog.Current.Find("optiscaler")!;
        var stable = tool.DefaultChannel;
        var nightly = tool.Channel("nightly")!;
        Assert.True(releases.LookupDue(tool, stable));
        Assert.True(releases.LookupDue(tool, nightly));

        var latestStable = await releases.LatestAsync(tool, stable, false, CancellationToken.None);
        Assert.Equal("0.9.4", latestStable.Release!.Version);
        Assert.Null(releases.Cached(tool, nightly));
        Assert.True(releases.LookupDue(tool, nightly));

        var latestNightly = await releases.LatestAsync(tool, nightly, false, CancellationToken.None);
        Assert.Equal("10.0.0-pre1", latestNightly.Release!.Version);
        Assert.StartsWith("nightly-20261031:22:", latestNightly.Release.Id, StringComparison.Ordinal);
        Assert.Equal(["/repos/optiscaler/OptiScaler/releases", "/repos/optiscaler/OptiScaler-nightly/releases"],
            requests);

        // Unforced lookups reuse each channel's answer; a forced one asks again for that channel only.
        await releases.LatestAsync(tool, stable, false, CancellationToken.None);
        await releases.LatestAsync(tool, nightly, false, CancellationToken.None);
        Assert.Equal(2, requests.Count);
        Assert.Same(latestStable, releases.Cached(tool, stable));
        await releases.LatestAsync(tool, nightly, true, CancellationToken.None);
        Assert.Equal(3, requests.Count);
        Assert.Equal("/repos/optiscaler/OptiScaler-nightly/releases", requests[^1]);
    }

    [Fact]
    public async Task PrepareReportsTheDownloadThenThePackageCheck()
    {
        using var directory = new TemporaryDirectory();
        var archive = Zip();
        var sha = Convert.ToHexStringLower(SHA256.HashData(archive));
        using var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(archive)
        });
        using var releases = new GameToolReleases(directory.Root, handler);
        var bundled = GameToolCatalog.Current.Find("optiscaler")!;
        var channel = bundled.DefaultChannel with
        {
            Source = bundled.DefaultChannel.Source with { PackageType = "zip" }
        };
        var tool = bundled with { Channels = [channel] };
        var release = new HostedRelease("0.9.4", "v0.9.4:1:" + sha, "", "GitHub",
            new ReleaseAsset("OptiScaler_0.9.4.zip", "https://example.test/OptiScaler_0.9.4.zip", sha,
                archive.Length));
        List<string> phases = [];

        // The package holds no OptiScaler binary, so the check refuses it after both phases were reported.
        await Assert.ThrowsAsync<InvalidDataException>(() => releases.PrepareAsync(tool, channel, release,
            (phase, _, _) => phases.Add(phase), CancellationToken.None));

        Assert.Equal(GameToolPhase.Download, phases[0]);
        Assert.Equal(GameToolPhase.Verify, phases[^1]);
        Assert.All(phases[..^1], phase => Assert.Equal(GameToolPhase.Download, phase));
    }

    private static string Hash(char digit)
    {
        return new string(digit is >= '0' and <= '9' or >= 'a' and <= 'f' ? digit : 'a', 64);
    }

    private static HttpResponseMessage Json(string body)
    {
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8) };
    }

    private static byte[] Zip()
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        {
            using var writer = new StreamWriter(zip.CreateEntry("OptiScaler.ini").Open());
            writer.Write("[Menu]\nScale=auto\n");
        }

        return buffer.ToArray();
    }
}
