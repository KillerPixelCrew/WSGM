using WSGM.Shell;

namespace WSGM.Tests.Fakes;

internal sealed class InMemoryHidHideOwnershipStore : IHidHideOwnershipStore
{
    internal HidHideOwnershipLedger? Ledger { get; private set; }

    public Task<HidHideOwnershipLedger?> LoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Ledger);
    }

    public Task SaveAsync(HidHideOwnershipLedger ledger, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Ledger = ledger;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Ledger = null;
        return Task.CompletedTask;
    }
}
