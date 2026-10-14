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

    [Theory]
    [InlineData("download", true)]
    [InlineData("bundled", false)]
    public void DistributionMarkerSelectsPrerequisiteAcquisition(string mode, bool online)
    {
        using var payload = SetupPayload.OpenArchive(Archive(
            "{\"schemaVersion\":1,\"wsgmVersion\":\"2.1.0\"}",
            $"{{\"schemaVersion\":1,\"prerequisites\":\"{mode}\"}}"));

        Assert.Equal(online, payload.Online);
        Assert.True(payload.DistributionSpecified);
    }

    [Fact]
    public void LegacyPayloadDoesNotOptIntoOnlinePrerequisites()
    {
        using var payload = SetupPayload.OpenArchive(Archive("{\"schemaVersion\":1,\"wsgmVersion\":\"2.1.0\"}"));

        Assert.False(payload.Online);
        Assert.False(payload.DistributionSpecified);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"schemaVersion\":2,\"prerequisites\":\"download\"}")]
    [InlineData("{\"schemaVersion\":\"1\",\"prerequisites\":\"download\"}")]
    [InlineData("{\"schemaVersion\":1,\"prerequisites\":true}")]
    [InlineData("{\"schemaVersion\":1,\"prerequisites\":\"unknown\"}")]
    public void InvalidDistributionRefusesAndReleasesTheArchive(string marker)
    {
        var stream = Archive("{\"schemaVersion\":1,\"wsgmVersion\":\"2.1.0\"}", marker);

        Assert.Throws<InvalidDataException>(() => SetupPayload.OpenArchive(stream));
        Assert.Equal(1, stream.Disposals);
    }

    [Theory]
    [InlineData("download", true)]
    [InlineData("bundled", false)]
    public void DirectoryPayloadUsesTheSameDistributionMarker(string mode, bool online)
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(directory.GetPath("bundle.json"), "{\"schemaVersion\":1,\"wsgmVersion\":\"2.1.0\"}");
        File.WriteAllText(directory.GetPath("setup-distribution.json"),
            $"{{\"schemaVersion\":1,\"prerequisites\":\"{mode}\"}}");
        using var payload = SetupPayload.Open(directory.GetPath("."));

        Assert.Equal(online, payload!.Online);
        Assert.True(payload.DistributionSpecified);
    }

    private static TrackedStream Archive(string? bundle, string? distribution = null)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        {
            if (bundle is not null)
            {
                using var manifest = archive.CreateEntry("bundle.json").Open();
                manifest.Write(Encoding.UTF8.GetBytes(bundle));
            }

            if (distribution is not null)
            {
                using var marker = archive.CreateEntry("setup-distribution.json").Open();
                marker.Write(Encoding.UTF8.GetBytes(distribution));
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
