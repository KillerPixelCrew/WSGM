using System.Security.Cryptography;
using WSGM.Core;
using WSGM.Shell;
using WSGM.Tests.Core;

namespace WSGM.Tests.Shell;

/// <summary>
///     Updating the Xbox app: retail first, the Insider channel only when retail has nothing new enough, the
///     download checked against Microsoft's digest before Windows installs it, and every stop reported.
/// </summary>
public sealed class XboxAppUpdaterTests
{
    private static readonly byte[] Payload = Enumerable.Range(0, 5000).Select(value => (byte)value).ToArray();
    private static readonly Version Old = new(2611, 1001, 10, 0);
    private static readonly Version New = new(2612, 1001, 23, 0);

    [Fact]
    public async Task AVerifiedDownloadIsInstalledAndRemoved()
    {
        var fake = new Fake { RetailOffers = true };
        using var updater = fake.Create();

        await updater.RunAsync(CancellationToken.None);

        Assert.Equal(XboxAppUpdatePhase.Updated, updater.Status.Phase);
        Assert.Equal(New, updater.Status.Installed);
        Assert.Equal(["Retail"], fake.AskedRings);
        var installed = Assert.Single(fake.Installed);
        Assert.Equal(Payload, fake.InstalledBytes);
        Assert.False(File.Exists(installed.LocalPath));
    }

    [Fact]
    public async Task TheInsiderChannelIsAskedOnlyWhenRetailHasNothingNewEnough()
    {
        var fake = new Fake { RetailOffers = false };
        using var updater = fake.Create();

        await updater.RunAsync(CancellationToken.None);

        Assert.Equal(["Retail", "WIF"], fake.AskedRings);
        Assert.Equal(XboxAppUpdatePhase.Updated, updater.Status.Phase);
    }

    [Fact]
    public async Task ADownloadThatDoesNotMatchMicrosoftsDigestIsNeverInstalled()
    {
        var fake = new Fake { RetailOffers = true, Digest = new byte[32] };
        using var updater = fake.Create();

        await updater.RunAsync(CancellationToken.None);

        Assert.Equal(XboxAppUpdatePhase.Failed, updater.Status.Phase);
        Assert.Contains("digest", updater.Status.Message);
        Assert.Empty(fake.Installed);
        Assert.Empty(Directory.EnumerateFiles(fake.DownloadFolder, fake.MonikerPattern));
    }

    [Fact]
    public async Task NoOfferAnywhereIsReportedWithoutDownloading()
    {
        var fake = new Fake { RetailOffers = false, InsiderOffers = false };
        using var updater = fake.Create();

        await updater.RunAsync(CancellationToken.None);

        Assert.Equal(XboxAppUpdatePhase.Failed, updater.Status.Phase);
        Assert.Contains(New.ToString(), updater.Status.Message);
        Assert.Equal(0, fake.Downloads);
    }

    [Fact]
    public async Task CancellingTheDownloadChangesNothingAndSaysSo()
    {
        var fake = new Fake { RetailOffers = true };
        using var updater = fake.Create();
        fake.OnRead = updater.Cancel;

        await updater.RunAsync(CancellationToken.None);

        Assert.Equal(XboxAppUpdatePhase.Failed, updater.Status.Phase);
        Assert.Contains("cancelled", updater.Status.Message);
        Assert.Empty(fake.Installed);
    }

    [Fact]
    public async Task ADownloadThatStopsDeliveringFailsInsteadOfWaitingForever()
    {
        var fake = new Fake { RetailOffers = true, Stalls = true };
        using var updater = fake.Create(TimeSpan.FromMilliseconds(100));

        await updater.RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(XboxAppUpdatePhase.Failed, updater.Status.Phase);
        Assert.Contains("stopped receiving data", updater.Status.Message);
        Assert.Empty(fake.Installed);
    }

    [Fact]
    public void RefreshFollowsTheInstalledVersion()
    {
        var fake = new Fake();
        using var updater = fake.Create();

        updater.Refresh();
        Assert.Equal(XboxAppUpdatePhase.Needed, updater.Status.Phase);

        fake.Version = New;
        updater.Refresh();
        Assert.Equal(XboxAppUpdatePhase.NotNeeded, updater.Status.Phase);

        fake.Version = null;
        updater.Refresh();
        Assert.Equal(XboxAppUpdatePhase.NotNeeded, updater.Status.Phase);
    }

    private sealed class Fake
    {
        internal List<string> AskedRings { get; } = [];
        internal List<Uri> Installed { get; } = [];
        internal byte[]? InstalledBytes { get; private set; }
        internal int Downloads { get; private set; }
        internal bool RetailOffers { get; init; }
        internal bool InsiderOffers { get; init; } = true;
        internal byte[] Digest { get; init; } = SHA256.HashData(Payload);
        internal Version? Version { get; set; } = Old;
        internal Action? OnRead { get; set; }
        internal string DownloadFolder => Path.Combine(Path.GetTempPath(), "WSGM", "xbox-app-update");
        internal string MonikerPattern => Moniker + "*";

        private string Moniker => "Microsoft.GamingApp_" + New + "_neutral_~_8wekyb3d8bbwe";

        internal bool Stalls { get; init; }

        internal XboxAppUpdater Create(TimeSpan? stallTimeout = null)
        {
            StoreDeliveryClient store = new(Post);
            return new XboxAppUpdater(store, () => Version, DownloadAsync, InstallAsync, stallTimeout);
        }

        private Task<string> Post(string url, string envelope, CancellationToken cancellationToken)
        {
            if (envelope.Contains("/GetCookie<", StringComparison.Ordinal))
            {
                return Task.FromResult(StoreDeliveryClientTests.Cookie("cookie"));
            }

            if (envelope.Contains("/SyncUpdates<", StringComparison.Ordinal))
            {
                var ring = envelope.Contains("FlightRing=WIF", StringComparison.Ordinal) ? "WIF" : "Retail";
                AskedRings.Add(ring);
                var offers = ring == "WIF" ? InsiderOffers : RetailOffers;
                return Task.FromResult(offers
                    ? StoreDeliveryClientTests.Sync((1, "update", 1, Moniker, Payload.Length, Digest))
                    : StoreDeliveryClientTests.Sync());
            }

            return Task.FromResult(StoreDeliveryClientTests.Urls(
                "https://tlu.dl.delivery.mp.microsoft.com/filestreamingservice/files/x"));
        }

        private Task<Stream> DownloadAsync(string url, CancellationToken cancellationToken)
        {
            Downloads++;
            return Task.FromResult<Stream>(Stalls
                ? new StalledStream()
                : new ChunkedStream(Payload, () => OnRead?.Invoke()));
        }

        private Task InstallAsync(Uri bundle, IProgress<uint> progress, CancellationToken cancellationToken)
        {
            Installed.Add(bundle);
            InstalledBytes = File.ReadAllBytes(bundle.LocalPath);
            progress.Report(100);
            Version = New;
            return Task.CompletedTask;
        }
    }

    /// <summary>An open connection that never delivers a byte.</summary>
    private sealed class StalledStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    /// <summary>Hands out a few bytes per read and runs a callback before each, so a test can act mid-download.</summary>
    private sealed class ChunkedStream(byte[] data, Action beforeRead) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            beforeRead();
            cancellationToken.ThrowIfCancellationRequested();
            return base.ReadAsync(buffer[..Math.Min(buffer.Length, 1000)], cancellationToken);
        }
    }
}
