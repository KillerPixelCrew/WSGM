using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using WSGM.DeviceLab.Knowledge;
using WSGM.DeviceLab.Wizard;

namespace WSGM.DeviceLab.Gui;

// The opt-in mode commands for devices without a curated record (LabModeCommands): each one present is
// described, the tester chooses "Try it" or "Skip", every write goes into the evidence, and whatever can
// be undone is undone when the stage ends, including when it ends early.
internal sealed partial class WizardWindow
{
    // Fills the caller's run, so a stage that ends early can still undo what was already sent.
    private async Task OfferModeCommandsAsync(
        ModeCommandRun run,
        StackPanel page,
        DeviceKnowledgeRecord? record,
        LabModeStages stage)
    {
        var commands = LabModeCommands.For(record, stage);
        if (commands.Count == 0)
        {
            return;
        }

        List<LabModeEvidence> offered = [];
        foreach (var command in commands)
        {
            if (command.Withheld is not null)
            {
                offered.Add(NotSent(command, "withheld", command.Withheld));
                continue;
            }

            // Looked up just before asking: a mode switch before it may have re-enumerated the controller.
            var (hid, problem) = await Task.Run(() => LabModeCommands.Locate(command));
            if (hid is null)
            {
                offered.Add(NotSent(command, "not-present", problem));
                continue;
            }

            page.Children.Clear();
            page.Children.Add(PageTitle(run.Title));
            page.Children.Add(Heading($"{command.Device}: optional setup"));
            page.Children.Add(Status(command.Description));
            page.Children.Add(command.Reversible
                ? Muted("This is optional: nobody has checked this device's setup yet. Handheld Companion sends it.")
                : Warning("This change stays after the test. Choose Skip if you are not sure."));
            if (await AskAsync(page, "Try it", "Skip") != 0)
            {
                offered.Add(NotSent(command, "skipped", null));
                continue;
            }

            var line = Status("Sending...");
            page.Children.Add(line);
            var session = await Task.Run(() => LabModeCommands.Start(command, hid, Lifetime));
            run.Sessions.Add(session);
            offered.Add(session.Evidence());
            line.Text = session.Sent
                ? command.RepeatMs > 0 ? "Sent. It keeps being sent until this stage ends." : "Sent."
                : $"It could not be sent: {session.Problem} The test continues without it.";
            if (!session.Sent)
            {
                await AskAsync(page, "Continue");
            }
        }

        await Task.Run(() => run.Project.WriteEvidence(run.Attempt, "mode-commands", new
        {
            Stage = stage.ToString(),
            Record = record?.Id,
            Commands = offered
        }));
    }

    // Stops repeating and undoes what can be undone, once. Quiet is for the finally path: no page changes
    // and no questions, because the stage may be ending on Stop or on the window closing.
    private async Task EndModeCommandsAsync(ModeCommandRun run, StackPanel page, bool quiet)
    {
        if (run.Ended || run.Sessions.Count == 0)
        {
            run.Ended = true;
            return;
        }

        run.Ended = true;
        TextBlock? line = null;
        if (!quiet && run.Sessions.Any(session => session.RestoreOwed))
        {
            page.Children.Clear();
            page.Children.Add(PageTitle(run.Title));
            line = Status("Putting the controller back the way it was...");
            page.Children.Add(line);
        }

        // Undone in reverse order, so a mode switch is undone after the commands sent in that mode.
        var sessions = Enumerable.Reverse(run.Sessions).ToList();
        var problems = await Task.Run(() => sessions
            .Select(session => (session.Command.Device, Problem: LabModeCommands.Stop(session)))
            .Where(item => item.Problem is not null)
            .Select(item => $"{item.Device}: {item.Problem}")
            .ToList());
        await Task.Run(() => run.Project.WriteEvidence(run.Attempt, "mode-commands-end", new
        {
            Commands = run.Sessions.Select(session => session.Evidence()),
            Note =
                "None of these controllers can report the restored state back; a restore counts once its reports were written.",
            Problems = problems
        }));
        if (quiet || problems.Count == 0 || line is null)
        {
            return;
        }

        line.Text = $"The controller could not be put back: {string.Join(" ", problems)}";
        page.Children.Add(Warning(
            "Restart the device to reset the controller. The next time Device Lab starts it will try again."));
        await AskAsync(page, "Continue");
    }

    private static LabModeEvidence NotSent(LabModeCommand command, string outcome, string? problem)
    {
        return new LabModeEvidence(command.Id, outcome, command.Reversible, command.Readable, null, [], 0, null, null,
            problem);
    }

    // The commands one stage tried, and whether they were already ended.
    private sealed class ModeCommandRun(LabProject project, string attempt, string title)
    {
        public LabProject Project { get; } = project;

        public string Attempt { get; } = attempt;

        public string Title { get; } = title;

        public List<LabModeSession> Sessions { get; } = [];

        public bool Ended { get; set; }
    }
}
