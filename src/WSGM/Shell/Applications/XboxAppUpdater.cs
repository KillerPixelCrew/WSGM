using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Management.Deployment;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Where an Xbox app update stands.</summary>
internal enum XboxAppUpdatePhase
{
    /// <summary>The Xbox app is missing or already new enough.</summary>
    NotNeeded,

    /// <summary>The installed build cannot be shown in Game Mode; nothing has started.</summary>
    Needed,

    /// <summary>Asking Microsoft which build to install.</summary>
    Checking,

    /// <summary>Downloading the build.</summary>
    Downloading,

    /// <summary>Windows is installing the build.</summary>
    Installing,

    /// <summary>The build was installed.</summary>
    Updated,

    /// <summary>The update stopped; <see cref="XboxAppUpdateStatus.Message" /> says why.</summary>
    Failed
}

/// <summary>A snapshot of the Xbox app update.</summary>
/// <param name="Phase">Where it stands.</param>
/// <param name="Installed">The installed version, or null when the Xbox app is not installed.</param>
/// <param name="Target">The version being installed, once known.</param>
/// <param name="Done">Bytes downloaded, or installation percent while installing.</param>
/// <param name="Total">Bytes to download, or 100 while installing.</param>
/// <param name="Message">Why it failed, or empty.</param>
internal sealed record XboxAppUpdateStatus(
    XboxAppUpdatePhase Phase,
    Version? Installed,
    Version? Target,
    long Done,
    long Total,
    string Message)
{
    /// <summary>No update applies.</summary>
    internal static readonly XboxAppUpdateStatus None = new(XboxAppUpdatePhase.NotNeeded, null, null, 0, 0, "");

    /// <summary>Whether an update is running.</summary>
    internal bool IsRunning => Phase is XboxAppUpdatePhase.Checking or XboxAppUpdatePhase.Downloading
        or XboxAppUpdatePhase.Installing;
}

/// <summary>Updates the Xbox app to a build whose own dashboard Game Mode can show.</summary>
/// <remarks>
///     <para>
///         The build comes from Microsoft's Store delivery service and network, never a third party. Retail
///         is asked first and the Xbox Insider channel only when retail has nothing new enough. The download
///         must match the SHA-256 digest Microsoft publishes for it, and Windows then checks the package's
///         own signature and identity while installing it.
///     </para>
///     <para>
///         Only a user's explicit choice starts it. A failed or cancelled update is reported and never
///         retried on its own. Installing closes a running Xbox app, as the Store does.
///     </para>
/// </remarks>
internal sealed class XboxAppUpdater : IDisposable
{
    private readonly Func<string, CancellationToken, Task<Stream>> _download;
    private readonly Lock _gate = new();
    private readonly HttpClient? _http;
    private readonly Func<Version?> _installedVersion;
    private readonly Func<Uri, IProgress<uint>, CancellationToken, Task> _install;
    private readonly TimeSpan _stallTimeout;
    private readonly StoreDeliveryClient _store;
    private CancellationTokenSource? _run;
    private XboxAppUpdateStatus _status = XboxAppUpdateStatus.None;

    /// <summary>Creates the updater.</summary>
    /// <param name="store">The Store delivery client; null uses Microsoft's service.</param>
    /// <param name="installedVersion">Reads the installed Xbox app version; null asks Windows.</param>
    /// <param name="download">Opens a download stream; null uses HTTP.</param>
    /// <param name="install">Installs a downloaded bundle with percent progress; null asks Windows.</param>
    /// <param name="stallTimeout">How long a download may receive nothing before it fails; null is one minute.</param>
    internal XboxAppUpdater(StoreDeliveryClient? store = null, Func<Version?>? installedVersion = null,
        Func<string, CancellationToken, Task<Stream>>? download = null,
        Func<Uri, IProgress<uint>, CancellationToken, Task>? install = null, TimeSpan? stallTimeout = null)
    {
        _stallTimeout = stallTimeout ?? TimeSpan.FromMinutes(1);
        if (download is null)
        {
            _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        }

        _store = store ?? new StoreDeliveryClient();
        _installedVersion = installedVersion ?? ReadInstalledVersion;
        _download = download ?? OpenDownloadAsync;
        _install = install ?? InstallAsync;
    }

    /// <summary>The current state.</summary>
    internal XboxAppUpdateStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _run?.Cancel();
        }

        _http?.Dispose();
    }

    /// <summary>Raised from any thread whenever <see cref="Status" /> changes.</summary>
    internal event Action? Changed;

    /// <summary>Reads the installed version again, unless an update is running.</summary>
    internal void Refresh()
    {
        Version? installed;
        try
        {
            installed = _installedVersion();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"Xbox app version could not be read: {ex.Message}");
            return;
        }

        lock (_gate)
        {
            if (_status.IsRunning)
            {
                return;
            }

            // An outcome stays visible until the next update; the installed version still follows Windows.
            var phase = _status.Phase is XboxAppUpdatePhase.Updated or XboxAppUpdatePhase.Failed
                ? _status.Phase
                : XboxAppUpdatePlan.NeedsUpdate(installed)
                    ? XboxAppUpdatePhase.Needed
                    : XboxAppUpdatePhase.NotNeeded;
            if (phase == XboxAppUpdatePhase.Failed && !XboxAppUpdatePlan.NeedsUpdate(installed))
            {
                phase = XboxAppUpdatePhase.NotNeeded;
            }

            var next = _status with { Phase = phase, Installed = installed };
            if (next == _status)
            {
                return;
            }

            _status = next;
        }

        Changed?.Invoke();
    }

    /// <summary>Stops a running download. Installation cannot be interrupted once Windows has it.</summary>
    internal void Cancel()
    {
        lock (_gate)
        {
            if (_status.Phase is XboxAppUpdatePhase.Checking or XboxAppUpdatePhase.Downloading)
            {
                _run?.Cancel();
            }
        }
    }

    /// <summary>Finds, downloads and installs the build Game Mode needs.</summary>
    /// <param name="cancellationToken">Cancels the update while it is still downloading.</param>
    /// <returns>Completes when the update has succeeded, failed or been cancelled.</returns>
    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource run;
        lock (_gate)
        {
            if (_status.IsRunning)
            {
                return;
            }

            run = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _run = run;
        }

        string? file = null;
        try
        {
            var installed = _installedVersion();
            Publish(new XboxAppUpdateStatus(XboxAppUpdatePhase.Checking, installed, null, 0, 0, ""));
            StorePackageOffer? offer = null;
            var ring = "";
            foreach (var candidate in XboxAppUpdatePlan.Rings)
            {
                offer = XboxAppUpdatePlan.Choose(
                    await _store.FindAsync(XboxAppUpdatePlan.CategoryId, candidate, run.Token).ConfigureAwait(false),
                    installed);
                if (offer is not null)
                {
                    ring = candidate;
                    break;
                }
            }

            if (offer is null)
            {
                Fail(installed, null,
                    $"Microsoft offers no Xbox app build {XboxAppUpdatePlan.MinimumVersion} or newer yet.");
                return;
            }

            Log.Info($"Xbox app update: installing {offer.Moniker} from the {ring} channel over {installed}.");
            var url = await _store.ResolveDownloadAsync(offer, ring, run.Token).ConfigureAwait(false);
            Publish(new XboxAppUpdateStatus(XboxAppUpdatePhase.Downloading, installed, offer.Version, 0, offer.Size,
                ""));
            file = await DownloadAsync(url, offer, installed, run.Token).ConfigureAwait(false);
            if (file is null)
            {
                return;
            }

            Publish(new XboxAppUpdateStatus(XboxAppUpdatePhase.Installing, installed, offer.Version, 0, 100, ""));
            // Reported on Windows' callback thread as it happens; Progress<T> would post it, letting a late
            // report overwrite the outcome.
            var progress = new InlineProgress(percent => PublishInstalling(installed, offer.Version, percent));

            // Windows owns the installation from here; abandoning it half way would only leave it running.
            await _install(new Uri(file), progress, CancellationToken.None).ConfigureAwait(false);
            var now = _installedVersion();
            if (now is null || now < offer.Version!)
            {
                Fail(now, offer.Version, $"Windows reported success but the Xbox app is still {now}.");
                return;
            }

            Log.Info($"Xbox app update: {now} installed.");
            Publish(new XboxAppUpdateStatus(XboxAppUpdatePhase.Updated, now, offer.Version, 100, 100, ""));
        }
        catch (OperationCanceledException) when (run.IsCancellationRequested)
        {
            Log.Info("Xbox app update cancelled.");
            Fail(Status.Installed, Status.Target, "The update was cancelled. The Xbox app was not changed.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException
                                       or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            Log.Warn($"Xbox app update failed: {ex.Message}");
            Fail(Status.Installed, Status.Target, ex.Message);
        }
        finally
        {
            lock (_gate)
            {
                _run = null;
            }

            run.Dispose();
            if (file is not null)
            {
                TryDelete(file);
            }
        }
    }

    private async Task<string?> DownloadAsync(Uri url, StorePackageOffer offer, Version? installed,
        CancellationToken cancellationToken)
    {
        var folder = Path.Combine(Path.GetTempPath(), "WSGM", "xbox-app-update");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, offer.Moniker + ".msixbundle");
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var source = await _download(url.AbsoluteUri, cancellationToken).ConfigureAwait(false))
            await using (FileStream target = new(file, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16,
                             true))
            {
                var buffer = new byte[1 << 16];
                long done = 0;
                var reported = 0L;
                int read;
                while ((read = await ReadAsync(source, buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, read);
                    done += read;
                    if (done > offer.Size)
                    {
                        throw new InvalidOperationException("The download is larger than Microsoft says it is.");
                    }

                    // Progress is published per percent, not per buffer.
                    if (done - reported >= offer.Size / 100 || done == offer.Size)
                    {
                        reported = done;
                        Publish(new XboxAppUpdateStatus(XboxAppUpdatePhase.Downloading, installed, offer.Version,
                            done, offer.Size, ""));
                    }
                }

                if (done != offer.Size)
                {
                    throw new IOException("The download ended early. Check the connection and try again.");
                }
            }

            if (!hash.GetHashAndReset().AsSpan().SequenceEqual(offer.Sha256))
            {
                throw new InvalidOperationException(
                    "The download does not match the digest Microsoft published for it, so it was not installed.");
            }

            return file;
        }
        catch
        {
            TryDelete(file);
            throw;
        }
    }

    /// <summary>Reads once, failing when the server sends nothing for <see cref="_stallTimeout" />.</summary>
    /// <remarks>
    ///     A connection to Microsoft's network can stay open while delivering nothing; on the Atlas one sat at
    ///     71 MB for ten minutes. The stall is reported and the user decides whether to try again.
    /// </remarks>
    private async Task<int> ReadAsync(Stream source, byte[] buffer, CancellationToken cancellationToken)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stall.CancelAfter(_stallTimeout);
        try
        {
            return await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException(
                $"The download stopped receiving data for {_stallTimeout.TotalSeconds:0} seconds. Try again.");
        }
    }

    private void PublishInstalling(Version? installed, Version? target, uint percent)
    {
        lock (_gate)
        {
            if (_status.Phase != XboxAppUpdatePhase.Installing)
            {
                return;
            }

            _status = new XboxAppUpdateStatus(XboxAppUpdatePhase.Installing, installed, target,
                Math.Min(percent, 100u), 100, "");
        }

        Changed?.Invoke();
    }

    private void Fail(Version? installed, Version? target, string message)
    {
        Publish(new XboxAppUpdateStatus(XboxAppUpdatePhase.Failed, installed, target, 0, 0, message));
    }

    private void Publish(XboxAppUpdateStatus status)
    {
        lock (_gate)
        {
            _status = status;
        }

        Changed?.Invoke();
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Xbox app update download could not be removed: {ex.Message}");
        }
    }

    private async Task<Stream> OpenDownloadAsync(string url, CancellationToken cancellationToken)
    {
        var response = await _http!.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            response.Dispose();
            throw new HttpRequestException(
                $"Microsoft's download server refused the Xbox app ({(int)response.StatusCode}).", null,
                response.StatusCode);
        }

        return await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Version? ReadInstalledVersion()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var package = new PackageManager()
            .FindPackagesForUser(identity.User?.Value ?? string.Empty, XboxAppUpdatePlan.PackageFamily)
            .FirstOrDefault();
        if (package is null)
        {
            return null;
        }

        var version = package.Id.Version;
        return new Version(version.Major, version.Minor, version.Build, version.Revision);
    }

    /// <summary>Reports progress synchronously on the reporting thread.</summary>
    private sealed class InlineProgress(Action<uint> report) : IProgress<uint>
    {
        public void Report(uint value)
        {
            report(value);
        }
    }

    private static async Task InstallAsync(Uri bundle, IProgress<uint> progress, CancellationToken cancellationToken)
    {
        var operation = new PackageManager().AddPackageAsync(bundle, null, DeploymentOptions.ForceApplicationShutdown);
        operation.Progress = (_, value) => progress.Report(value.percentage);

        // The operation's own status is read rather than awaiting its completion callback. On the Atlas the
        // deployment finished in 23 seconds while the elevated process never received Completed, and the
        // update stayed at "Installing · 0 %" (8 November 2026). Progress callbacks remain best effort.
        while (operation.Status == AsyncStatus.Started)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        }

        if (operation.Status != AsyncStatus.Completed)
        {
            var code = operation.ErrorCode?.HResult ?? 0;
            throw new InvalidOperationException(
                $"Windows could not install the Xbox app ({operation.Status}, 0x{code:X8}).");
        }

        var result = operation.GetResults();
        if (result.ExtendedErrorCode is { } error && error.HResult < 0)
        {
            throw new InvalidOperationException(
                $"Windows could not install the Xbox app: {result.ErrorText} (0x{error.HResult:X8}).");
        }
    }
}
