using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>Uploads masked log files into one new filebin.net bin and answers with the bin's link.</summary>
/// <remarks>
///     <para>
///         filebin.net stores the raw body of <c>POST https://filebin.net/&lt;bin&gt;/&lt;file name&gt;</c> as that
///         file, creating the bin on its first file, and answers 201 with JSON describing the bin and the stored
///         file. A bin expires on its own after about a week, and <c>DELETE https://filebin.net/&lt;bin&gt;</c>
///         removes it sooner. Every upload uses a new bin whose name carries 128 random bits, so its link cannot be
///         guessed, and puts each log file into it whole: no size is assumed, and a file filebin refuses fails
///         with filebin's own explanation.
///     </para>
///     <para>
///         The files are read once, with shared access while WSGM keeps writing them, and masked by
///         <see cref="LogMask" /> before the first request. An upload that does not finish, cancelled or failed,
///         deletes the bin, so a partial upload never stays readable. Files go up one at a time. The log records
///         one line for the outcome, with the file count and bytes, never the text or the link.
///     </para>
/// </remarks>
internal sealed class LogUploader
{
    /// <summary>Where bins live.</summary>
    internal const string Service = "https://filebin.net/";

    // filebin answers a stored file with a short JSON description, and a refusal with a short explanation.
    private const int MaxAnswerBytes = 64 * 1024;

    // Removing the bin runs after a cancellation too, so it has its own bound rather than the caller's token.
    private static readonly TimeSpan RemoveTimeout = TimeSpan.FromSeconds(20);

    private readonly string _bin;
    private readonly DownloadClient _client;
    private readonly LogMask _mask;

    /// <summary>Creates an uploader for one new bin.</summary>
    /// <param name="client">The HTTPS client, owned by the caller.</param>
    /// <param name="mask">What is replaced before the text leaves the machine.</param>
    /// <param name="bin">The bin's name; null for a new unguessable one.</param>
    internal LogUploader(DownloadClient client, LogMask mask, string? bin = null)
    {
        _client = client;
        _mask = mask;
        _bin = bin ?? NewBin();
    }

    /// <summary>Gets the bin's link, which is what the user shares.</summary>
    internal string Link => Service + _bin;

    /// <summary>A new bin name: <c>wsgm-</c> and 128 random bits in hexadecimal.</summary>
    /// <returns>The name.</returns>
    internal static string NewBin()
    {
        return "wsgm-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
    }

    /// <summary>Lists the log files that exist: the live log, then its archive of the previous session.</summary>
    /// <param name="live">The live log, <c>wsgm.log</c>.</param>
    /// <param name="archive">The rotated archive, <c>wsgm.old.log</c>.</param>
    /// <returns>The files present, with their current sizes.</returns>
    internal static IReadOnlyList<LogUploadFile> Find(string live, string archive)
    {
        List<LogUploadFile> files = [];
        foreach (var (path, previous) in new[] { (live, false), (archive, true) })
        {
            FileInfo info = new(path);
            if (info.Exists)
            {
                files.Add(new LogUploadFile(info.Name, info.FullName, info.Length, previous));
            }
        }

        return files;
    }

    /// <summary>Reads, masks and uploads the files whole into the bin.</summary>
    /// <param name="files">The files, uploaded in this order.</param>
    /// <param name="progress">Receives the file being sent.</param>
    /// <param name="cancellationToken">Cancels the upload; the bin is then removed.</param>
    /// <returns>The bin's link.</returns>
    /// <exception cref="LogUploadException">The upload was cancelled or failed; the message says why.</exception>
    internal async Task<string> UploadAsync(IReadOnlyList<LogUploadFile> files, IProgress<LogUploadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var sent = 0;
        long bytes = 0;
        try
        {
            // Everything is read and masked before the first request, so a file that cannot be read sends nothing.
            List<(LogUploadFile File, string Text)> texts = [];
            foreach (var file in files)
            {
                texts.Add((file, _mask.Apply(Read(file))));
            }

            foreach (var (file, text) in texts)
            {
                progress?.Report(new LogUploadProgress(file.Name));
                // Counted before the request: a refused or interrupted one may still have created the bin.
                sent++;
                bytes += await PostAsync(file.Name, text, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            var note = await RemoveAsync(sent).ConfigureAwait(false);
            Log.Info($"Log upload: cancelled after {sent} of {files.Count} file(s).{note}");
            throw new LogUploadException("Upload cancelled." + note, true, exception);
        }
        catch (LogUploadException exception)
        {
            var note = await RemoveAsync(sent).ConfigureAwait(false);
            Log.Warn($"Log upload failed at file {sent} of {files.Count}: {exception.Message}{note}");
            throw new LogUploadException(exception.Message + note, false, exception.InnerException ?? exception);
        }

        Log.Info($"Log upload: {files.Count} file(s), {bytes} bytes sent to filebin.net.");
        return Link;
    }

    private static string Read(LogUploadFile file)
    {
        try
        {
            // WSGM keeps appending and may rotate the file while it is read: share both.
            using FileStream stream = new(file.Path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using StreamReader reader = new(stream, Encoding.UTF8, true);
            return reader.ReadToEnd();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new LogUploadException($"Could not read {file.Name}: {exception.Message}", inner: exception);
        }
    }

    // Answers the bytes sent, once filebin stored them all.
    private async Task<long> PostAsync(string name, string text, CancellationToken cancellationToken)
    {
        var size = Encoding.UTF8.GetByteCount(text);
        try
        {
            using var response = await _client
                .PostTextAsync($"{Link}/{Uri.EscapeDataString(name)}", text, cancellationToken)
                .ConfigureAwait(false);
            await using var answer = await BoundedHttp.ReadAsync(response.Content, MaxAnswerBytes,
                () => new InvalidDataException("filebin.net answered with more than a description of the file."),
                cancellationToken).ConfigureAwait(false);
            var body = Encoding.UTF8.GetString(answer.ToArray());
            if (!response.IsSuccessStatusCode)
            {
                throw new LogUploadException(Refusal(name, response.StatusCode, body));
            }

            // A stored size that differs means the link would show a cut-short log; an answer that names no size
            // is taken at its 201.
            if (StoredBytes(body) is { } stored && stored != size)
            {
                throw new LogUploadException($"filebin.net kept {stored} of the {size} bytes of {name}.");
            }

            return size;
        }
        catch (HttpRequestException exception)
        {
            throw new LogUploadException($"Could not reach filebin.net: {exception.Message}", inner: exception);
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or InvalidDataException)
        {
            throw new LogUploadException(exception.Message, inner: exception);
        }
    }

    private static string Refusal(string name, HttpStatusCode status, string body)
    {
        if (status == HttpStatusCode.TooManyRequests)
        {
            return "filebin.net is limiting uploads right now (HTTP 429). Wait a few minutes and try again.";
        }

        // filebin explains a refusal, such as a file that is too large, in a line of its own; an HTML error page
        // explains nothing the status does not.
        var reason = body.Trim();
        var line = reason.IndexOfAny(['\r', '\n']);
        reason = line < 0 ? reason : reason[..line];
        return $"filebin.net refused {name} with HTTP {(int)status} ({status})"
               + (reason.Length > 0 && !reason.StartsWith('<') ? $": {reason.TrimEnd('.')}." : ".");
    }

    // The stored file's size from filebin's answer, or null when the answer does not say.
    private static long? StoredBytes(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("file", out var file)
                   && file.ValueKind == JsonValueKind.Object
                   && file.TryGetProperty("bytes", out var bytes)
                   && bytes.TryGetInt64(out var value)
                ? value
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<string> RemoveAsync(int sent)
    {
        if (sent == 0)
        {
            return "";
        }

        using CancellationTokenSource timeout = new(RemoveTimeout);
        try
        {
            await _client.DeleteAsync(Link, timeout.Token).ConfigureAwait(false);
            return " The partial upload was removed.";
        }
        catch (Exception exception) when (exception is HttpRequestException or TimeoutException
                                              or OperationCanceledException or InvalidDataException)
        {
            Log.Warn($"Log upload: could not remove the partial upload: {exception.Message}");
            return " The partial upload could not be removed; filebin.net deletes it in about a week.";
        }
    }
}
