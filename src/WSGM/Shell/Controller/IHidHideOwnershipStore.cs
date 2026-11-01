using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Shell;

internal interface IHidHideOwnershipStore
{
    Task<HidHideOwnershipLedger?> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(HidHideOwnershipLedger ledger, CancellationToken cancellationToken);

    Task DeleteAsync(CancellationToken cancellationToken);
}
