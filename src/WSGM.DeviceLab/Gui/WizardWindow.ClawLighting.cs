using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using WSGM.DeviceLab.Transports;
using WSGM.DeviceLab.Wizard;

namespace WSGM.DeviceLab.Gui;

internal sealed partial class WizardWindow
{
    private async Task<bool> RunClawLightingAsync(StackPanel page, LabPowerPlan plan, LabPowerLog log,
        List<(string Shown, string Seen)> lighting)
    {
        if (plan.Record?.Id != "wsgm.claw-8-a2vm")
        {
            return false;
        }

        page.Children.Add(Heading("Claw RGB lighting"));
        page.Children.Add(
            Status("Test red, green and blue on the rings and buttons, then restore your original lighting."));
        if (await AskAsync(page, "Test RGB", "Skip") == 1)
        {
            return true;
        }

        var worker = await WorkerAsync();
        using var rgb = await Task.Run(() =>
            worker.Open<ILabClawLighting>(LabClawLighting.Service.Name, log, plan.Record.Id));
        var (original, token) = await Task.Run(() => worker.Checkpoint<byte[]>(rgb, profile =>
            LabPowerRecovery.Record(_machine, plan.Record.Id, changes => changes with { ClawLighting = profile })));
        try
        {
            (string Name, byte Red, byte Green, byte Blue)[] colours =
            [
                ("red", 255, 0, 0), ("green", 0, 255, 0), ("blue", 0, 0, 255)
            ];
            foreach (var colour in colours)
            {
                var profile = LabClawLighting.Colour(original, colour.Red, colour.Green, colour.Blue);
                if (!await Task.Run(() => rgb.Apply(profile)))
                {
                    throw new IOException("The Claw RGB profile did not match after writing it.");
                }

                page.Children.Add(Status($"The rings and buttons should now be {colour.Name}."));
                var answer = await AskAsync(page, "Correct colour", "Wrong colour", "No change");
                lighting.Add(($"claw:{colour.Name}",
                    answer switch { 0 => "matched", 1 => "wrong colour", _ => "no change" }));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            lighting.Add(("claw RGB", $"failed: {ex.Message}"));
            page.Children.Add(Warning(ex.Message));
        }
        finally
        {
            try
            {
                if (await Task.Run(() => rgb.Apply(original)))
                {
                    _machine.Update(changes => changes with { Power = changes.Power! with { ClawLighting = null } });
                    await ReleaseQuietlyAsync(worker, rgb, token);
                }
                else
                {
                    throw new IOException("The original Claw RGB profile did not read back.");
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                lighting.Add(("claw RGB restore", $"failed: {ex.Message}"));
                page.Children.Add(Warning($"RGB restoration failed: {ex.Message}"));
            }
        }

        return true;
    }
}
