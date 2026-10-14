using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using Microsoft.Win32;

namespace WSGM.Setup.Engine;

/// <summary>
///     RivaTuner Statistics Server, which WSGM's frame limit, performance overlay and AutoTDP drive. Setup
///     obtains one pinned Guru3D build from the payload or its mirror, checks its SHA-256 and only then runs it. Uninstall
///     leaves RTSS in place, because other programs use it too.
/// </summary>
internal static class RtssInstaller
{
    /// <summary>The pinned RTSS version.</summary>
    internal static string Version => Pin.Version;

    /// <summary>SHA-256 of <see cref="Download" />, as winget's Guru3D.RTSS 7.3.7 manifest pins it.</summary>
    internal static string Sha256 => Pin.Sha256;

    /// <summary>The installer inside the archive.</summary>
    internal static string SetupEntry => Pin.SetupEntry;

    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\RTSS";

    // HttpClient's timeout ends with the headers; a mirror that stops sending the body is caught by this.
    internal static readonly TimeSpan BodyStallTimeout = TimeSpan.FromSeconds(30);

    private static readonly (string Version, string Sha256, string SetupEntry, string Asset, Uri Download) Pin = ReadPin();

    /// <summary>
    ///     The archive, from the mirror winget's manifest uses. Guru3D's own download page has no stable file link.
    /// </summary>
    internal static Uri Download => Pin.Download;

    /// <summary>Whether RTSS is installed, by whoever: the registration WSGM's RTSS discovery reads.</summary>
    /// <returns>True when an RTSS registration names Unwinder; does not validate runtime readiness or version.</returns>
    public static bool Present()
    {
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            try
            {
                using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = machine.OpenSubKey(UninstallKey, false);
                if (string.Equals((key?.GetValue("Publisher") as string)?.Trim(), "Unwinder", StringComparison.Ordinal))
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SetupLog.Warn($"RTSS: the {view} registration could not be read: {ex.Message}");
            }
        }

        return false;
    }

    /// <summary>Downloads, checks and silently installs the pinned RTSS, unless one is installed already.</summary>
    /// <param name="step">The progress line.</param>
    /// <returns>Whether RTSS is installed afterwards.</returns>
    public static bool Install(SetupStep step)
    {
        return Install(null, step);
    }

    /// <summary>Installs bundled RTSS offline, or downloads it for online and legacy payloads.</summary>
    /// <param name="payload">Payload acquisition policy; null retains the legacy download behavior.</param>
    /// <param name="step">The progress line.</param>
    /// <returns>Whether RTSS is installed afterwards.</returns>
    public static bool Install(SetupPayload? payload, SetupStep step)
    {
        if (Present())
        {
            step.DoneLabel = "RivaTuner Statistics Server already installed";
            step.State = StepState.Skipped;
            return true;
        }

        var stage = Path.Combine(Path.GetTempPath(), $"wsgm-rtss-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stage);
        try
        {
            var archive = Path.Combine(stage, Pin.Asset);
            if (!AcquireArchive(payload, new SocketsHttpHandler(), archive, out var problem))
            {
                step.Note = problem;
                return false;
            }

            using (var stream = File.OpenRead(archive))
            {
                if (!Matches(stream))
                {
                    step.Note = "The RTSS archive was not the expected build, so setup did not run it. Install "
                                + "it from guru3d.com.";
                    return false;
                }
            }

            var setup = Path.Combine(stage, SetupEntry);
            using (var zip = ZipFile.OpenRead(archive))
            {
                if (zip.GetEntry(SetupEntry) is not { } entry)
                {
                    step.Note = "The RTSS archive holds no installer. Install it from guru3d.com.";
                    return false;
                }

                entry.ExtractToFile(setup);
            }

            var code = WindowsSetup.Run(setup, "/S");
            if (code == 0 && Present())
            {
                return true;
            }

            step.Note =
                $"RTSS setup exited with code {code}. Install it from guru3d.com and WSGM uses it from then on.";
            return false;
        }
        finally
        {
            try
            {
                Directory.Delete(stage, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SetupLog.Warn($"RTSS: the download folder could not be removed: {ex.Message}");
            }
        }
    }

    /// <summary>Uses the explicit bundled payload without contacting the network; legacy payloads download.</summary>
    /// <param name="payload">The setup payload, or null for legacy acquisition.</param>
    /// <param name="handler">Owned download handler; disposed even when no network is allowed.</param>
    /// <param name="destination">Temporary archive path.</param>
    /// <param name="problem">An actionable acquisition failure, or empty.</param>
    /// <returns>Whether the archive is available for checksum validation.</returns>
    internal static bool AcquireArchive(SetupPayload? payload, HttpMessageHandler handler, string destination,
        out string problem)
    {
        if (payload is not { Online: false, DistributionSpecified: true })
        {
            if (TryDownload(handler, destination, BodyStallTimeout, out var downloadProblem))
            {
                problem = "";
                return true;
            }

            problem = $"Setup could not download RTSS ({downloadProblem}). Connect to the internet and run Repair, or install it from guru3d.com.";
            return false;
        }

        using (handler)
        {
            try
            {
                payload.ExtractFile("PerformanceRuntime/" + Pin.Asset, destination);
                problem = "";
                return true;
            }
            catch (FileNotFoundException)
            {
                problem = "This offline setup is missing RTSS. Use a complete offline setup, or install RTSS from guru3d.com.";
                return false;
            }
        }
    }

    /// <summary>Whether a download is exactly the pinned build.</summary>
    /// <param name="archive">The downloaded archive.</param>
    /// <returns>Whether its SHA-256 is <see cref="Sha256" />.</returns>
    internal static bool Matches(Stream archive)
    {
        return string.Equals(Convert.ToHexStringLower(SHA256.HashData(archive)), Sha256, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Fetches the pinned archive once; the hash check afterwards decides whether it is used.</summary>
    /// <param name="handler">The network, or a test's fake. The client owns and disposes it.</param>
    /// <param name="destination">Where the archive goes.</param>
    /// <param name="stallTimeout">How long one body read may wait for data.</param>
    /// <param name="problem">Why the download failed, or empty.</param>
    /// <returns>Whether the whole body arrived.</returns>
    internal static bool TryDownload(HttpMessageHandler handler, string destination, TimeSpan stallTimeout,
        out string problem)
    {
        return TryDownload(handler, Download, destination, stallTimeout, out problem);
    }

    /// <summary>Fetches one pinned prerequisite URL without retries; each body read has a stall limit.</summary>
    /// <param name="handler">Owned network handler, or a test's fake.</param>
    /// <param name="download">The prerequisite's pinned HTTPS URL.</param>
    /// <param name="destination">Destination in the prerequisite's temporary staging folder.</param>
    /// <param name="stallTimeout">Maximum time for one body read.</param>
    /// <param name="problem">Actionable failure detail, or empty.</param>
    /// <returns>Whether the complete response body arrived.</returns>
    internal static bool TryDownload(HttpMessageHandler handler, Uri download, string destination,
        TimeSpan stallTimeout, out string problem)
    {
        var complete = false;
        try
        {
            if (!download.IsAbsoluteUri || download.Scheme != Uri.UriSchemeHttps)
            {
                problem = "the setup download URL is invalid";
                handler.Dispose();
                return false;
            }

            using HttpClient client = new(handler) { Timeout = TimeSpan.FromMinutes(5) };
            using HttpRequestMessage request = new(HttpMethod.Get, download);
            using var response = client.Send(request,
                HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
            {
                problem = $"the server answered {(int)response.StatusCode}";
                return false;
            }

            using var source = response.Content.ReadAsStream();
            using var target = File.Create(destination);
            var total = CopyBody(source, target, stallTimeout);
            SetupLog.Info($"Setup: downloaded {total} bytes from {download.Host}.");
            problem = "";
            complete = true;
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            SetupLog.Warn($"Setup: the download failed: {ex.Message}");
            problem = ex is OperationCanceledException ? "it timed out" : "no connection";
            return false;
        }
        finally
        {
            if (!complete)
            {
                try
                {
                    File.Delete(destination);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    SetupLog.Warn($"Setup: a partial prerequisite download could not be removed: {ex.Message}");
                }
            }
        }
    }

    private static (string Version, string Sha256, string SetupEntry, string Asset, Uri Download) ReadPin()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("WSGM.RtssRuntimePin")
                           ?? throw new InvalidOperationException("The RTSS runtime pin is missing.");
        using var pin = JsonDocument.Parse(stream);
        var root = pin.RootElement;
        var asset = root.GetProperty("asset").GetString()!;
        var setupEntry = root.GetProperty("setupEntry").GetString()!;
        var download = new Uri(root.GetProperty("url").GetString()!, UriKind.Absolute);
        if (Path.GetFileName(asset) != asset || Path.GetFileName(setupEntry) != setupEntry
            || download.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidDataException("The RTSS runtime pin is invalid.");
        }

        return (root.GetProperty("version").GetString()!, root.GetProperty("sha256").GetString()!,
            setupEntry, asset, download);
    }

    internal static long CopyBody(Stream source, Stream target, TimeSpan stallTimeout)
    {
        using CancellationTokenSource reading = new();
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            int read;
            reading.CancelAfter(stallTimeout);
            try
            {
                read = source.ReadAsync(buffer.AsMemory(), reading.Token).AsTask().GetAwaiter().GetResult();
            }
            finally
            {
                reading.CancelAfter(Timeout.InfiniteTimeSpan);
            }

            if (read == 0)
            {
                return total;
            }

            target.Write(buffer, 0, read);
            total += read;
        }
    }
}
