using WSGM.Core;
using WSGM.Settings;
using WSGM.Testing;

namespace WSGM.Tests.Settings;

/// <summary>Settings > System > Upload log: confirm, upload off the UI thread, copy the links, or say why not.</summary>
public sealed class SettingsViewModelLogUploadTests
{
    private static readonly LogUploadFile Live = new("wsgm.log", @"X:\wsgm.log", 1_258_291, false);
    private static readonly LogUploadFile Previous = new("wsgm.old.log", @"X:\wsgm.old.log", 5_242_880, true);

    [Fact]
    public async Task TheConfirmationNamesBothFilesAndWhatStaysInThem()
    {
        var model = Model(_ => throw new InvalidOperationException("nothing is uploaded before confirming"));

        await model.PrepareLogUploadAsync();

        Assert.True(model.LogUploadConfirmPending);
        Assert.True(model.CanCancelLogUpload);
        Assert.Contains("wsgm.log (1.2 MB)", model.LogUploadConfirmText, StringComparison.Ordinal);
        Assert.Contains("wsgm.old.log, the previous session (5.0 MB)", model.LogUploadConfirmText,
            StringComparison.Ordinal);
        Assert.Contains("%USERPROFILE%", model.LogUploadConfirmText, StringComparison.Ordinal);
        Assert.Contains("<user>", model.LogUploadConfirmText, StringComparison.Ordinal);
        Assert.Contains("game names, the device model, file paths below your user folder and hardware details",
            model.LogUploadConfirmText, StringComparison.Ordinal);
        Assert.Contains("Anyone with the link can read it.", model.LogUploadConfirmText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutALogThereIsNothingToConfirm()
    {
        var model = Model(_ => throw new InvalidOperationException("no upload"), []);

        await model.PrepareLogUploadAsync();

        Assert.False(model.LogUploadConfirmPending);
        Assert.True(model.LogUploadProblem);
        Assert.Equal("WSGM has not written a log yet, so there is nothing to upload.", model.LogUploadStatusText);
    }

    [Fact]
    public async Task CancellingTheConfirmationUploadsNothing()
    {
        var model = Model(_ => throw new InvalidOperationException("no upload"));
        await model.PrepareLogUploadAsync();

        model.CancelLogUpload();
        await model.UploadLogAsync();

        Assert.False(model.LogUploadConfirmPending);
        Assert.False(model.HasLogUploadActions);
        Assert.False(model.HasLogUploadLinks);
    }

    [Fact]
    public async Task EveryLinkIsShownAndCopiedOnePerLine()
    {
        IReadOnlyList<LogUploadFile>? sent = null;
        var model = Model(files =>
        {
            sent = files;
            return Task.FromResult<IReadOnlyList<LogUploadLink>>(
            [
                new LogUploadLink("wsgm.log", 1, "https://paste.rs/a"),
                new LogUploadLink("wsgm.log", 2, "https://paste.rs/b"),
                new LogUploadLink("wsgm.old.log", 1, "https://paste.rs/c")
            ]);
        });
        List<string> copied = [];
        model.Clipboard = text =>
        {
            copied.Add(text);
            return Task.CompletedTask;
        };
        await model.PrepareLogUploadAsync();

        await model.UploadLogAsync();

        var expected = string.Join(Environment.NewLine, "https://paste.rs/a", "https://paste.rs/b",
            "https://paste.rs/c");
        Assert.Equal([Live, Previous], sent);
        Assert.Equal(expected, model.LogUploadLinksText);
        Assert.Equal([expected], copied);
        Assert.False(model.LogUploadProblem);
        Assert.Equal("Uploaded in 3 parts. Copied to clipboard. Paste all the links into your report.",
            model.LogUploadStatusText);
        Assert.True(model.CanCopyLogLinks);
        Assert.False(model.CanCancelLogUpload);

        await model.CopyLogLinksAsync();

        Assert.Equal([expected, expected], copied);
    }

    [Fact]
    public async Task AFailedClipboardStillLeavesTheLinksToCopyByHand()
    {
        var model = Model(_ => Task.FromResult<IReadOnlyList<LogUploadLink>>(
            [new LogUploadLink("wsgm.log", 1, "https://paste.rs/a")]));
        model.Clipboard = _ => throw new InvalidOperationException("clipboard busy");
        await model.PrepareLogUploadAsync();

        await model.UploadLogAsync();

        Assert.Equal("https://paste.rs/a", model.LogUploadLinksText);
        Assert.True(model.LogUploadProblem);
        Assert.Equal("Uploaded. Could not copy to the clipboard (clipboard busy), so copy the links below.",
            model.LogUploadStatusText);
    }

    [Fact]
    public async Task AFailureShowsTheUploadersReason()
    {
        var model = Model(_ => throw new LogUploadException(
            "paste.rs is limiting uploads right now (HTTP 429). Wait a few minutes and try again."));
        await model.PrepareLogUploadAsync();

        await model.UploadLogAsync();

        Assert.True(model.LogUploadProblem);
        Assert.False(model.HasLogUploadLinks);
        Assert.False(model.LogUploadBusy);
        Assert.Equal("paste.rs is limiting uploads right now (HTTP 429). Wait a few minutes and try again.",
            model.LogUploadStatusText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellingOrClosingStopsARunningUpload(bool closeWindow)
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = Model(async (_, cancellationToken) =>
        {
            started.SetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException exception)
            {
                throw new LogUploadException("Upload cancelled.", true, exception);
            }

            return [];
        });
        await model.PrepareLogUploadAsync();
        var upload = model.UploadLogAsync();
        await started.Task;
        Assert.True(model.LogUploadBusy);
        Assert.True(model.CanCancelLogUpload);

        if (closeWindow)
        {
            model.StopLogUpload();
        }
        else
        {
            model.CancelLogUpload();
        }

        await upload;
        Assert.False(model.LogUploadBusy);
        Assert.False(model.LogUploadProblem);
        Assert.Equal("Upload cancelled.", model.LogUploadStatusText);
    }

    private static SettingsViewModel Model(
        Func<IReadOnlyList<LogUploadFile>, Task<IReadOnlyList<LogUploadLink>>> upload,
        IReadOnlyList<LogUploadFile>? files = null)
    {
        return Model((list, _) => upload(list), files);
    }

    private static SettingsViewModel Model(
        Func<IReadOnlyList<LogUploadFile>, CancellationToken, Task<IReadOnlyList<LogUploadLink>>> upload,
        IReadOnlyList<LogUploadFile>? files = null)
    {
        AppConfig config = new();
        return new SettingsViewModel(config, SettingsTestServices.Inert(config) with
        {
            ListLogs = () => files ?? [Live, Previous],
            UploadLogs = (list, _, cancellationToken) => upload(list, cancellationToken)
        });
    }
}
