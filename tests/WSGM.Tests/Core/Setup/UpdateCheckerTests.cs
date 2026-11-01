using System.Net;
using System.Security.Cryptography;
using System.Text;
using WSGM.Core;
using WSGM.Install;
using WSGM.Testing;

namespace WSGM.Tests.Core;

public sealed class UpdateCheckerTests
{
    private static readonly UpdateRelease DownloadRelease = new("2.1.0", "https://fixture.test/release", "Setup.exe",
        "https://fixture.test/setup", "https://fixture.test/hash", null);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StalledSetupOrHashLeavesNoPartialAndPreservesTheExistingSetup(bool hash)
    {
        using TemporaryDirectory directory = new();
        var target = Path.Combine(directory.Root, DownloadRelease.SetupName);
        await File.WriteAllTextAsync(target, "previous setup");
        await File.WriteAllTextAsync(target + ".partial", "previous interrupted copy");
        var bytes = Encoding.UTF8.GetBytes("new setup fixture");
        var hashBytes = Encoding.UTF8.GetBytes(Convert.ToHexString(SHA256.HashData(bytes)));
        using HttpClient http = new(new DownloadHandler(path => path == "/hash"
            ? hash ? new StreamContent(new PartialThenStalledStream(hashBytes)) : new ByteArrayContent(hashBytes)
            : new StreamContent(new PartialThenStalledStream(bytes))));

        var failure = await Assert.ThrowsAsync<IOException>(() => UpdateChecker.DownloadAsync(http, DownloadRelease,
            null, CancellationToken.None, directory.Root, TimeSpan.FromMilliseconds(100)));

        Assert.Contains("sent nothing", failure.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(target + ".partial"));
        Assert.Equal("previous setup", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task HashMismatchDeletesThePartialAndPreservesTheExistingSetup()
    {
        using TemporaryDirectory directory = new();
        var target = Path.Combine(directory.Root, DownloadRelease.SetupName);
        await File.WriteAllTextAsync(target, "previous setup");
        using HttpClient http = new(new DownloadHandler(path => new ByteArrayContent(Encoding.UTF8.GetBytes(
            path == "/hash" ? new string('0', 64) : "new setup fixture"))));

        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateChecker.DownloadAsync(http, DownloadRelease,
            null, CancellationToken.None, directory.Root));

        Assert.False(File.Exists(target + ".partial"));
        Assert.Equal("previous setup", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task VerifiedDownloadReportsKnownLengthProgressAndPublishesOnlyTheCompleteFile()
    {
        using TemporaryDirectory directory = new();
        var bytes = new byte[200_000];
        for (var index = 0; index < bytes.Length; index++)
        {
            bytes[index] = (byte)(index % 251);
        }

        var hash = Encoding.UTF8.GetBytes(Convert.ToHexString(SHA256.HashData(bytes)));
        using HttpClient http = new(new DownloadHandler(path => new ByteArrayContent(path == "/hash" ? hash : bytes)));
        var progress = new RecordingProgress();

        var result = await UpdateChecker.DownloadAsync(http, DownloadRelease, progress, CancellationToken.None,
            directory.Root);

        Assert.Equal(bytes, await File.ReadAllBytesAsync(result));
        Assert.False(File.Exists(result + ".partial"));
        Assert.NotEmpty(progress.Values);
        Assert.Equal(1, progress.Values[^1]);
        Assert.All(progress.Values, value => Assert.InRange(value, 0, 1));
        Assert.True(progress.Values.SequenceEqual(progress.Values.Order()));
    }

    [Fact]
    public async Task RemovedHashMetadataAndDeclaredSetupCapsDoNotRefuseADownload()
    {
        using TemporaryDirectory directory = new();
        var bytes = Encoding.UTF8.GetBytes("setup fixture");
        var hash = Convert.ToHexString(SHA256.HashData(bytes)) + "  Setup.exe" + new string(' ', 1024 * 1024 + 1);
        using HttpClient http = new(new DownloadHandler(path =>
        {
            var content = new ByteArrayContent(path == "/hash" ? Encoding.UTF8.GetBytes(hash) : bytes);
            if (path == "/setup")
            {
                content.Headers.ContentLength = 1024L * 1024 * 1024 + 1;
            }

            return content;
        }));

        var result = await UpdateChecker.DownloadAsync(http, DownloadRelease, null, CancellationToken.None,
            directory.Root);

        Assert.Equal(bytes, await File.ReadAllBytesAsync(result));
        Assert.False(File.Exists(result + ".partial"));
    }

    [Fact]
    public async Task CallerCancellationRemovesThePartialAndRemainsCancellation()
    {
        using TemporaryDirectory directory = new();
        var bytes = Encoding.UTF8.GetBytes("setup fixture");
        var hash = Encoding.UTF8.GetBytes(Convert.ToHexString(SHA256.HashData(bytes)));
        PartialThenStalledStream setup = new(bytes);
        using HttpClient http = new(new DownloadHandler(path => path == "/hash"
            ? new ByteArrayContent(hash)
            : new StreamContent(setup)));
        using CancellationTokenSource cancellation = new();
        var download = UpdateChecker.DownloadAsync(http, DownloadRelease, null, cancellation.Token, directory.Root);
        await setup.Stalled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
        Assert.False(File.Exists(Path.Combine(directory.Root, DownloadRelease.SetupName) + ".partial"));
    }

    private static byte[] Release(string tag, bool prerelease = false, params string[] assets)
    {
        var list = string.Join(",", assets.Select(name =>
            $$"""{"name":"{{name}}","browser_download_url":"https://github.com/KillerPixelCrew/WSGM/releases/download/{{tag}}/{{name}}"}"""));
        return Encoding.UTF8.GetBytes(
            $$"""{"tag_name":"{{tag}}","draft":false,"prerelease":{{(prerelease ? "true" : "false")}},"html_url":"https://github.com/KillerPixelCrew/WSGM/releases/tag/{{tag}}","assets":[{{list}}]}""");
    }

    private static BundledPlugin Plugin(string id, string origin = "community", string? contact = "dev@example.com")
    {
        return new BundledPlugin
        {
            Id = id,
            Name = id + " plugin",
            Version = "1.0.0",
            Category = "wsgm.common",
            Origin = origin,
            Validation = "blind",
            Contact = contact,
            File = id + "-1.0.0.wsgmpkg",
            Sha256 = new string('a', 64)
        };
    }

    [Fact]
    public void LatestRelease_FindsTheSetupItsHashAndTheBundle()
    {
        var release = UpdateChecker.ParseLatestRelease(Release("v2.1.0", false,
            "WSGM-Setup-2.1.0.exe", "WSGM-Setup-2.1.0.exe.sha256", "bundle.json"));

        Assert.NotNull(release);
        Assert.Equal("2.1.0", release.Version);
        Assert.EndsWith("/WSGM-Setup-2.1.0.exe", release.SetupUrl, StringComparison.Ordinal);
        Assert.EndsWith("/WSGM-Setup-2.1.0.exe.sha256", release.HashUrl, StringComparison.Ordinal);
        Assert.EndsWith("/bundle.json", release.BundleUrl, StringComparison.Ordinal);
    }

    [Fact]
    public void LatestRelease_IgnoresPrereleasesAndReleasesWithoutAVerifiableSetup()
    {
        Assert.Null(UpdateChecker.ParseLatestRelease(Release("v2.1.0", true,
            "WSGM-Setup-2.1.0.exe", "WSGM-Setup-2.1.0.exe.sha256")));
        Assert.Null(UpdateChecker.ParseLatestRelease(Release("v2.1.0", false, "WSGM-Setup-2.1.0.exe")));
        Assert.Null(UpdateChecker.ParseLatestRelease(Encoding.UTF8.GetBytes(
            """{"tag_name":"v2.1.0","assets":[{"name":"WSGM-Setup-2.1.0.exe","browser_download_url":"http://example.com/a"},{"name":"WSGM-Setup-2.1.0.exe.sha256","browser_download_url":"http://example.com/b"}]}""")));
    }

    [Theory]
    [InlineData("2.0.1", true)]
    [InlineData("2.1", true)]
    [InlineData("3", true)]
    [InlineData("2.0.0", false)]
    [InlineData("1.9.9", false)]
    [InlineData("2.0.1-rc1", true)]
    [InlineData("not-a-version", false)]
    public void Newer_ComparesTheNumericCore(string candidate, bool newer)
    {
        Assert.Equal(newer, UpdateChecker.IsNewer(candidate, new Version(2, 0, 0, 0)));
    }

    [Fact]
    public void Hash_ReadsTheSha256SumFormat()
    {
        var hash = new string('A', 64);

        Assert.Equal(hash.ToLowerInvariant(), UpdateChecker.ParseHash($"{hash}  WSGM-Setup-2.1.0.exe"));
        Assert.Null(UpdateChecker.ParseHash("not a hash"));
        Assert.Null(UpdateChecker.ParseHash(""));
    }

    [Fact]
    public void Warnings_NameInstalledCommunityPluginsTheReleaseDropped()
    {
        BundleManifest installed = new()
        {
            SchemaVersion = 1,
            WsgmVersion = "2.0.0",
            Plugins = [Plugin("kept"), Plugin("dropped"), Plugin("first", "first-party"), Plugin("not-installed")]
        };
        BundleManifest next = new()
        {
            SchemaVersion = 1,
            WsgmVersion = "2.1.0",
            Plugins = [Plugin("kept")],
            Outdated =
            [
                new OutdatedPlugin { Id = "dropped", Contact = "new@example.com", Log = "https://example.com/log" }
            ]
        };

        var warnings = UpdateChecker.Warnings(installed, ["kept", "dropped", "first"], next);

        var warning = Assert.Single(warnings);
        Assert.Equal("dropped", warning.PluginId);
        Assert.Equal("new@example.com", warning.Contact);
        Assert.Equal("https://example.com/log", warning.Log);
        Assert.Empty(UpdateChecker.Warnings(null, ["dropped"], next));
    }

    [Fact]
    public void State_RoundTripsThroughItsFile()
    {
        using TemporaryDirectory directory = new();
        var path = Path.Combine(directory.Root, "update.json");
        UpdateState state = new()
        {
            LastCheckUtc = new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero),
            Offer = new UpdateOffer(
                new UpdateRelease("2.1.0", "https://page", "WSGM-Setup-2.1.0.exe", "https://setup", "https://hash",
                    null),
                [new UpdateWarning("dropped", "Dropped", "dev@example.com", null)])
        };

        UpdateChecker.WriteState(state, path);
        var read = UpdateChecker.ReadState(path);

        Assert.Equal(state.LastCheckUtc, read.LastCheckUtc);
        Assert.Equal("2.1.0", read.Offer?.Release.Version);
        Assert.Equal("dev@example.com", Assert.Single(read.Offer!.Warnings).Contact);
        Assert.Null(UpdateChecker.ReadState(Path.Combine(directory.Root, "missing.json")).LastCheckUtc);
    }

    private sealed class DownloadHandler(Func<string, HttpContent> content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = content(request.RequestUri!.AbsolutePath) });
        }
    }

    private sealed class RecordingProgress : IProgress<double>
    {
        public List<double> Values { get; } = [];

        public void Report(double value)
        {
            Values.Add(value);
        }
    }

    private sealed class PartialThenStalledStream(byte[] bytes) : MemoryStream(bytes)
    {
        public TaskCompletionSource Stalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = await base.ReadAsync(buffer, cancellationToken);
            if (read != 0)
            {
                return read;
            }

            Stalled.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
