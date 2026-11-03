using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WSGM.Core;
using WSGM.Settings;
using WSGM.Testing;
using WSGM.UiTests.Infrastructure;

namespace WSGM.UiTests.Settings;

/// <summary>The Upload log row driven by keyboard: confirm, upload, then the link with focus on Copy again.</summary>
public sealed class SettingsLogUploadTests
{
    private const string Link = "https://filebin.net/wsgm-0123456789abcdef0123456789abcdef";

    [AvaloniaFact]
    public async Task TheRowWalksFromConfirmationToTheCopiedLinkWithFocusOnTheNextAction()
    {
        using UiFixture fixture = new();
        fixture.LogFiles = [new LogUploadFile("wsgm.log", @"X:\wsgm.log", 2048, false)];
        TaskCompletionSource<string> answer = new();
        fixture.UploadLogs = (_, _) => answer.Task;
        var window = fixture.Settings();
        UiFixture.Click(window, UiFixture.Tab(window, 9));
        var model = Assert.IsType<SettingsViewModel>(window.DataContext);
        List<string> copied = [];
        model.Clipboard = text =>
        {
            copied.Add(text);
            return Task.CompletedTask;
        };

        Button Named(string name)
        {
            return window.GetVisualDescendants().OfType<Button>().Single(button => button.Name == name);
        }

        Named("UploadLogButton").Focus(NavigationMethod.Directional);
        await Press(window, () => model.LogUploadConfirmPending);

        Assert.True(Named("ConfirmLogUploadButton").IsEffectivelyVisible);
        Assert.True(Named("CancelLogUploadButton").IsFocused);

        Named("ConfirmLogUploadButton").Focus(NavigationMethod.Directional);
        UiFixture.Click(window, Named("ConfirmLogUploadButton"));
        Dispatcher.UIThread.RunJobs();
        Assert.True(model.LogUploadBusy);
        Assert.True(Named("CancelLogUploadButton").IsFocused);
        Assert.False(Named("UploadLogButton").IsEffectivelyEnabled);

        answer.SetResult(Link);
        await AsyncConditions.WaitForAsync(() => !model.LogUploadBusy && model.HasLogUploadLink);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal([Link], copied);
        Assert.Equal("Uploaded. Copied to clipboard. Paste the link into your report.", model.LogUploadStatusText);
        Assert.True(Named("CopyLogLinkButton").IsFocused);
        Assert.False(Named("CancelLogUploadButton").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task ClosingSettingsStopsARunningUpload()
    {
        using UiFixture fixture = new();
        fixture.LogFiles = [new LogUploadFile("wsgm.log", @"X:\wsgm.log", 2048, false)];
        CancellationToken observed = default;
        fixture.UploadLogs = (_, cancellationToken) =>
        {
            observed = cancellationToken;
            return Task.Delay(Timeout.Infinite, cancellationToken)
                .ContinueWith<string>(
                    _ => throw new LogUploadException("Upload cancelled.", true),
                    TaskScheduler.Default);
        };
        var window = fixture.Settings();
        UiFixture.Click(window, UiFixture.Tab(window, 9));
        var model = Assert.IsType<SettingsViewModel>(window.DataContext);
        await model.PrepareLogUploadAsync();
        var upload = model.UploadLogAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.True(model.LogUploadBusy);

        window.Close();

        Assert.True(observed.IsCancellationRequested);
        await upload;
        Assert.Equal("Upload cancelled.", model.LogUploadStatusText);
    }

    private static async Task Press(Window window, Func<bool> done)
    {
        UiFixture.Key(window, Key.Enter);
        await AsyncConditions.WaitForAsync(done);
        Dispatcher.UIThread.RunJobs();
    }
}
