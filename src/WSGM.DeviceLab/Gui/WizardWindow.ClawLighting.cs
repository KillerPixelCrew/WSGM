using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using WSGM.DeviceLab.Transports;
using WSGM.DeviceLab.Wizard;

namespace WSGM.DeviceLab.Gui;

// The Claw RGB test: the worker records the exact original profile before the first write, and the
// original is written back and read back on every exit path, Stop and window close included.
internal sealed partial class WizardWindow
{
    private async Task<bool> RunClawLightingAsync(StackPanel page, LabPowerPlan plan, LabPowerLog log,
        List<(string Shown, string Seen)> lighting)
    {
        if (plan.ClawLighting is null || plan.Record is not { } record)
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
        ILabClawLighting rgb;
        try
        {
            rgb = await Task.Run(() => worker.Open<ILabClawLighting>(LabClawLighting.Service.Name, log, record.Id));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or Win32Exception)
        {
            log.Add("claw-lighting-open-failed", ex.Message);
            page.Children.Add(Muted("The Claw lighting interface was not found, so it was not tested."));
            return false;
        }

        using (rgb)
        {
            byte[] original;
            string token;
            try
            {
                (original, token) = await Task.Run(() => worker.Checkpoint<byte[]>(rgb, profile =>
                    LabPowerRecovery.Record(_machine, record.Id, changes => changes with { ClawLighting = profile })));
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                page.Children.Add(Warning($"The lighting test did not start: {ex.Message}"));
                return true;
            }

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
                    var answer = (ClawColourAnswer)await AskAsync(page, "Correct colour", "Wrong colour", "No change");
                    lighting.Add(($"claw:{colour.Name}", answer switch
                    {
                        ClawColourAnswer.Correct => "matched",
                        ClawColourAnswer.Wrong => "wrong colour",
                        _ => "no change"
                    }));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                lighting.Add(("claw RGB", $"failed: {ex.Message}"));
                page.Children.Add(Warning(ex.Message));
            }
            finally
            {
                // The exact original, written and read back without the stage token, so Stop restores it too.
                // The record is cleared and the checkpoint released only after that readback matched.
                var restored = false;
                try
                {
                    restored = await Task.Run(() => rgb.Apply(original));
                    if (!restored)
                    {
                        throw new IOException("The original Claw RGB profile did not read back.");
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    lighting.Add(("claw RGB restore", $"failed: {ex.Message}"));
                    page.Children.Add(Warning($"RGB restoration failed: {ex.Message}"));
                }

                if (restored)
                {
                    await ForgetRecordedAsync(changes => changes with { ClawLighting = null });
                    await ReleaseQuietlyAsync(worker, rgb, token);
                }
            }
        }

        return true;
    }

    // The colour question's answers, in the order of its labels.
    private enum ClawColourAnswer
    {
        Correct,
        Wrong,
        NoChange
    }
}
