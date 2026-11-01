using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Settings;

public sealed partial class SettingsViewModel
{
    // Ends a running upload when the window closes: an upload nobody can see finish leaves a link nobody can copy.
    private readonly CancellationTokenSource _logUploadLifetime = new();
    private CancellationTokenSource? _logUpload;
    private IReadOnlyList<LogUploadFile> _logUploadFiles = [];

    /// <summary>Gets or sets the window's clipboard, which the link is copied to; null where there is none.</summary>
    internal Func<string, Task>? Clipboard { get; set; }

    /// <summary>Lists the log files and asks for confirmation before uploading them.</summary>
    public AsyncRelayCommand UploadLogCommand => field ??= new AsyncRelayCommand(PrepareLogUploadAsync);

    /// <summary>Uploads the confirmed files.</summary>
    public AsyncRelayCommand ConfirmLogUploadCommand => field ??= new AsyncRelayCommand(UploadLogAsync);

    /// <summary>Withdraws the confirmation, or stops a running upload.</summary>
    public RelayCommand CancelLogUploadCommand => field ??= new RelayCommand(CancelLogUpload);

    /// <summary>Copies the link again.</summary>
    public AsyncRelayCommand CopyLogLinkCommand => field ??= new AsyncRelayCommand(CopyLogLinkAsync);

    /// <summary>Gets what the upload sends and what stays in it, while it waits for confirmation.</summary>
    public string LogUploadConfirmText
    {
        get;
        private set => SetFieldIfChanged(ref field, value, nameof(LogUploadConfirmText));
    } = "";

    /// <summary>Gets whether the upload waits for the user's confirmation.</summary>
    public bool LogUploadConfirmPending
    {
        get;
        private set
        {
            if (SetFieldIfChanged(ref field, value, nameof(LogUploadConfirmPending)))
            {
                RaiseLogUploadActions();
            }
        }
    }

    /// <summary>Gets whether files are being listed or uploaded.</summary>
    public bool LogUploadBusy
    {
        get;
        private set
        {
            if (SetFieldIfChanged(ref field, value, nameof(LogUploadBusy)))
            {
                RaiseLogUploadActions();
            }
        }
    }

    /// <summary>Gets the upload's progress, outcome or failure.</summary>
    public string LogUploadStatusText
    {
        get;
        private set
        {
            if (SetFieldIfChanged(ref field, value, nameof(LogUploadStatusText)))
            {
                Raise(nameof(HasLogUploadStatus));
            }
        }
    } = "";

    /// <summary>Gets whether there is a status to show.</summary>
    public bool HasLogUploadStatus => LogUploadStatusText.Length > 0;

    /// <summary>Gets whether the status reports a failure.</summary>
    public bool LogUploadProblem
    {
        get;
        private set => SetFieldIfChanged(ref field, value, nameof(LogUploadProblem));
    }

    /// <summary>Gets the link that shows the uploaded files, exactly as it is copied.</summary>
    public string LogUploadLink
    {
        get;
        private set
        {
            if (SetFieldIfChanged(ref field, value, nameof(LogUploadLink)))
            {
                Raise(nameof(HasLogUploadLink));
                RaiseLogUploadActions();
            }
        }
    } = "";

    /// <summary>Gets whether an upload finished with a link.</summary>
    public bool HasLogUploadLink => LogUploadLink.Length > 0;

    /// <summary>Gets whether the confirmation or a running upload can be cancelled.</summary>
    public bool CanCancelLogUpload => LogUploadConfirmPending || LogUploadBusy;

    /// <summary>Gets whether the finished upload's link can be copied again.</summary>
    public bool CanCopyLogLink => HasLogUploadLink && !LogUploadBusy && !LogUploadConfirmPending;

    /// <summary>Gets whether the row shows any upload action.</summary>
    public bool HasLogUploadActions => CanCancelLogUpload || CanCopyLogLink;

    /// <summary>Stops a running upload; called once when the window closes.</summary>
    internal void StopLogUpload()
    {
        _logUploadLifetime.Cancel();
    }

    private void RaiseLogUploadActions()
    {
        Raise(nameof(CanCancelLogUpload));
        Raise(nameof(CanCopyLogLink));
        Raise(nameof(HasLogUploadActions));
    }

    /// <summary>Lists the log files on a worker and asks for confirmation, or says there is no log.</summary>
    /// <returns>A task completing once the confirmation or the reason shows.</returns>
    internal async Task PrepareLogUploadAsync()
    {
        if (LogUploadBusy)
        {
            return;
        }

        LogUploadBusy = true;
        LogUploadConfirmPending = false;
        LogUploadProblem = false;
        LogUploadStatusText = "";
        try
        {
            _logUploadFiles = await Task.Run(_services.ListLogs);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _services.Report("Log upload: listing the log files failed", exception);
            _logUploadFiles = [];
        }
        finally
        {
            LogUploadBusy = false;
        }

        if (_logUploadFiles.Count == 0)
        {
            LogUploadProblem = true;
            LogUploadStatusText = "WSGM has not written a log yet, so there is nothing to upload.";
            return;
        }

        LogUploadConfirmText = DescribeLogUpload(_logUploadFiles);
        LogUploadConfirmPending = true;
    }

    /// <summary>Uploads the confirmed files and copies their link, or shows why it did not finish.</summary>
    /// <returns>A task completing once the outcome shows.</returns>
    internal async Task UploadLogAsync()
    {
        if (!LogUploadConfirmPending || LogUploadBusy)
        {
            return;
        }

        LogUploadConfirmPending = false;
        LogUploadBusy = true;
        LogUploadProblem = false;
        LogUploadLink = "";
        LogUploadStatusText = "Uploading…";
        using var upload = CancellationTokenSource.CreateLinkedTokenSource(_logUploadLifetime.Token);
        _logUpload = upload;
        Progress<LogUploadProgress> progress = new(step =>
        {
            // A report queued behind the upload's end must not replace its outcome.
            if (ReferenceEquals(_logUpload, upload))
            {
                LogUploadStatusText = $"Uploading {step.FileName}…";
            }
        });
        string? link = null;
        string? failure = null;
        var cancelled = false;
        try
        {
            link = await _services.UploadLogs(_logUploadFiles, progress, upload.Token);
        }
        catch (LogUploadException exception)
        {
            failure = exception.Message;
            cancelled = exception.Cancelled;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _services.Report("Log upload failed", exception);
            failure = "The upload failed: " + exception.Message;
        }
        finally
        {
            _logUpload = null;
            LogUploadBusy = false;
        }

        if (link is null)
        {
            LogUploadProblem = !cancelled;
            LogUploadStatusText = failure ?? "The upload failed.";
            return;
        }

        LogUploadLink = link;
        await CopyLogLinkAsync();
    }

    /// <summary>Stops a running upload, or withdraws the confirmation.</summary>
    internal void CancelLogUpload()
    {
        if (_logUpload is { } upload)
        {
            LogUploadStatusText = "Cancelling…";
            upload.Cancel();
            return;
        }

        LogUploadConfirmPending = false;
    }

    /// <summary>Copies the link to the window's clipboard and says whether that worked.</summary>
    /// <returns>A task completing once the copy finished.</returns>
    internal async Task CopyLogLinkAsync()
    {
        if (!HasLogUploadLink)
        {
            return;
        }

        if (Clipboard is not { } clipboard)
        {
            LogUploadProblem = true;
            LogUploadStatusText = "Uploaded. No clipboard is available here, so copy the link below.";
            return;
        }

        try
        {
            await clipboard(LogUploadLink);
            LogUploadProblem = false;
            LogUploadStatusText = "Uploaded. Copied to clipboard. Paste the link into your report.";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _services.Report("Log upload: copying the link failed", exception);
            LogUploadProblem = true;
            LogUploadStatusText =
                $"Uploaded. Could not copy to the clipboard ({exception.Message}), so copy the link below.";
        }
    }

    private static string DescribeLogUpload(IReadOnlyList<LogUploadFile> files)
    {
        var named = string.Join(" and ", files.Select(file =>
            file.Previous
                ? $"{file.Name}, the previous session ({DescribeSize(file.Bytes)})"
                : $"{file.Name} ({DescribeSize(file.Bytes)})"));
        return $"Upload {named} to filebin.net? Your Windows user folder is replaced with {LogMask.ProfileToken} "
               + $"and your user name with {LogMask.UserToken}. Still included: game names, the device model, file "
               + "paths below your user folder and hardware details. The link expires after about a week, and anyone "
               + "with it can read the log until then.";
    }

    private static string DescribeSize(long bytes)
    {
        return bytes >= 1024 * 1024
            ? (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture) + " MB"
            : $"{Math.Max(1, (bytes + 1023) / 1024)} KB";
    }
}
