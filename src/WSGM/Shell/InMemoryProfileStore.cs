using System;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Detached profile persistence shared by previews and tests.</summary>
internal sealed class InMemoryProfileStore(ProfileConfig initial)
{
    private readonly Lock _gate = new();
    private ProfileConfig _stored = ConfigJson.Clone(initial, ConfigJsonContext.Tolerant.ProfileConfig);

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
