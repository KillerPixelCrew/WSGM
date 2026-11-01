using WSGM.Shell;

namespace WSGM.Tests.Shell;

public sealed class LaunchedWindowFocusTests
{
    [Fact]
    public async Task LaunchedWindowAsksSteamToRaiseTheGameBeforeFocusingIt()
    {
        List<string> steps = [];
        var gameReturn = new GameWindowReturn((pid, _) =>
        {
            steps.Add($"raise {pid}");
            return Task.FromResult(true);
        }, _ => 42, _ => false, hwnd =>
        {
            steps.Add($"focus {hwnd}");
            return true;
        }, _ => { });

        Assert.True(await LaunchedWindowFocus.RaiseAsync(gameReturn, 30, 42, 10));
        Assert.Equal(["raise 42", "focus 30"], steps);
    }

    [Fact]
    public async Task LaunchedWindowWithoutSteamOverlayIsStillFocused()
    {
        nint focused = 0;
        var gameReturn = new GameWindowReturn((_, _) => Task.FromResult(false), _ => 42, _ => false, hwnd =>
        {
            focused = hwnd;
            return true;
        }, _ => { });

        Assert.True(await LaunchedWindowFocus.RaiseAsync(gameReturn, 30, 42, 0));
        Assert.Equal(30, focused);
    }
}
