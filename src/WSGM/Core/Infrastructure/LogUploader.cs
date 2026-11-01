using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>Uploads masked log files to paste.rs and answers with their links, in order.</summary>
/// <remarks>
///     <para>
///         paste.rs takes the raw body of a POST to <c>https://paste.rs/</c> and answers with the paste's link:
///         201 when it kept everything, 206 when the body reached its size limit and it kept only the start. It
///         publishes no number for that limit, so none is assumed here. A file goes up whole; when paste.rs
///         answers 206, the uploader reads the cut-short paste back, whose length is the limit, deletes it, and
///         sends the file again in pieces below that size, split on line boundaries and in order. A piece that
///         is still cut short halves the size, so the loop always ends. Every piece starts with a header line
///         naming its file and number, and nothing is ever dropped: a log of any size goes up whole.
///     </para>
///     <para>
///         The files are read once, with shared access while WSGM keeps writing them, and masked by
///         <see cref="LogMask" /> before the first request. An upload that does not finish, cancelled or
///         failed, deletes the pieces it already sent, so a partial set of links never stays readable. Pieces
///         go up one at a time, which is all the pacing paste.rs's rate limit needs from one user action. The
///         log records one line for the outcome, with the piece count and bytes, never the text or a link.
///     </para>
/// </remarks>
internal sealed class LogUploader
{
    /// <summary>Where pastes are created.</summary>
    internal const string Endpoint = "https://paste.rs/";

    // paste.rs answers a paste with its link; anything longer than this is not one.
    private const int MaxAnswerBytes = 4096;

    // One UTF-8 character takes at most four bytes, so a piece this size always moves forward.
    private const int MinPieceBytes = 4;

    // Removing pieces runs after a cancellation too, so it has its own bound rather than the caller's token.
    private static readonly TimeSpan RemoveTimeout = TimeSpan.FromSeconds(20);

    private readonly DownloadClient _client;
    private readonly LogMask _mask;
    private int _cutShort;

    // The most bytes one paste body may have, learned from the last 206; null until paste.rs cut one short.
    private long? _limit;

    /// <summary>Creates an uploader.</summary>
    /// <param name="client">The HTTPS client, owned by the caller.</param>
    /// <param name="mask">What is replaced before the text leaves the machine.</param>
    internal LogUploader(DownloadClient client, LogMask mask)
    {
        _client = client;
        _mask = mask;
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

    /// <summary>Reads, masks and uploads the files, each whole or in as many pieces as paste.rs needs.</summary>
    /// <param name="files">The files, in the order their links are listed.</param>
    /// <param name="progress">Receives the piece being sent.</param>
    /// <param name="cancellationToken">Cancels the upload; pieces already sent are then removed.</param>
    /// <returns>Every paste's link, file by file and piece by piece.</returns>
    /// <exception cref="LogUploadException">The upload was cancelled or failed; the message says why.</exception>
    internal async Task<IReadOnlyList<LogUploadLink>> UploadAsync(IReadOnlyList<LogUploadFile> files,
        IProgress<LogUploadProgress>? progress, CancellationToken cancellationToken)
    {
        List<LogUploadLink> links = [];
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
                var start = 0;
                var piece = 1;
                while (true)
                {
                    var header = Header(file, piece);
                    var end = text.Length;
                    if (_limit is { } limit)
                    {
                        var room = limit - Encoding.UTF8.GetByteCount(header);
                        if (room < MinPieceBytes)
                        {
                            throw new LogUploadException(
                                $"paste.rs cut even a {limit + 1}-byte piece short, too small to upload the log in.");
                        }

                        end = Take(text, start, room);
                    }

                    var body = header + text[start..end];
                    var size = Encoding.UTF8.GetByteCount(body);
                    progress?.Report(new LogUploadProgress(file.Name, piece));
                    var (url, partial) = await PostAsync(body, cancellationToken).ConfigureAwait(false);
                    if (partial)
                    {
                        _limit = await LearnLimitAsync(url, size, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    links.Add(new LogUploadLink(file.Name, piece, url));
                    bytes += size;
                    start = end;
                    piece++;
                    if (start >= text.Length)
                    {
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            var note = await RemoveAsync(links).ConfigureAwait(false);
            Log.Info($"Log upload: cancelled after {links.Count} part(s).{note}");
            throw new LogUploadException("Upload cancelled." + note, true, exception);
        }
        catch (LogUploadException exception)
        {
            var note = await RemoveAsync(links).ConfigureAwait(false);
            Log.Warn($"Log upload failed after {links.Count} part(s): {exception.Message}{note}");
            throw new LogUploadException(exception.Message + note, false, exception.InnerException ?? exception);
        }

        Log.Info($"Log upload: {links.Count} part(s), {bytes} bytes sent to paste.rs"
                 + (_cutShort == 0
                     ? "."
                     : $"; {_cutShort} paste(s) were cut short, so pieces stay below {_limit + 1} bytes."));
        return links;
    }

    /// <summary>Finds where the next piece ends: whole lines that fit, or part of one line too long to fit.</summary>
    /// <param name="text">The whole text.</param>
    /// <param name="start">Where the piece starts.</param>
    /// <param name="maxBytes">The most UTF-8 bytes the piece may have; at least four.</param>
    /// <returns>The index after the piece's last character, always past <paramref name="start" /> when text remains.</returns>
    internal static int Take(string text, int start, long maxBytes)
    {
        long used = 0;
        var end = start;
        while (end < text.Length)
        {
            var newline = text.IndexOf('\n', end);
            var lineEnd = newline < 0 ? text.Length : newline + 1;
            var lineBytes = Encoding.UTF8.GetByteCount(text.AsSpan(end, lineEnd - end));
            if (used + lineBytes > maxBytes)
            {
                break;
            }

            used += lineBytes;
            end = lineEnd;
        }

        if (end > start)
        {
            return end;
        }

        // One line is larger than a whole piece: it continues in the next one, split between characters.
        while (end < text.Length)
        {
            var width = char.IsHighSurrogate(text[end]) && end + 1 < text.Length && char.IsLowSurrogate(text[end + 1])
                ? 2
                : 1;
            var characterBytes = Encoding.UTF8.GetByteCount(text.AsSpan(end, width));
            if (used + characterBytes > maxBytes)
            {
                break;
            }

            used += characterBytes;
            end += width;
        }

        return end;
    }

    private static string Header(LogUploadFile file, int piece)
    {
        return $"== WSGM {file.Name}{(file.Previous ? " (previous session)" : "")}, part {piece} =="
               + Environment.NewLine;
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

    private Task<(string Url, bool Partial)> PostAsync(string body, CancellationToken cancellationToken)
    {
        return RequestAsync(async () =>
        {
            using var response = await _client.PostTextAsync(Endpoint, body, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is not (HttpStatusCode.Created or HttpStatusCode.PartialContent))
            {
                throw new LogUploadException($"paste.rs answered HTTP {(int)response.StatusCode} "
                                             + $"({response.StatusCode}) instead of creating the paste.");
            }

            await using var answer = await BoundedHttp.ReadAsync(response.Content, MaxAnswerBytes, NotALink,
                cancellationToken).ConfigureAwait(false);
            var link = Encoding.UTF8.GetString(answer.ToArray()).Trim();
            if (JsonRead.HttpsUrl(link).Length == 0)
            {
                throw NotALink();
            }

            return (link, response.StatusCode == HttpStatusCode.PartialContent);
        });
    }

    private static InvalidDataException NotALink()
    {
        return new InvalidDataException("paste.rs answered with something other than a link.");
    }

    // A 206 paste holds exactly as many bytes as paste.rs keeps, and a body that reaches that size is cut short,
    // so the next pieces stay one byte below it. When it cannot be read back, half the size still converges.
    private async Task<long> LearnLimitAsync(string url, long sent, CancellationToken cancellationToken)
    {
        _cutShort++;
        long? kept = null;
        try
        {
            using var response = await _client.OpenAsync(url, cancellationToken).ConfigureAwait(false);
            await using var copy = await BoundedHttp.ReadAsync(response.Content, (int)Math.Min(sent, int.MaxValue),
                () => new InvalidDataException("The cut-short paste is longer than what was sent."),
                cancellationToken).ConfigureAwait(false);
            kept = copy.Length;
        }
        catch (Exception exception) when (exception is HttpRequestException or TimeoutException or IOException
                                              or InvalidDataException)
        {
            Log.Warn($"Log upload: could not read back a cut-short paste, halving the piece size: {exception.Message}");
        }
        finally
        {
            using CancellationTokenSource timeout = new(RemoveTimeout);
            await DeleteAsync(url, timeout.Token).ConfigureAwait(false);
        }

        return kept is > 0 ? kept.Value - 1 : sent / 2;
    }

    private async Task<string> RemoveAsync(IReadOnlyList<LogUploadLink> links)
    {
        if (links.Count == 0)
        {
            return "";
        }

        using CancellationTokenSource timeout = new(RemoveTimeout);
        var failed = 0;
        foreach (var link in links)
        {
            if (!await DeleteAsync(link.Url, timeout.Token).ConfigureAwait(false))
            {
                failed++;
            }
        }

        return failed == 0
            ? $" The {links.Count} part(s) already uploaded were removed."
            : $" {failed} of the {links.Count} part(s) already uploaded could not be removed from paste.rs.";
    }

    private async Task<bool> DeleteAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            await _client.DeleteAsync(url, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is HttpRequestException or TimeoutException
                                              or OperationCanceledException or InvalidDataException)
        {
            Log.Warn($"Log upload: could not remove a paste: {exception.Message}");
            return false;
        }
    }

    private static async Task<T> RequestAsync<T>(Func<Task<T>> request)
    {
        try
        {
            return await request().ConfigureAwait(false);
        }
        catch (HttpRequestException exception) when (exception.StatusCode is { } status)
        {
            throw new LogUploadException(status == HttpStatusCode.TooManyRequests
                ? "paste.rs is limiting uploads right now (HTTP 429). Wait a few minutes and try again."
                : $"paste.rs refused the upload with HTTP {(int)status} ({status}).", inner: exception);
        }
        catch (HttpRequestException exception)
        {
            throw new LogUploadException($"Could not reach paste.rs: {exception.Message}", inner: exception);
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or InvalidDataException)
        {
            throw new LogUploadException(exception.Message, inner: exception);
        }
    }
}
