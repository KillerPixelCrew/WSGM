using System.IO.Pipes;

namespace WSGM.Explorerfy;

/// <summary>Holds the "bring Explorer up" lease against the running WSGM shell for
/// as long as this wrapper lives. The pipe connection IS the lease: WSGM drops to
/// desktop mode on connect and returns to game mode when the connection closes —
/// whether this wrapper exits cleanly after the game or Steam kills it (a broken
/// pipe releases the lease either way).
/// <para>Best-effort: if WSGM is not running as the shell (no pipe to reach), the
/// game still launches — it just runs in whatever desktop/session state exists.</para></summary>
internal sealed class ExplorerLease : IAsyncDisposable
{
    private const byte Acquire = 1;
    private readonly NamedPipeClientStream? _pipe;

    private ExplorerLease(NamedPipeClientStream? pipe) => _pipe = pipe;

    internal static async Task<ExplorerLease> AcquireAsync(string pipeName, TimeSpan timeout)
    {
        NamedPipeClientStream? pipe = null;
        try
        {
            pipe = new NamedPipeClientStream(
                ".", pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var cts = new CancellationTokenSource(timeout);
            await pipe.ConnectAsync(cts.Token);

            await pipe.WriteAsync(new byte[] { Acquire }, cts.Token);
            await pipe.FlushAsync(cts.Token);

            // WSGM confirms once Explorer is up (or reports it could not enter
            // desktop mode). Either way we launch — the ack is only diagnostic.
            var ack = new byte[1];
            await pipe.ReadExactlyAsync(ack, cts.Token);
            ExplorerfyLog.Info(ack[0] == 1
                ? "WSGM confirmed desktop mode (Explorer up)."
                : "WSGM could not enter desktop mode; launching anyway.");
            return new ExplorerLease(pipe);
        }
        catch (Exception ex)
        {
            ExplorerfyLog.Info(
                $"WSGM shell not reachable ({ex.GetType().Name}); launching without Explorer coordination.");
            pipe?.Dispose();
            return new ExplorerLease(null);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_pipe is null)
        {
            return;
        }
        try
        {
            // Closing the pipe is the release signal — WSGM returns to game mode.
            await _pipe.DisposeAsync();
        }
        catch
        {
            // Releasing is best-effort; a already-broken pipe has released already.
        }
    }
}
