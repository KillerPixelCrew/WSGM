using SteamUiToolkit;
using WSGM.Core;
using WSGM.Shell;
using WSGM.Testing;

namespace WSGM.Tests.Shell;

public sealed class PowerButtonCoordinatorTests
{
    public static IEnumerable<object[]> DesktopChoices =>
        Enum.GetValues<PowerButtonShortPress>().Select(value => new object[] { value });

    [Theory]
    [MemberData(nameof(DesktopChoices))]
    public async Task GameModeWaitsForSteamBeforeStandbyRegardlessOfTheDesktopChoice(PowerButtonShortPress choice)
    {
        using var directory = new TemporaryDirectory();
        using var button = Create(directory);
        await button.Completion;
        TaskCompletionSource<SteamSuspendFlowResult> preparation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource resumed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<string> calls = [];
        var services = Services(gameMode: true, calls) with
        {
            RunSteamSuspendFlowAsync = _ =>
            {
                calls.Add("steam");
                return preparation.Task;
            },
            NotifySteamResumedAsync = _ =>
            {
                resumed.TrySetResult();
                return Task.FromResult(true);
            }
        };
        button.ConfigureActions(services);

        var action = button.RunShortPressAsync(services, choice, CancellationToken.None);
        Assert.Equal(["steam"], calls);
        preparation.SetResult(SteamSuspendFlowResult.Completed);
        await action;
        Assert.Equal(["steam", "standby"], calls);

        button.NoteResume();
        await resumed.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task DesktopStillUsesTheConfiguredActionWithoutCallingSteam()
    {
        using var directory = new TemporaryDirectory();
        using var button = Create(directory);
        await button.Completion;
        List<string> calls = [];
        var services = Services(gameMode: false, calls);

        await button.RunShortPressAsync(services, PowerButtonShortPress.Hibernate, CancellationToken.None);
        await button.RunShortPressAsync(services, PowerButtonShortPress.Sleep, CancellationToken.None);
        await button.RunShortPressAsync(services, PowerButtonShortPress.SteamSuspend, CancellationToken.None);
        await button.RunShortPressAsync(services, PowerButtonShortPress.Nothing, CancellationToken.None);

        Assert.Equal(["Hibernate", "standby", "standby"], calls);
    }

    [Fact]
    public async Task StoppingDuringSteamsPreparationDoesNotEnterStandby()
    {
        using var directory = new TemporaryDirectory();
        using var button = Create(directory);
        await button.Completion;
        using var stopped = new CancellationTokenSource();
        List<string> calls = [];
        var services = Services(gameMode: true, calls) with
        {
            RunSteamSuspendFlowAsync = _ =>
            {
                stopped.Cancel();
                return Task.FromResult(SteamSuspendFlowResult.Completed);
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            button.RunShortPressAsync(services, PowerButtonShortPress.Sleep, stopped.Token));

        Assert.Empty(calls);
    }

    private static PowerButtonCoordinator Create(TemporaryDirectory directory)
    {
        return new PowerButtonCoordinator(new ConfigStore(new UserDataContext(directory.Root,
            "WSGM.Tests." + Guid.NewGuid().ToString("N"))), restoreTakeover: _ => { });
    }

    private static PowerButtonServices Services(bool gameMode, List<string> calls)
    {
        return new PowerButtonServices
        {
            InGameMode = () => gameMode,
            OpenSteamPowerMenuAsync = _ => Task.FromResult(true),
            ToggleDesktopPowerMenuAsync = _ => Task.FromResult(true),
            CloseDesktopPowerMenuAsync = _ => Task.FromResult(false),
            RunSteamSuspendFlowAsync = _ => throw new InvalidOperationException("Unexpected Steam suspend"),
            NotifySteamResumedAsync = _ => Task.FromResult(true),
            EnterStandby = () =>
            {
                calls.Add("standby");
                return true;
            },
            RequestPowerAction = action => calls.Add(action.ToString())
        };
    }
}
