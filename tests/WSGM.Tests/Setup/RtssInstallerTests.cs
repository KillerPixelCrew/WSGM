using System.Net;
using System.Text;
using WSGM.Setup;
using WSGM.Setup.Engine;

namespace WSGM.Tests.Setup;

public sealed class RtssInstallerTests
{
    [Fact]
    public void Matches_RefusesAnythingButThePinnedBuild()
    {
        using MemoryStream other = new([0x50, 0x4B, 0x03, 0x04]);

        Assert.False(RtssInstaller.Matches(other));
    }

    [Fact]
    public void Pin_NamesOneVersionAcrossTheInstallerAndTheDownload()
    {
        var compact = RtssInstaller.Version.Replace(".", "", StringComparison.Ordinal);

        Assert.StartsWith($"RTSSSetup{compact}.", RtssInstaller.SetupEntry, StringComparison.Ordinal);
        Assert.Contains($"RTSSSetup{compact}Build", RtssInstaller.Download.OriginalString, StringComparison.Ordinal);
        Assert.Equal(64, RtssInstaller.Sha256.Length);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BundledArchiveNeverFallsBackToTheNetwork(bool included)
    {
        using SetupTestInstallation installation = new();
        using var payload = Payload(installation.User, "bundled", included);
        var handler = new RecordingHandler(() => new HttpResponseMessage(HttpStatusCode.OK));
        var destination = Path.Combine(installation.Machine, "rtss.zip");

        var acquired = RtssInstaller.AcquireArchive(payload, handler, destination, out var problem);

        Assert.Equal(included, acquired);
        Assert.Equal(0, handler.Requests);
        Assert.True(handler.Disposed);
        if (included)
        {
            Assert.Equal("bundled-archive", File.ReadAllText(destination));
            Assert.Empty(problem);
        }
        else
        {
            Assert.Contains("offline setup is missing RTSS", problem, StringComparison.Ordinal);
            Assert.False(File.Exists(destination));
        }
    }

    [Theory]
    [InlineData("download")]
    [InlineData(null)]
    public void OnlineAndLegacyPayloadsRequestOnlyThePinnedArchive(string? mode)
    {
        using SetupTestInstallation installation = new();
        using var payload = Payload(installation.User, mode, included: false);
        var handler = new RecordingHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes("downloaded-archive"))
        });
        var destination = Path.Combine(installation.Machine, "rtss.zip");

        Assert.True(RtssInstaller.AcquireArchive(payload, handler, destination, out var problem));

        Assert.Equal(1, handler.Requests);
        Assert.Equal(RtssInstaller.Download, handler.RequestedUrl);
        Assert.True(handler.Disposed);
        Assert.Empty(problem);
        using var downloaded = File.OpenRead(destination);
        Assert.False(RtssInstaller.Matches(downloaded));
    }

    [Fact]
    public void FailedDownloadIsNotRetriedAndDoesNotLeaveAnArchive()
    {
        using SetupTestInstallation installation = new();
        var handler = new RecordingHandler(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var destination = Path.Combine(installation.Machine, "rtss.zip");

        Assert.False(RtssInstaller.TryDownload(handler, destination, TimeSpan.FromSeconds(1), out var problem));

        Assert.Equal(1, handler.Requests);
        Assert.True(handler.Disposed);
        Assert.Contains("503", problem, StringComparison.Ordinal);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public void StalledBodyIsNotRetriedAndItsPartialArchiveIsRemoved()
    {
        using SetupTestInstallation installation = new();
        var handler = new RecordingHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new PartialThenStallStream())
        });
        var destination = Path.Combine(installation.Machine, "rtss.zip");

        Assert.False(RtssInstaller.TryDownload(handler, destination, TimeSpan.FromMilliseconds(50), out var problem));

        Assert.Equal(1, handler.Requests);
        Assert.Equal("it timed out", problem);
        Assert.True(handler.Disposed);
        Assert.False(File.Exists(destination));
    }

    private static SetupPayload Payload(string root, string? mode, bool included)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "bundle.json"), "{\"schemaVersion\":1,\"wsgmVersion\":\"2.1.0\"}");
        if (mode is not null)
        {
            File.WriteAllText(Path.Combine(root, "setup-distribution.json"),
                $"{{\"schemaVersion\":1,\"prerequisites\":\"{mode}\"}}");
        }

        if (included)
        {
            Directory.CreateDirectory(Path.Combine(root, "PerformanceRuntime"));
            File.WriteAllText(Path.Combine(root, "PerformanceRuntime", "rtss.zip"), "bundled-archive");
        }

        return SetupPayload.Open(root)!;
    }

    private sealed class RecordingHandler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        internal int Requests { get; private set; }
        internal Uri? RequestedUrl { get; private set; }
        internal bool Disposed { get; private set; }

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            RequestedUrl = request.RequestUri;
            return response();
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(Send(request, cancellationToken));
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class PartialThenStallStream : Stream
    {
        private bool _sent;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (!_sent)
            {
                _sent = true;
                buffer.Span[0] = 0x50;
                return 1;
            }

            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
