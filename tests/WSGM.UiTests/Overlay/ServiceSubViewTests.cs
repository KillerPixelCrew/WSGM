using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using SteamUiToolkit;
using WSGM.Overlay;
using WSGM.UiTests.Infrastructure;

namespace WSGM.UiTests.Overlay;

public sealed class ServiceSubViewTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARefusalFromThePreviousVisitDoesNotAppearWhenReopened(bool reopenBeforeCompletion)
    {
        using var fixture = new UiFixture();
        var view = new ThemesView();
        var window = new Window { Content = view, Width = 600, Height = 400 };
        var answer = new TaskCompletionSource<SteamUiCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            window.Show();
            view.Open();
            var command = view.RunCommandAsync(_ => answer.Task);
            view.Leave();
            if (reopenBeforeCompletion)
            {
                view.Open();
            }

            answer.SetResult(new SteamUiCommandResult(false, "Previous visit refused"));
            await command.WaitAsync(TimeSpan.FromSeconds(5));
            view.Open();
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain(view.GetLogicalDescendants().OfType<TextBlock>(),
                text => text.Text?.Contains("Previous visit refused", StringComparison.Ordinal) == true);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ARefusalWhileStillOnThePageRemainsVisible()
    {
        using var fixture = new UiFixture();
        var view = new ThemesView();
        var window = new Window { Content = view, Width = 600, Height = 400 };
        try
        {
            window.Show();
            view.Open();
            await view.RunCommandAsync(_ => Task.FromResult(new SteamUiCommandResult(false, "Current refusal")));
            Assert.Contains(view.GetLogicalDescendants().OfType<TextBlock>(), text => text.Text == "Current refusal");
        }
        finally
        {
            window.Close();
        }
    }
}
