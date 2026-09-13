using System.Reflection;
using WSGM.Core;

namespace WSGM.Tests;

public sealed class SteamInputClaimTests
{
    [Fact]
    public async Task HandoffClaimDoesNotWaitForTheNativeOperationLock()
    {
        // Hold precisely the lock a pending pipe acquire holds, without opening a native client.
        object nativeLock = typeof(SteamInputBlocker).GetField("Sync", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ManualResetEventSlim release = new();
        Task nativeOperation = Task.Run(() =>
        {
            lock (nativeLock)
            {
                entered.SetResult();
                Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            }
        });
        const string owner = "test-settings-handoff";
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Run(() => SteamInputBlocker.ClaimFor(owner)).WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            release.Set();
            await nativeOperation;
            SteamInputBlocker.ReleaseFor(owner, "test cleanup");
        }
    }
}
