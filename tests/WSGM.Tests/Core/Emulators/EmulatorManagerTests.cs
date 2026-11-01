using System.Text.Json;
using WSGM.Core;
using WSGM.Testing;

namespace WSGM.Tests.Core;

public sealed class EmulatorManagerTests
{
    [Fact]
    public async Task SetPreferredAsync_WaitsForLaunchAdmissionBeforeChangingTheReceipt()
    {
        using var temporary = new TemporaryDirectory();
        var store = new EmulatorStore
        {
            SystemPreferences = [new EmulatorSystemPreference { SystemId = "genesis", InstallationId = "old" }]
        };
        File.WriteAllText(EmulatorStorage.StorePath(temporary.Root),
            JsonSerializer.Serialize(store, EmulatorStorage.JsonOptions));
        using var manager = new EmulatorManager(new UserDataContext(temporary.Root, "unused-test-config"));
        await manager.Initialization.WaitAsync(AsyncConditions.TimeLimit);
        using var release = new ManualResetEventSlim();
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = Task.Run(() =>
        {
            using var lease = EmulatorStorage.AcquireGate(temporary.Root, CancellationToken.None);
            held.SetResult();
            release.Wait();
        });
        await held.Task.WaitAsync(AsyncConditions.TimeLimit);

        Task? preference = null;
        try
        {
            preference = manager.SetPreferredAsync("megadrive", "", "", CancellationToken.None);
            Assert.NotSame(preference, await Task.WhenAny(preference, Task.Delay(TimeSpan.FromMilliseconds(100))));
            Assert.Single(EmulatorStorage.ReadStore(temporary.Root).SystemPreferences);
        }
        finally
        {
            release.Set();
            await holder.WaitAsync(AsyncConditions.TimeLimit);
            if (preference is not null)
            {
                await preference.WaitAsync(AsyncConditions.TimeLimit);
            }
        }

        Assert.Empty(EmulatorStorage.ReadStore(temporary.Root).SystemPreferences);
    }
}
