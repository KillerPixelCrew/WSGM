using System.IO.Compression;
using System.Text;
using WSGM.Setup;
using WSGM.Testing;

namespace WSGM.Tests.Setup;

public sealed class SetupPayloadTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{\"schemaVersion\":2,\"wsgmVersion\":\"2.1.0\"}")]
    public void FailedBundleOpenReleasesThePayloadStream(string? bundle)
    {
        var stream = Archive(bundle);
        Assert.ThrowsAny<Exception>(() => SetupPayload.OpenArchive(stream));
        Assert.Equal(1, stream.Disposals);
        Assert.False(stream.CanRead);
    }

    [Fact]
    public void InvalidArchiveReleasesThePayloadStream()
    {
        var stream = new TrackedStream([1, 2, 3]);
        Assert.Throws<InvalidDataException>(() => SetupPayload.OpenArchive(stream));
        Assert.Equal(1, stream.Disposals);
    }

    [Fact]
    public void SuccessfulOpenKeepsTheArchiveUntilThePayloadOwnerRetires()
    {
        var stream = Archive("{\"schemaVersion\":1,\"wsgmVersion\":\"2.1.0\"}");
        using var destination = new TemporaryDirectory();
        using (var payload = SetupPayload.OpenArchive(stream))
        {
            Assert.Equal(0, stream.Disposals);
            payload.Extract("App", destination.Root);
            Assert.Equal("application", File.ReadAllText(destination.GetPath("app.txt")));
        }

        Assert.Equal(1, stream.Disposals);
        Assert.False(stream.CanRead);
    }

    private static TrackedStream Archive(string? bundle)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        {
            if (bundle is not null)
            {
                using var manifest = archive.CreateEntry("bundle.json").Open();
                manifest.Write(Encoding.UTF8.GetBytes(bundle));
            }

            using var application = archive.CreateEntry("App/app.txt").Open();
            application.Write(Encoding.UTF8.GetBytes("application"));
        }

        return new TrackedStream(buffer.ToArray());
    }

    private sealed class TrackedStream(byte[] bytes) : MemoryStream(bytes)
    {
        internal int Disposals { get; private set; }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Disposals++;
            }

            base.Dispose(disposing);
        }
    }
}
