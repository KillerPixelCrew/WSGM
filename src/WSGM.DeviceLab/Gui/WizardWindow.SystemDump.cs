using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using WSGM.DeviceLab.Wizard;

namespace WSGM.DeviceLab.Gui;

internal sealed partial class WizardWindow
{
    // Read-only: every section reads and records, nothing on the machine is changed. A section that
    // fails is shown as one muted line and recorded; it never fails the stage.
    private async Task RunSystemDumpAsync(LabProject project, StackPanel page)
    {
        page.Children.Add(
            Status("Reading details about this device. Nothing is changed. This can take a minute or two."));
        if (!_options.Elevated)
        {
            page.Children.Add(Muted("Some details need administrator rights and may be missing."));
        }

        var attempt = await Task.Run(() => project.BeginAttempt(LabStages.SystemDump, DateTimeOffset.UtcNow));
        LabSystemDumpContext context = new(project, attempt, _options.Elevated, Lifetime)
        {
            OwnerReserved = _owner is not null
        };
        List<LabSystemDumpSectionResult> results = [];
        foreach (var section in LabSystemDump.Sections)
        {
            Lifetime.ThrowIfCancellationRequested();
            var line = Status($"{section.Title}: reading...");
            page.Children.Add(line);

            // WaitAsync lets closing the window stop waiting for a slow provider; the read-only section
            // then finishes on its own thread.
            var result = await Task.Run(() => LabSystemDump.Run(section, context), Lifetime).WaitAsync(Lifetime);
            results.Add(result);
            line.Text = $"{section.Title}: {result.Summary}";
            if (result.Status is LabSystemDumpSectionStatus.Failed && result.Issues.Count > 0)
            {
                page.Children.Add(Muted($"Could not read this part: {result.Issues[0]}"));
            }
            else if (result.Issues.Count > 0)
            {
                page.Children.Add(Muted(result.Issues.Count == 1
                    ? $"One item could not be read: {result.Issues[0]}"
                    : $"{result.Issues.Count} items could not be read."));
            }
        }

        var summary = LabSystemDump.Summarize(results);
        await Task.Run(() => project.WriteEvidence(attempt, "system-dump",
            LabSystemDump.Report(results, _options.Elevated)));
        page.Children.Add(Heading("Done"));
        page.Children.Add(Status(summary));
        await AskAsync(page, "Continue");
        await Task.Run(() => project.Finish(LabStages.SystemDump, LabSegmentStatus.Completed, summary,
            DateTimeOffset.UtcNow));
        Next(LabStages.SystemDump);
    }
}
