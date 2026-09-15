namespace WSGM.Tests;

/// <summary>Polls for a condition a background Steam UI task makes true.</summary>
internal static class AsyncConditions
{
    internal static async Task WaitForAsync(Func<bool> condition, int timeoutSeconds = 2)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        while (!condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }
    }
}
