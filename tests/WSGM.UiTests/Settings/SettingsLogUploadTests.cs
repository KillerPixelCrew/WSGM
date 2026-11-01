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

/// <summary>The Upload log row driven by keyboard: confirm, upload, then the links with focus on Copy again.</summary>
public sealed class SettingsLogUploadTests
{
    [AvaloniaFact]
    public async Task TheRowWalksFromConfirmationToCopiedLinksWithFocusOnTheNextAction()
    {
        using UiFixture fixture = new();
        fixture.LogFiles = [new LogUploadFile("wsgm.log", @"X:\wsgm.log", 2048, false)];
        TaskCompletionSource<IReadOnlyList<LogUploadLink>> answer = new();
        fixture.UploadLogs = (_, _) => answer.Task;
        var window = fixture.Settings();
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

        answer.SetResult([new LogUploadLink("wsgm.log", 1, "https://paste.rs/Xk2")]);
        await AsyncConditions.WaitForAsync(() => !model.LogUploadBusy && model.HasLogUploadLinks);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["https://paste.rs/Xk2"], copied);
        Assert.Equal("Uploaded. Copied to clipboard. Paste the link into your report.", model.LogUploadStatusText);
        Assert.True(Named("CopyLogLinksButton").IsFocused);
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
                .ContinueWith<IReadOnlyList<LogUploadLink>>(
                    _ => throw new LogUploadException("Upload cancelled.", true),
                    TaskScheduler.Default);
        };
        var window = fixture.Settings();
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
