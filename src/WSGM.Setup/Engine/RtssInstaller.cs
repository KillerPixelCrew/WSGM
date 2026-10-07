using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.Win32;

namespace WSGM.Setup.Engine;

/// <summary>
///     RivaTuner Statistics Server, which WSGM's frame limit, performance overlay and AutoTDP drive. Setup does not
///     carry it: it fetches one pinned Guru3D build, checks it against its SHA-256 and only then runs it. Uninstall
///     leaves RTSS in place, because other programs use it too.
/// </summary>
internal static class RtssInstaller
{
    /// <summary>The pinned RTSS version.</summary>
    internal const string Version = "7.3.7";

    /// <summary>SHA-256 of <see cref="Download" />, as winget's Guru3D.RTSS 7.3.7 manifest pins it.</summary>
    internal const string Sha256 = "9b084a8cb3e53ec1a673894d0b66e22b16c9fd8785636b020b2d422f3f2a820e";

    /// <summary>The installer inside the archive.</summary>
    internal const string SetupEntry = "RTSSSetup737.exe";

    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\RTSS";

    // HttpClient's timeout ends with the headers; a mirror that stops sending the body is caught by this.
    private static readonly TimeSpan BodyStallTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     The archive, from the mirror winget's manifest uses. Guru3D's own download page has no stable file link.
    /// </summary>
    internal static readonly Uri Download =
        new("https://ftp.nluug.nl/pub/games/PC/guru3d/afterburner/[Guru3D]-RTSSSetup737Build28314.zip");

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
            var archive = Path.Combine(stage, "rtss.zip");
            if (!TryDownload(new SocketsHttpHandler(), archive, BodyStallTimeout, out var problem))
            {
                step.Note = $"Setup could not download RTSS ({problem}). Install it from guru3d.com and WSGM uses "
                            + "it from then on.";
                return false;
            }

            using (var stream = File.OpenRead(archive))
            {
                if (!Matches(stream))
                {
                    step.Note = "The downloaded RTSS was not the expected build, so setup did not run it. Install "
                                + "it from guru3d.com.";
                    return false;
                }
            }

            var setup = Path.Combine(stage, SetupEntry);
            using (var zip = ZipFile.OpenRead(archive))
            {
                if (zip.GetEntry(SetupEntry) is not { } entry)
                {
                    step.Note = "The RTSS download holds no installer. Install it from guru3d.com.";
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

    /// <summary>Whether a download is exactly the pinned build.</summary>
    /// <param name="archive">The downloaded archive.</param>
    /// <returns>Whether its SHA-256 is <see cref="Sha256" />.</returns>
    internal static bool Matches(Stream archive)
    {
        return string.Equals(Convert.ToHexStringLower(SHA256.HashData(archive)), Sha256, StringComparison.Ordinal);
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
        try
        {
            using HttpClient client = new(handler) { Timeout = TimeSpan.FromMinutes(5) };
            using var response = client.Send(new HttpRequestMessage(HttpMethod.Get, Download),
                HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
            {
                problem = $"the server answered {(int)response.StatusCode}";
                return false;
            }

            using var source = response.Content.ReadAsStream();
            using var target = File.Create(destination);
            var total = CopyBody(source, target, stallTimeout);
            SetupLog.Info($"RTSS: downloaded {total} bytes from {Download.Host}.");
            problem = "";
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            SetupLog.Warn($"RTSS: the download failed: {ex.Message}");
            problem = ex is OperationCanceledException ? "it timed out" : "no connection";
            return false;
        }
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
