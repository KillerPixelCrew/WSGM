using System.Net;
using System.Text;
using System.Text.Json;
using WSGM.Core;

namespace WSGM.Tests.Core;

/// <summary>Uploading masked logs whole into one filebin.net bin, against a fake filebin.</summary>
public sealed class LogUploaderTests : IDisposable
{
    private const string Bin = "wsgm-0123456789abcdef0123456789abcdef";

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "wsgm-log-upload-" + Guid.NewGuid().ToString("N"));

    public LogUploaderTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        Directory.Delete(_directory, true);
    }

    [Fact]
    public async Task BothLogsGoUpWholeAndMaskedIntoOneBin()
    {
        var live = Write("wsgm.log", @"started from C:\Users\chris\AppData\Local\WSGM by chris" + "\r\n");
        var previous = Write("wsgm.old.log", "before\n");
        Filebin server = new();

        var link = await Upload(server, LogUploader.Find(live, previous));

        Assert.Equal($"https://filebin.net/{Bin}", link);
        Assert.Equal([$"/{Bin}/wsgm.log", $"/{Bin}/wsgm.old.log"], server.Files.Keys);
        Assert.Equal(@"started from %USERPROFILE%\AppData\Local\WSGM by <user>" + "\r\n",
            server.Text($"/{Bin}/wsgm.log"));
        Assert.Equal("before\n", server.Text($"/{Bin}/wsgm.old.log"));
        Assert.All(server.ContentTypes, type => Assert.Equal("text/plain", type));
        Assert.Empty(server.Deleted);
    }

    [Fact]
    public async Task ALargeLogGoesUpInOneRequestWithoutASizeLimit()
    {
        var text = string.Concat(Enumerable.Range(1, 200_000).Select(index => $"line {index}\n"));
        var log = Write("wsgm.log", text);
        Filebin server = new();

        await Upload(server, [log]);

        Assert.Equal(1, server.Posts);
        Assert.Equal(text, server.Text($"/{Bin}/wsgm.log"));
    }

    [Fact]
    public void EachUploadGetsANewUnguessableBin()
    {
        var first = LogUploader.NewBin();
        var second = LogUploader.NewBin();

        Assert.Matches("^wsgm-[0-9a-f]{32}$", first);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void OnlyTheLogFilesThatExistAreListed()
    {
        var live = Write("wsgm.log", "now\n");

        var file = Assert.Single(LogUploader.Find(live, Path.Combine(_directory, "wsgm.old.log")));

        Assert.Equal(new LogUploadFile("wsgm.log", live, 4, false), file);
    }

    [Fact]
    public async Task ALogWsgmIsStillWritingIsRead()
    {
        var log = Write("wsgm.log", "first\n");
        await using FileStream writer = new(log, FileMode.Append, FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
        writer.Write("second\n"u8);
        writer.Flush();
        Filebin server = new();

        await Upload(server, [log]);

        Assert.Equal("first\nsecond\n", server.Text($"/{Bin}/wsgm.log"));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "", "filebin.net is limiting uploads right now (HTTP 429).")]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, "File too large\nmore detail",
        "filebin.net refused wsgm.old.log with HTTP 413 (RequestEntityTooLarge): File too large.")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "<html><body>busy</body></html>",
        "filebin.net refused wsgm.old.log with HTTP 503 (ServiceUnavailable).")]
    public async Task ARefusedFileFailsWithFilebinsReasonAndRemovesTheBin(HttpStatusCode status, string body,
        string reason)
    {
        var live = Write("wsgm.log", "now\n");
        var previous = Write("wsgm.old.log", "before\n");
        Filebin server = new() { Refuse = post => post == 2 ? (status, body) : null };

        var failure = await Assert.ThrowsAsync<LogUploadException>(() => Upload(server, [live, previous]));

        Assert.False(failure.Cancelled);
        Assert.StartsWith(reason, failure.Message, StringComparison.Ordinal);
        Assert.EndsWith(" The partial upload was removed.", failure.Message, StringComparison.Ordinal);
        Assert.Equal([$"/{Bin}"], server.Deleted);
        Assert.Empty(server.Files);
    }

    [Fact]
    public async Task AFileFilebinKeptOnlyPartOfFails()
    {
        var log = Write("wsgm.log", "0123456789\n");
        Filebin server = new() { KeepBytes = 4 };

        var failure = await Assert.ThrowsAsync<LogUploadException>(() => Upload(server, [log]));

        Assert.Equal("filebin.net kept 4 of the 11 bytes of wsgm.log. The partial upload was removed.",
            failure.Message);
    }

    [Fact]
    public async Task ABinThatCannotBeRemovedSaysItExpiresOnItsOwn()
    {
        var log = Write("wsgm.log", "now\n");
        Filebin server = new() { Refuse = _ => (HttpStatusCode.InternalServerError, ""), RefuseDelete = true };

        var failure = await Assert.ThrowsAsync<LogUploadException>(() => Upload(server, [log]));

        Assert.EndsWith(" The partial upload could not be removed; filebin.net deletes it in about a week.",
            failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANetworkFailureSaysFilebinCouldNotBeReached()
    {
        var log = Write("wsgm.log", "now\n");
        Filebin server = new() { Unreachable = true };

        var failure = await Assert.ThrowsAsync<LogUploadException>(() => Upload(server, [log]));

        Assert.StartsWith("Could not reach filebin.net: No such host is known.", failure.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingFileFailsBeforeAnythingIsSent()
    {
        Filebin server = new();

        var failure = await Assert.ThrowsAsync<LogUploadException>(() =>
            Upload(server, [Path.Combine(_directory, "wsgm.log")]));

        Assert.StartsWith("Could not read wsgm.log: ", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, server.Posts);
        Assert.Empty(server.Deleted);
    }

    [Fact]
    public async Task CancellingRemovesTheBin()
    {
        var live = Write("wsgm.log", "now\n");
        var previous = Write("wsgm.old.log", "before\n");
        using CancellationTokenSource cancel = new();
        Filebin server = new()
        {
            BeforePost = post =>
            {
                if (post == 2)
                {
                    cancel.Cancel();
                }
            }
        };

        var failure = await Assert.ThrowsAsync<LogUploadException>(() =>
            Upload(server, [live, previous], cancel.Token));

        Assert.True(failure.Cancelled);
        Assert.Equal("Upload cancelled. The partial upload was removed.", failure.Message);
        Assert.Empty(server.Files);
    }

    [Fact]
    public async Task TheProgressNamesEachFileAsItIsSent()
    {
        var live = Write("wsgm.log", "now\n");
        var previous = Write("wsgm.old.log", "before\n");
        Filebin server = new();
        List<LogUploadProgress> steps = [];

        await Upload(server, [live, previous], progress: new Recorder(steps));

        Assert.Equal([new LogUploadProgress("wsgm.log"), new LogUploadProgress("wsgm.old.log")], steps);
    }

    private string Write(string name, string text)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, text);
        return path;
    }

    private static Task<string> Upload(Filebin server, IEnumerable<string> paths,
        CancellationToken cancellationToken = default, IProgress<LogUploadProgress>? progress = null)
    {
        return Upload(server, paths.Select(path =>
                new LogUploadFile(Path.GetFileName(path), path, 0, Path.GetFileName(path) == "wsgm.old.log"))
            .ToArray(), cancellationToken, progress);
    }

    private static async Task<string> Upload(Filebin server, IReadOnlyList<LogUploadFile> files,
        CancellationToken cancellationToken = default, IProgress<LogUploadProgress>? progress = null)
    {
        using DownloadClient client = new(server);
        return await new LogUploader(client, new LogMask(@"C:\Users\chris", "chris"), Bin)
            .UploadAsync(files, progress, cancellationToken);
    }

    // Progress<T> posts to the thread pool; a synchronous recorder keeps the order the uploader reported.
    private sealed class Recorder(List<LogUploadProgress> steps) : IProgress<LogUploadProgress>
    {
        public void Report(LogUploadProgress value)
        {
            steps.Add(value);
        }
    }

    /// <summary>filebin.net as probed: 201 with JSON for a stored file, 200 for a deleted bin.</summary>
    private sealed class Filebin : HttpMessageHandler
    {
        internal Dictionary<string, byte[]> Files { get; } = [];
        internal List<string> Deleted { get; } = [];
        internal List<string?> ContentTypes { get; } = [];
        internal int Posts { get; private set; }
        internal bool Unreachable { get; init; }
        internal bool RefuseDelete { get; init; }
        internal int? KeepBytes { get; init; }
        internal Func<int, (HttpStatusCode Status, string Body)?> Refuse { get; init; } = _ => null;
        internal Action<int> BeforePost { get; init; } = _ => { };

        internal string Text(string path)
        {
            return Encoding.UTF8.GetString(Files[path]);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (Unreachable)
            {
                throw new HttpRequestException("No such host is known.");
            }

            Assert.Equal("filebin.net", request.RequestUri!.Host);
            Assert.Equal("https", request.RequestUri.Scheme);
            var path = request.RequestUri.AbsolutePath;
            if (request.Method == HttpMethod.Delete)
            {
                Deleted.Add(path);
                if (RefuseDelete)
                {
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                }

                foreach (var file in Files.Keys.Where(key => key.StartsWith(path + "/", StringComparison.Ordinal))
                             .ToArray())
                {
                    Files.Remove(file);
                }

                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            Assert.Equal(HttpMethod.Post, request.Method);
            BeforePost(++Posts);
            cancellationToken.ThrowIfCancellationRequested();
            if (Refuse(Posts) is { } refused)
            {
                return new HttpResponseMessage(refused.Status) { Content = new StringContent(refused.Body) };
            }

            ContentTypes.Add(request.Content!.Headers.ContentType?.MediaType);
            var body = await request.Content.ReadAsByteArrayAsync(cancellationToken);
            Files[path] = body;
            var bin = path.Split('/')[1];
            var answer = JsonSerializer.Serialize(new
            {
                bin = new { id = bin, readonly_ = false, expired_at = "2026-11-08T12:00:00Z" },
                file = new { filename = path.Split('/')[2], bytes = KeepBytes ?? body.Length, md5 = "x" }
            });
            return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent(answer) };
        }
    }
}
