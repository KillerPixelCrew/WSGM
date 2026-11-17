using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>The images a save will write, downloaded before it reaches the titles that use them.</summary>
/// <remarks>
///     <para>
///         A save writes its titles one after another. Each title's images used to start downloading
///         only once its shortcut existed, so Steam's writes and the downloads took turns, and a free
///         Screenscraper account serves one request at a time. Started here, a later title's images
///         queue behind the provider's own gate while Steam writes the current title, so that gate
///         never waits for Steam.
///     </para>
///     <para>
///         An image is handed over once and then forgotten, and at most <see cref="Capacity" /> are
///         held, the oldest dropped first, so a save that stops early leaves a bounded amount behind.
///         A download a stopped save cancelled is started again when it is asked for.
///     </para>
/// </remarks>
/// <param name="download">Downloads one image through the provider that serves it.</param>
internal sealed class ArtworkPrefetch(Func<string, CancellationToken, Task<byte[]>> download)
{
    /// <summary>How many images are held at once.</summary>
    internal const int Capacity = 32;

    private readonly LinkedList<string> _order = new();
    private readonly Dictionary<string, (Task<byte[]> Download, LinkedListNode<string> Node)> _started =
        new(StringComparer.Ordinal);

    private readonly Lock _sync = new();

    /// <summary>Starts downloading the images that are not already on their way.</summary>
    /// <param name="urls">The images' addresses; an empty one clears a slot and is skipped.</param>
    /// <param name="cancellationToken">The save the downloads belong to.</param>
    internal void Start(IEnumerable<string> urls, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(urls);
        lock (_sync)
        {
            foreach (var url in urls)
            {
                if (url.Length == 0 || cancellationToken.IsCancellationRequested)
                {
                    continue;
                }

                if (_started.TryGetValue(url, out var existing))
                {
                    if (!existing.Download.IsCanceled)
                    {
                        continue;
                    }

                    _order.Remove(existing.Node);
                }

                _started[url] = (Observed(download(url, cancellationToken)), _order.AddLast(url));
                while (_started.Count > Capacity && _order.First is { } oldest)
                {
                    _order.RemoveFirst();
                    _started.Remove(oldest.Value);
                }
            }
        }
    }

    /// <summary>Hands over one image, downloading it now when nothing started it.</summary>
    /// <param name="url">The image's address.</param>
    /// <param name="cancellationToken">Cancels the wait, and a download this call starts.</param>
    /// <returns>The image's bytes.</returns>
    /// <exception cref="ArtworkProviderException">The image could not be downloaded.</exception>
    internal async Task<byte[]> TakeAsync(string url, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        Task<byte[]>? started = null;
        lock (_sync)
        {
            if (_started.Remove(url, out var entry))
            {
                _order.Remove(entry.Node);
                started = entry.Download;
            }
        }

        if (started is not null)
        {
            try
            {
                return await started.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Started for a save that has since stopped: this one downloads it again.
            }
        }

        return await download(url, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Marks a download's failure as seen, so one nobody takes does not surface as unobserved.</summary>
    private static Task<byte[]> Observed(Task<byte[]> task)
    {
        _ = task.ContinueWith(static completed => _ = completed.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return task;
    }
}
