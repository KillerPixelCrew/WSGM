using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using WSGM.Shell;

namespace WSGM.UiTests;

public sealed class SessionActivationTests
{
    [AvaloniaFact]
    public async Task ActivationQueuedDuringStartupReachesTheUiOwner()
    {
        using UiFixture fixture = new();
        string name = @"Local\WSGM.Test.Activate." + Guid.NewGuid().ToString("N");
        using EventWaitHandle signal = new(false, EventResetMode.AutoReset, name);
        signal.Set();
        TaskCompletionSource activated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using SessionActivation owner = new(() =>
        {
            Assert.True(Dispatcher.UIThread.CheckAccess());
            activated.TrySetResult();
        }, name);
        await activated.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
