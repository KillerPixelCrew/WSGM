using System;

namespace WSGM.Core;

/// <summary>One thread-owned writer; file promotion and boot projection may share its scope.</summary>
/// <remarks>
///     This scope serializes related operations but does not roll back files already published.
///     Save is explicit, and both Save and Dispose must run on the thread that acquired the scope.
/// </remarks>
public sealed class ConfigTransaction : IDisposable
{
    private readonly ConfigMutexLease _held;
    private readonly ConfigStore _store;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private bool _disposed;
    private (byte[] Bytes, int Version)? _older;

    /// <summary>Transfers an acquired writer lease and strict read into one thread-bound transaction.</summary>
    /// <param name="store">Persistence owner used for explicit publication.</param>
    /// <param name="held">Acquired mutex lease transferred to the transaction.</param>
    /// <param name="read">Validated loaded or absent configuration result.</param>
    /// <param name="older">Original older-schema bytes and version to preserve on first save, or null.</param>
    internal ConfigTransaction(ConfigStore store, ConfigMutexLease held, ConfigReadResult read,
        (byte[] Bytes, int Version)? older)
    {
        _store = store;
        _held = held;
        Read = read;
        _older = older;
    }

    /// <summary>The validated read, updated to refer to a saved replacement.</summary>
    public ConfigReadResult Read { get; private set; }

    /// <summary>The fresh configuration owned by this writer.</summary>
    public AppConfig Config => Read.RequireConfig();

    /// <summary>Releases the writer without implicitly saving.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        EnsureOwner();
        _disposed = true;
        _store.ReleaseWriterThread();
        _held.Dispose();
    }

    /// <summary>Publishes configuration durably while retaining the writer scope.</summary>
    /// <remarks>
    ///     Preserves the first older-schema document before replacing it. A successful save updates
    ///     only the configuration file; callers own asset promotion and boot-manifest publication.
    /// </remarks>
    /// <param name="replacement">A merged replacement, or null to save the owned document.</param>
    /// <exception cref="ConfigUnavailableException">The owner thread, schema backup or publication is unavailable.</exception>
    /// <exception cref="ObjectDisposedException">The writer scope has already been disposed.</exception>
    public void Save(AppConfig? replacement = null)
    {
        EnsureOwner();
        if (replacement is not null)
        {
            Read = Read with { Config = replacement };
        }

        if (_older is { } older)
        {
            _store.PreserveOlderSchema(older.Bytes, older.Version);
            _older = null;
        }

        _store.WriteHeld(Config);
    }

    private void EnsureOwner()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_thread != Environment.CurrentManagedThreadId)
        {
            throw new ConfigUnavailableException("The writer must stay on its acquiring thread.");
        }
    }
}
