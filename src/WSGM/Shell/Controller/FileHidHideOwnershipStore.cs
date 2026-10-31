using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Shell;

internal sealed class FileHidHideOwnershipStore : IHidHideOwnershipStore
{
    private readonly string _path;

    internal FileHidHideOwnershipStore(string path)
    {
        _path = Path.GetFullPath(path);
    }

    public async Task<HidHideOwnershipLedger?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        await using FileStream stream = new(
            _path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync(
            stream,
            HidHideOwnershipJsonContext.Default.HidHideOwnershipLedger,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveAsync(
        HidHideOwnershipLedger ledger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        var directory = Path.GetDirectoryName(_path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("The HidHide ledger path has no parent directory.");
        }

        Directory.CreateDirectory(directory);
        await AtomicFile.WriteAsync(
            _path,
            (stream, token) => JsonSerializer.SerializeAsync(
                stream,
                ledger,
                HidHideOwnershipJsonContext.Default.HidHideOwnershipLedger,
                token),
            true,
            cancellationToken).ConfigureAwait(false);
    }

    public Task DeleteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(_path);
        return Task.CompletedTask;
    }
}
