using System;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Detached profile persistence shared by previews and tests.</summary>
/// <param name="initial">Initial profiles, cloned so later caller edits do not change this store.</param>
internal sealed class InMemoryProfileStore(ProfileConfig initial)
{
    private readonly Lock _gate = new();
    private ProfileConfig _stored = ConfigJson.Clone(initial, ConfigJsonContext.Tolerant.ProfileConfig);

    /// <summary>Serializes an in-memory edit and returns detached stored profiles.</summary>
    /// <param name="edit">Runs synchronously under the store lock on a private clone; true commits that clone, false discards it.</param>
    /// <param name="cancellationToken">Checked after acquiring the lock and before the edit; does not interrupt the callback.</param>
    /// <returns>A completed task containing a fresh clone of the stored profiles, including after a no-op edit.</returns>
    internal Task<ProfileConfig> MutateAsync(Func<ProfileConfig, bool> edit, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = ConfigJson.Clone(_stored, ConfigJsonContext.Tolerant.ProfileConfig);
            if (edit(candidate))
            {
                _stored = candidate;
            }

            return Task.FromResult(ConfigJson.Clone(_stored, ConfigJsonContext.Tolerant.ProfileConfig));
        }
    }
}
