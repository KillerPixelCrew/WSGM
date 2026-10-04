using WSGM.Core;
using WSGM.Testing;

namespace WSGM.Tests.Fakes;

/// <summary>A configuration store over a temporary root and a private mutex, deleted on dispose.</summary>
/// <remarks>Tests never open the production <c>Local\WSGM.Config</c> mutex or the real user data root.</remarks>
internal sealed class TemporaryConfigStore : IDisposable
{
    private readonly TemporaryDirectory _directory = new();

    internal TemporaryConfigStore()
    {
        Context = new UserDataContext(_directory.Root, @"Local\WSGM.Tests.Config." + Guid.NewGuid().ToString("N"));
        Store = new ConfigStore(Context);
    }

    internal UserDataContext Context { get; }

    internal ConfigStore Store { get; }

    public void Dispose()
    {
        _directory.Dispose();
    }
}
