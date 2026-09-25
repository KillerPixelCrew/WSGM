using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using WSGM.DeviceLab.Application;
using WSGM.DeviceLab.Capture.Live;
using WSGM.DeviceLab.Knowledge;
using WSGM.DeviceLab.Transports;
using WSGM.DeviceLab.Wizard;
using WSGM.DeviceLab.Worker;

namespace WSGM.DeviceLab.Gui;

// Shared helpers for the hardware stages: the owner guard, the shared input capture, the confirmed
// knowledge record, and linear "ask the tester" prompts that keep each stage readable as one async
// method.
internal sealed partial class WizardWindow
{
    private LabInputCapture? _capture;
    private LabWorkerClient? _worker;

    /// <summary>
    ///     The running stage's token: cancelled by "Stop and save", Escape, or the window closing. Restore
    ///     paths must not depend on it; they are bounded on their own.
    /// </summary>
    private CancellationToken Lifetime => _stage.Token;

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
        if (_capture is null)
        {
            var windowHandle = TryGetPlatformHandle()?.Handle
                               ?? throw new InvalidOperationException("The Device Lab window has no native handle.");
            LabTrace.Write("input capture: start");
            _capture = await Task.Run(() => LabInputCapture.Start(windowHandle));
            LabTrace.Write("input capture: running");
        }

        return _capture;
    }

    /// <summary>
    ///     The elevated hardware worker, started on first use and stopped when the window closes. Every
    ///     hardware write goes through it, behind its checkpoint handshake.
    /// </summary>
    private async Task<LabWorkerClient> WorkerAsync()
    {
        if (_worker is null)
        {
            LabTrace.Write("hardware worker: start");
            _worker = await Task.Run(LabWorkerClient.Start);
            LabTrace.Write("hardware worker: running");
        }

        return _worker;
    }

    // Cancelling ends only the wait for the controller to come back; the command is never resent.
    private async Task<LabInitResult> SendCuratedInitAsync(string recordId, CancellationToken cancellationToken)
    {
        LabTrace.Write($"controller init {recordId}: send");
        var worker = await WorkerAsync();
        return await Task.Run(() =>
        {
            using var init = worker.Open<ILabCuratedInitWorker>(LabCuratedInitWorker.Service.Name, null, recordId);
            var (_, token) = worker.Checkpoint<int?>(init, _ => _machine.Update(changes => changes with
            {
                CuratedInitRecordId = recordId
            }));
            var result = init.Send(cancellationToken);
            worker.Release(init, token);
            _machine.Update(changes => changes with { CuratedInitRecordId = null });
            return result;
        });
    }

    private async Task<int?> CuratedModeAsync(string recordId)
    {
        var worker = await WorkerAsync();
        return await Task.Run(() =>
        {
            using var init = worker.Open<ILabCuratedInitWorker>(LabCuratedInitWorker.Service.Name, null, recordId);
            return init.CurrentMode();
        });
    }

    private async Task<string?> RecoverControllerInitAsync(CancellationToken cancellationToken)
    {
        var modes = await Task.Run(() => LabModeCommands.HasPending ? LabModeCommands.RecoverPending() : null);
        if (!LabControllerInit.HasControllerModePending)
        {
            if (_machine.Read().CuratedInitRecordId is null)
            {
                return modes;
            }

            // Nothing records what the setup changed, so it cannot be undone or checked: report it once
            // and forget it rather than warn on every start. It is never resent.
            await Task.Run(() => _machine.Update(changes => changes with { CuratedInitRecordId = null }));
            const string unknown =
                "A controller setup stopped before its result was known. Check the OEM button layout.";
            return modes is null ? unknown : $"{unknown} {modes}";
        }

        var worker = await WorkerAsync();
        var restored = await Task.Run(() =>
        {
            using var init = worker.Open<ILabCuratedInitWorker>(LabCuratedInitWorker.Service.Name, null,
                (string?)null);
            var (_, token) = worker.Checkpoint<int?>(init, _ => { });
            var problem = init.RecoverControllerMode(cancellationToken);
            if (problem is null)
            {
                worker.Release(init, token);
                _machine.Update(changes => changes with { CuratedInitRecordId = null });
            }

            return problem;
        });
        return modes is null ? restored : restored is null ? modes : $"{restored} {modes}";
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
