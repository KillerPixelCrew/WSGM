using WSGM.Core;
using WSGM.Settings;
using WSGM.Testing;

namespace WSGM.Tests.Settings;

/// <summary>Settings > Diagnostics > Upload log: confirm, upload off the UI thread, copy the link, or say why not.</summary>
public sealed class SettingsViewModelLogUploadTests
{
    private const string Bin = "https://filebin.net/wsgm-0123456789abcdef0123456789abcdef";
    private static readonly LogUploadFile Live = new("wsgm.log", @"X:\wsgm.log", 1_258_291, false);
    private static readonly LogUploadFile Previous = new("wsgm.old.log", @"X:\wsgm.old.log", 5_242_880, true);

    [Fact]
    public async Task TheConfirmationNamesBothFilesWhatStaysInThemAndWhenTheLinkExpires()
    {
        var model = Model(_ => throw new InvalidOperationException("nothing is uploaded before confirming"));

        await model.PrepareLogUploadAsync();

        Assert.True(model.LogUploadConfirmPending);
        Assert.True(model.CanCancelLogUpload);
        Assert.Contains("Upload wsgm.log (1.2 MB) and wsgm.old.log, the previous session (5.0 MB) to filebin.net?",
            model.LogUploadConfirmText, StringComparison.Ordinal);
        Assert.Contains("%USERPROFILE%", model.LogUploadConfirmText, StringComparison.Ordinal);
        Assert.Contains("<user>", model.LogUploadConfirmText, StringComparison.Ordinal);
        Assert.Contains("game names, the device model, file paths below your user folder and hardware details",
            model.LogUploadConfirmText, StringComparison.Ordinal);
        Assert.EndsWith("The link expires after about a week, and anyone with it can read the log until then.",
            model.LogUploadConfirmText, StringComparison.Ordinal);
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
        Assert.False(model.HasLogUploadLink);
    }

    [Fact]
    public async Task TheBinLinkIsShownAndCopied()
    {
        IReadOnlyList<LogUploadFile>? sent = null;
        var model = Model(files =>
        {
            sent = files;
            return Task.FromResult(Bin);
        });
        List<string> copied = [];
        model.Clipboard = text =>
        {
            copied.Add(text);
            return Task.CompletedTask;
        };
        await model.PrepareLogUploadAsync();

        await model.UploadLogAsync();

        Assert.Equal([Live, Previous], sent);
        Assert.Equal(Bin, model.LogUploadLink);
        Assert.Equal([Bin], copied);
        Assert.False(model.LogUploadProblem);
        Assert.Equal("Uploaded. Copied to clipboard. Paste the link into your report.", model.LogUploadStatusText);
        Assert.True(model.CanCopyLogLink);
        Assert.False(model.CanCancelLogUpload);

        await model.CopyLogLinkAsync();

        Assert.Equal([Bin, Bin], copied);
    }

    [Fact]
    public async Task AFailedClipboardStillLeavesTheLinkToCopyByHand()
    {
        var model = Model(_ => Task.FromResult(Bin));
        model.Clipboard = _ => throw new InvalidOperationException("clipboard busy");
        await model.PrepareLogUploadAsync();

        await model.UploadLogAsync();

        Assert.Equal(Bin, model.LogUploadLink);
        Assert.True(model.LogUploadProblem);
        Assert.Equal("Uploaded. Could not copy to the clipboard (clipboard busy), so copy the link below.",
            model.LogUploadStatusText);
    }

    [Fact]
    public async Task AFailureShowsTheUploadersReason()
    {
        const string reason = "filebin.net refused wsgm.log with HTTP 413 (RequestEntityTooLarge): File too large."
                              + " The partial upload was removed.";
        var model = Model(_ => throw new LogUploadException(reason));
        await model.PrepareLogUploadAsync();

        await model.UploadLogAsync();

        Assert.True(model.LogUploadProblem);
        Assert.False(model.HasLogUploadLink);
        Assert.False(model.LogUploadBusy);
        Assert.Equal(reason, model.LogUploadStatusText);
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

            return Bin;
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

    private static SettingsViewModel Model(Func<IReadOnlyList<LogUploadFile>, Task<string>> upload,
        IReadOnlyList<LogUploadFile>? files = null)
    {
        return Model((list, _) => upload(list), files);
    }

    private static SettingsViewModel Model(Func<IReadOnlyList<LogUploadFile>, CancellationToken, Task<string>> upload,
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
