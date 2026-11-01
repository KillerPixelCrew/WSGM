using System.Net;
using System.Text;
using WSGM.Core;

namespace WSGM.Tests.Core;

/// <summary>Uploading masked logs to a fake paste.rs: whole, in line-boundary pieces after a 206, or not at all.</summary>
public sealed class LogUploaderTests : IDisposable
{
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
    public async Task ASmallLogGoesUpWholeAndMasked()
    {
        var log = Write("wsgm.log", @"started from C:\Users\chris\AppData\Local\WSGM by chris" + "\r\n");
        PasteServer server = new(1024);

        var links = await Upload(server, [log]);

        var link = Assert.Single(links);
        Assert.Equal(new LogUploadLink("wsgm.log", 1, "https://paste.rs/p1"), link);
        Assert.Equal("== WSGM wsgm.log, part 1 ==" + Environment.NewLine
                                                   + @"started from %USERPROFILE%\AppData\Local\WSGM by <user>" +
                                                   "\r\n", server.Text("p1"));
        Assert.Empty(server.Deleted);
    }

    [Fact]
    public async Task ALogOverTheLimitIsSentAgainInOrderedLinePiecesBelowIt()
    {
        var lines = Enumerable.Range(1, 200).Select(index => $"2026-11-01 12:00:00.000 [info ] line {index}\r\n")
            .ToArray();
        var log = Write("wsgm.log", string.Concat(lines));
        PasteServer server = new(1000);

        var links = await Upload(server, [log]);

        // The whole file came back cut short, was read back for its length and deleted.
        Assert.Equal(["p1"], server.Deleted);
        Assert.Equal(links.Count, server.Pastes.Count);
        Assert.True(links.Count > 1);
        Assert.Equal(Enumerable.Range(1, links.Count), links.Select(link => link.Piece));
        var bodies = links.Select(link => server.Text(link.Url[(link.Url.LastIndexOf('/') + 1)..])).ToArray();
        Assert.All(bodies, body => Assert.True(Encoding.UTF8.GetByteCount(body) < 1000));
        var pieces = bodies.Select((body, index) =>
        {
            var header = $"== WSGM wsgm.log, part {index + 1} ==" + Environment.NewLine;
            Assert.StartsWith(header, body, StringComparison.Ordinal);
            return body[header.Length..];
        }).ToArray();
        Assert.All(pieces, piece => Assert.EndsWith("\r\n", piece, StringComparison.Ordinal));
        Assert.Equal(string.Concat(lines), string.Concat(pieces));
    }

    [Fact]
    public async Task APieceIsHalvedWhenTheCutShortPasteCannotBeReadBack()
    {
        var log = Write("wsgm.log", string.Concat(Enumerable.Range(1, 100).Select(index => $"line {index:000}\n")));
        PasteServer server = new(300) { ReadBack = false };

        var links = await Upload(server, [log]);

        Assert.True(server.Deleted.Count >= 1);
        Assert.Equal(links.Count, server.Pastes.Count);
        Assert.Equal(File.ReadAllText(log), string.Concat(links.Select(link =>
        {
            var body = server.Text(link.Url[(link.Url.LastIndexOf('/') + 1)..]);
            return body[(body.IndexOf('\n') + 1)..];
        })));
    }

    [Fact]
    public async Task ALineLongerThanAPieceContinuesInTheNextWithoutSplittingACharacter()
    {
        var line = string.Concat(Enumerable.Repeat("ab\U0001F600", 40));

        var first = LogUploader.Take(line, 0, 9);

        Assert.Equal(6, first);
        Assert.Equal("ab\U0001F600ab", line[..first]);
        Assert.Equal(first + 4, LogUploader.Take(line, first, 7));
    }

    [Fact]
    public void WholeLinesFillAPieceUpToItsSize()
    {
        Assert.Equal(5, LogUploader.Take("abc\r\nd\nefgh\n", 0, 6));
        Assert.Equal(7, LogUploader.Take("abc\r\nd\nefgh\n", 0, 7));
        Assert.Equal(12, LogUploader.Take("abc\r\nd\nefgh\n", 0, 12));
    }

    [Fact]
    public async Task ThePreviousSessionIsASeparatePartAfterTheLiveLog()
    {
        var live = Write("wsgm.log", "now\n");
        var previous = Write("wsgm.old.log", "before\n");
        PasteServer server = new(1024);

        var files = LogUploader.Find(live, previous);
        var links = await Upload(server, files);

        Assert.Equal([false, true], files.Select(file => file.Previous));
        Assert.Equal(["wsgm.log", "wsgm.old.log"], links.Select(link => link.FileName));
        Assert.StartsWith("== WSGM wsgm.old.log (previous session), part 1 ==", server.Text("p2"),
            StringComparison.Ordinal);
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
        PasteServer server = new(1024);

        await Upload(server, [log]);

        Assert.EndsWith("first\nsecond\n", server.Text("p1"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "limiting uploads right now (HTTP 429)")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "refused the upload with HTTP 503")]
    [InlineData(HttpStatusCode.OK, "answered HTTP 200")]
    public async Task ARefusedPieceFailsWithItsStatusAndRemovesThePiecesAlreadyUploaded(HttpStatusCode status,
        string reason)
    {
        var live = Write("wsgm.log", "now\n");
        var previous = Write("wsgm.old.log", "before\n");
        PasteServer server = new(1024) { Refuse = post => post == 2 ? status : null };

        var failure = await Assert.ThrowsAsync<LogUploadException>(() => Upload(server, [live, previous]));

        Assert.False(failure.Cancelled);
        Assert.Contains(reason, failure.Message, StringComparison.Ordinal);
        Assert.Contains("The 1 part(s) already uploaded were removed.", failure.Message, StringComparison.Ordinal);
        Assert.Equal(["p1"], server.Deleted);
        Assert.Empty(server.Pastes);
    }

    [Fact]
    public async Task ANetworkFailureSaysPasteRsCouldNotBeReached()
    {
        var log = Write("wsgm.log", "now\n");
        PasteServer server = new(1024) { Unreachable = true };

        var failure = await Assert.ThrowsAsync<LogUploadException>(() => Upload(server, [log]));

        Assert.StartsWith("Could not reach paste.rs: ", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAnswerThatIsNotALinkFails()
    {
        var log = Write("wsgm.log", "now\n");
        PasteServer server = new(1024) { Answer = "<html>busy</html>" };

        var failure = await Assert.ThrowsAsync<LogUploadException>(() => Upload(server, [log]));

        Assert.Equal("paste.rs answered with something other than a link.", failure.Message);
    }

    [Fact]
    public async Task AMissingFileFailsBeforeAnythingIsSent()
    {
        PasteServer server = new(1024);

        var failure = await Assert.ThrowsAsync<LogUploadException>(() =>
            Upload(server, [Path.Combine(_directory, "wsgm.log")]));

        Assert.StartsWith("Could not read wsgm.log: ", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, server.Posts);
    }

    [Fact]
    public async Task CancellingRemovesThePiecesAlreadyUploaded()
    {
        var live = Write("wsgm.log", "now\n");
        var previous = Write("wsgm.old.log", "before\n");
        using CancellationTokenSource cancel = new();
        PasteServer server = new(1024)
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
        Assert.Equal("Upload cancelled. The 1 part(s) already uploaded were removed.", failure.Message);
        Assert.Empty(server.Pastes);
    }

    [Fact]
    public async Task TheProgressNamesEachPieceAsItIsSent()
    {
        var live = Write("wsgm.log", "now\n");
        var previous = Write("wsgm.old.log", "before\n");
        PasteServer server = new(1024);
        List<LogUploadProgress> steps = [];

        await Upload(server, [live, previous], progress: new Recorder(steps));

        Assert.Equal([new LogUploadProgress("wsgm.log", 1), new LogUploadProgress("wsgm.old.log", 1)], steps);
    }

    private string Write(string name, string text)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, text);
        return path;
    }

    private static Task<IReadOnlyList<LogUploadLink>> Upload(PasteServer server, IEnumerable<string> paths,
        CancellationToken cancellationToken = default, IProgress<LogUploadProgress>? progress = null)
    {
        return Upload(server, paths.Select(path =>
                new LogUploadFile(Path.GetFileName(path), path, 0, Path.GetFileName(path) == "wsgm.old.log"))
            .ToArray(), cancellationToken, progress);
    }

    private static async Task<IReadOnlyList<LogUploadLink>> Upload(PasteServer server,
        IReadOnlyList<LogUploadFile> files, CancellationToken cancellationToken = default,
        IProgress<LogUploadProgress>? progress = null)
    {
        using DownloadClient client = new(server);
        return await new LogUploader(client, new LogMask(@"C:\Users\chris", "chris"))
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

    /// <summary>paste.rs as its page describes it: 201 for a whole paste, 206 when the body reached the limit.</summary>
    private sealed class PasteServer(int limit) : HttpMessageHandler
    {
        internal Dictionary<string, byte[]> Pastes { get; } = [];
        internal List<string> Deleted { get; } = [];
        internal int Posts { get; private set; }
        internal bool ReadBack { get; init; } = true;
        internal bool Unreachable { get; init; }
        internal string? Answer { get; init; }
        internal Func<int, HttpStatusCode?> Refuse { get; init; } = _ => null;
        internal Action<int> BeforePost { get; init; } = _ => { };

        internal string Text(string id)
        {
            return Encoding.UTF8.GetString(Pastes[id]);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (Unreachable)
            {
                throw new HttpRequestException("No such host is known.");
            }

            var id = request.RequestUri!.AbsolutePath.Trim('/');
            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal("https://paste.rs/", request.RequestUri.AbsoluteUri);
                BeforePost(++Posts);
                cancellationToken.ThrowIfCancellationRequested();
                if (Refuse(Posts) is { } refused)
                {
                    return new HttpResponseMessage(refused) { Content = new StringContent("https://paste.rs/x") };
                }

                var body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                var name = "p" + Posts;
                Pastes[name] = body[..Math.Min(body.Length, limit)];
                return new HttpResponseMessage(body.Length >= limit
                    ? HttpStatusCode.PartialContent
                    : HttpStatusCode.Created)
                {
                    Content = new StringContent(Answer ?? $"https://paste.rs/{name}\n")
                };
            }

            if (request.Method == HttpMethod.Delete)
            {
                Deleted.Add(id);
                return new HttpResponseMessage(Pastes.Remove(id) ? HttpStatusCode.OK : HttpStatusCode.NotFound);
            }

            return ReadBack && Pastes.TryGetValue(id, out var stored)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(stored) }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }
}
