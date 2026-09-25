using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using WSGM.DeviceLab.Knowledge;
using WSGM.DeviceLab.Wizard;
using WSGM.DeviceLab.Worker;
using WSGM.DeviceLab.Capture.Live;

namespace WSGM.DeviceLab.Gui;

// Shared helpers for the hardware stages: the owner guard, the shared input capture, the confirmed
// knowledge record, and linear "ask the tester" prompts that keep each stage readable as one async
// method.
internal sealed partial class WizardWindow
{
    private LabInputCapture? _capture;
    private LabWorkerClient? _worker;

    // Hardware stages refuse to start without the owner reservation preflight takes, so WSGM's device
    // integration can never drive the same hardware at the same time.
    private void RunHardware(StackPanel page, Func<Task> work, bool chained)
    {
        if (_owner is null)
        {
            page.Children.Add(Warning(
                "This step needs \"Get ready\" to have run with WSGM closed. Close WSGM, then run \"Get ready\" again."));
            page.Children.Add(Buttons(Action("Go to Get ready", () => StartStage(LabStages.Preflight))));
            return;
        }

        Run(page, work, chained);
    }

    /// <summary>The shared input capture, started on first use and stopped when the window closes.</summary>
    private async Task<LabInputCapture> CaptureAsync()
    {
        return _capture ??= await Task.Run(LabInputCapture.Start);
    }

    /// <summary>
    ///     The elevated hardware worker, started on first use and stopped when the window closes. Every
    ///     hardware write goes through it, behind its checkpoint handshake.
    /// </summary>
    private async Task<LabWorkerClient> WorkerAsync()
    {
        return _worker ??= await Task.Run(LabWorkerClient.Start);
    }

    /// <summary>The knowledge record the tester confirmed, if any.</summary>
    private static DeviceKnowledgeRecord? ConfirmedRecord(LabProject project)
    {
        var id = project.Manifest.Device.RecordId;
        return id is null
            ? null
            : DeviceKnowledgeBase.Default.Records.FirstOrDefault(record => record.Id == id);
    }

    private static void SkipStage(LabProject project, string id)
    {
        project.BeginAttempt(id, DateTimeOffset.UtcNow);
        project.Finish(id, LabSegmentStatus.Skipped, "Skipped by the tester.", DateTimeOffset.UtcNow);
    }

    /// <summary>Shows buttons and waits for the tester to press one; returns its index.</summary>
    /// <param name="panel">Where the buttons go; they are removed again once one is pressed.</param>
    /// <param name="labels">Button labels.</param>
    private async Task<int> AskAsync(Panel panel, params string[] labels)
    {
        TaskCompletionSource<int> chosen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var row = Buttons([
            .. labels.Select((label, index) => Action(label, () => chosen.TrySetResult(index)))
        ]);
        panel.Children.Add(row);
        await using (Lifetime.Register(() => chosen.TrySetCanceled(Lifetime)))
        {
            try
            {
                return await chosen.Task;
            }
            finally
            {
                panel.Children.Remove(row);
            }
        }
    }

    /// <summary>Shows a countdown line and waits it out.</summary>
    /// <param name="line">Line to update.</param>
    /// <param name="text">Text before the seconds.</param>
    /// <param name="seconds">Seconds.</param>
    private async Task CountdownAsync(TextBlock line, string text, int seconds)
    {
        for (var left = seconds; left > 0; left--)
        {
            line.Text = $"{text} {left}...";
            await Task.Delay(1000, Lifetime);
        }

        line.Text = text;
    }

    /// <summary>Runs an action on the UI thread from a capture or worker thread.</summary>
    private static void OnUi(Action action)
    {
        Dispatcher.UIThread.Post(action);
    }

    /// <summary>
    ///     The running stage's token: cancelled by "Stop and save", Escape, or the window closing. Restore
    ///     paths must not depend on it; they are bounded on their own.
    /// </summary>
    private CancellationToken Lifetime => _stage.Token;

    /// <summary>Shows buttons and waits for the tester, or for <paramref name="elsewhere" /> to end the wait.</summary>
    /// <param name="panel">Where the buttons go.</param>
    /// <param name="elsewhere">Ends the wait with -1, for example when a step advances by itself.</param>
    /// <param name="labels">Button labels.</param>
    private async Task<int> AskAsync(Panel panel, CancellationToken elsewhere, params string[] labels)
    {
        TaskCompletionSource<int> chosen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var row = Buttons([
            .. labels.Select((label, index) => Action(label, () => chosen.TrySetResult(index)))
        ]);
        panel.Children.Add(row);
        await using (Lifetime.Register(() => chosen.TrySetCanceled(Lifetime)))
        await using (elsewhere.Register(() => chosen.TrySetResult(-1)))
        {
            try
            {
                return await chosen.Task;
            }
            finally
            {
                panel.Children.Remove(row);
            }
        }
    }
}
